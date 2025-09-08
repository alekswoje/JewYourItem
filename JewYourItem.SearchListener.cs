using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JewYourItem.Utility;
using Newtonsoft.Json;
using JewYourItem.Models;

namespace JewYourItem;

public partial class JewYourItem
{
    private class SearchListener
    {
        private readonly JewYourItem _parent;
        public JewYourItemInstanceSettings Config { get; }
        public ClientWebSocket WebSocket { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public bool IsRunning { get; set; }
        public bool IsConnecting { get; set; } = false;
        public DateTime LastConnectionAttempt { get; set; } = DateTime.MinValue;
        public DateTime LastErrorTime { get; set; } = DateTime.MinValue;
        public int ConnectionAttempts { get; set; } = 0;
        public bool IsAuthenticationError { get; set; } = false;
        public string LastErrorMessage { get; set; } = "";
        private readonly object _connectionLock = new object();
        private StringBuilder _messageBuffer = new StringBuilder();
        private Action<string> _logMessage;
        private Action<string> _logError;
        private readonly Action<string> _logDebug;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;

        public SearchListener(JewYourItem parent, JewYourItemInstanceSettings config, Action<string> logMessage, Action<string> logError, Action<string> logDebug, Action<string> logInfo, Action<string> logWarning)
        {
            _parent = parent;
            Config = config;
            Cts = new CancellationTokenSource();
            _logMessage = logMessage;
            _logError = logError;
            _logDebug = logDebug;
            _logInfo = logInfo;
            _logWarning = logWarning;
        }

        public async void Start(Action<string> logMessage, Action<string> logError, string sessionId)
        {
            // Update logging delegates in case they changed
            _logMessage = logMessage;
            _logError = logError;
            // Note: LogDebug, LogInfo, LogWarning are set in constructor and shouldn't change
            lock (_connectionLock)
            {
                // STRICT CONNECTION SAFETY CHECKS
                if (string.IsNullOrEmpty(Config.League.Value) || string.IsNullOrEmpty(Config.SearchId.Value) || string.IsNullOrEmpty(sessionId))
                {
                    _logError("League, Search ID, or Session ID is empty for this search.");
                    LastErrorTime = DateTime.Now;
                    return;
                }

                // Check if already running or connecting
                if (IsRunning || IsConnecting)
                {
                    _logMessage($"🛡️ CONNECTION BLOCKED: Search {Config.SearchId.Value} already running ({IsRunning}) or connecting ({IsConnecting})");
                    return;
                }

                // Check if WebSocket is still active
                if (WebSocket != null && (WebSocket.State == WebSocketState.Open || WebSocket.State == WebSocketState.Connecting))
                {
                    _logMessage($"🛡️ CONNECTION BLOCKED: WebSocket for {Config.SearchId.Value} still active (State: {WebSocket.State})");
                    return;
                }

                // Rate limit cooldown check - use shorter cooldown for auth errors
                double cooldownSeconds = IsAuthenticationError ? 10 : JewYourItem.RestartCooldownSeconds; // 10 seconds for auth errors, 300 for others
                if ((DateTime.Now - LastErrorTime).TotalSeconds < cooldownSeconds)
                {
                    if (IsAuthenticationError)
                    {
                        _logDebug($"🔐 AUTH ERROR COOLDOWN: Search {Config.SearchId.Value} waiting for session ID fix ({cooldownSeconds - (DateTime.Now - LastErrorTime).TotalSeconds:F1}s remaining)");
                    }
                    else
                    {
                        _logDebug($"🛡️ CONNECTION BLOCKED: Search {Config.SearchId.Value} in rate limit cooldown ({cooldownSeconds - (DateTime.Now - LastErrorTime).TotalSeconds:F1}s remaining)");
                    }
                    return;
                }

                // EMERGENCY: Connection attempt throttling (max 1 attempt per 30 seconds)
                if ((DateTime.Now - LastConnectionAttempt).TotalSeconds < 30)
                {
                    _logDebug($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} connection throttled ({30 - (DateTime.Now - LastConnectionAttempt).TotalSeconds:F1}s remaining)");
                    return;
                }

                // EMERGENCY: Max connection attempts per hour (extremely limited)
                if (ConnectionAttempts >= 3 && (DateTime.Now - LastConnectionAttempt).TotalHours < 1)
                {
                    _logDebug($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} exceeded max connection attempts (3 per HOUR)");
                    return;
                }

                // EMERGENCY: Global session connection limit
                if (ConnectionAttempts >= 10)
                {
                    _logDebug($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} exceeded TOTAL session limit (10 attempts EVER)");
                    return;
                }

                // Reset connection attempts counter if more than 1 HOUR has passed
                if ((DateTime.Now - LastConnectionAttempt).TotalHours >= 1)
                {
                    ConnectionAttempts = Math.Min(ConnectionAttempts, 5); // Partial reset, keep some penalty
                }

                // EMERGENCY: Check global limits (allow more attempts during startup)
                JewYourItem._globalConnectionAttempts++;
                
                // Calculate time since plugin start to allow generous startup period
                var timeSinceStart = DateTime.Now - JewYourItem._pluginStartTime;
                bool isInitialStartup = timeSinceStart.TotalMinutes < 2; // 2 minute startup grace period
                bool isFirstConnectionAttempt = ConnectionAttempts <= 1;
                
                // During startup: Allow up to 25 total attempts (for 20+ listeners + some retries)
                // After startup: Allow only 3 attempts (for retry protection)
                int globalLimit = isInitialStartup ? 25 : 3;
                
                if (JewYourItem._globalConnectionAttempts > globalLimit)
                {
                    if (isInitialStartup)
                    {
                        _logMessage($"🚨🚨🚨 STARTUP EMERGENCY SHUTDOWN: Too many startup attempts ({JewYourItem._globalConnectionAttempts}/{globalLimit})");
                    }
                    else
                    {
                        _logMessage($"🚨🚨🚨 GLOBAL EMERGENCY SHUTDOWN: Too many retry attempts ({JewYourItem._globalConnectionAttempts}/{globalLimit})");
                    }
                    JewYourItem._emergencyShutdown = true;
                    return;
                }

                // Set connection state flags
                IsConnecting = true;
                LastConnectionAttempt = DateTime.Now;
                ConnectionAttempts++;
                
                _logDebug($"🔌 STARTING CONNECTION: Search {Config.SearchId.Value} (Attempt #{ConnectionAttempts}, Global: {JewYourItem._globalConnectionAttempts})");
            }
            try
            {
                // REMOVED: Rate limiting for WebSocket connections to make startup instant
                
                // Dispose any existing WebSocket
                if (WebSocket != null)
                {
                    try
                    {
                        WebSocket.Dispose();
                    }
                    catch { /* Ignore disposal errors */ }
                    WebSocket = null;
                }

                WebSocket = new ClientWebSocket();
                var cookie = $"POESESSID={sessionId}";
                WebSocket.Options.SetRequestHeader("Cookie", cookie);
                WebSocket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
                WebSocket.Options.SetRequestHeader("Origin", "https://www.pathofexile.com");
                WebSocket.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
                WebSocket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
                WebSocket.Options.SetRequestHeader("Pragma", "no-cache");
                WebSocket.Options.SetRequestHeader("Cache-Control", "no-cache");
                WebSocket.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate; client_max_window_bits");
                
                string url = $"wss://www.pathofexile.com/api/trade2/live/poe2/{Uri.EscapeDataString(Config.League.Value)}/{Config.SearchId.Value}";
                
                _logMessage($"🔌 CONNECTING: {Config.SearchId.Value} to {url}");
                await WebSocket.ConnectAsync(new Uri(url), Cts.Token);

                // Only increment global counter AFTER successful connection
                JewYourItem._globalConnectionAttempts++;

                lock (_connectionLock)
                {
                    IsConnecting = false;
                    IsRunning = true;
                }
                
                // Clear any previous authentication errors on successful connection
                IsAuthenticationError = false;
                LastErrorMessage = "";
                
                _logMessage($"✅ CONNECTED: WebSocket for search {Config.SearchId.Value}");
                _parent._activeListener = this;
                _ = ReceiveMessagesAsync(_logMessage, _logError, sessionId);
            }
            catch (Exception ex)
            {
                lock (_connectionLock)
                {
                    IsConnecting = false;
                    IsRunning = false;
                }
                
                // Check for authentication errors (401)
                bool isAuthError = ex.Message.Contains("401");
                IsAuthenticationError = isAuthError;
                LastErrorMessage = ex.Message;
                
                // Debug logging to help troubleshoot
                _logDebug($"🔍 ERROR ANALYSIS: Message='{ex.Message}', IsAuthError={isAuthError}");
                
                if (isAuthError)
                {
                    _logError($"🔐 AUTHENTICATION FAILED: Search {Config.SearchId.Value} - Invalid session ID! Please update your POESESSID in settings.");
                    _logError($"💡 TIP: Get your session ID from browser cookies on pathofexile.com");
                }
                else
                {
                    _logError($"❌ CONNECTION FAILED: Search {Config.SearchId.Value}: {ex.Message}");
                }
                
                LastErrorTime = DateTime.Now;
                
                // Clean up WebSocket on failure
                if (WebSocket != null)
                {
                    try
                    {
                        WebSocket.Dispose();
                    }
                    catch { /* Ignore disposal errors */ }
                    WebSocket = null;
                }
            }
        }

        private async Task ReceiveMessagesAsync(Action<string> logMessage, Action<string> logError, string sessionId)
        {
            var buffer = new byte[1024 * 4];
            // CRITICAL: Add null checks to prevent crashes during cleanup
            while (WebSocket != null && WebSocket.State == WebSocketState.Open && Cts != null && !Cts.Token.IsCancellationRequested)
            {
                try
                {
                    var memoryStream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        var bufferSegment = new ArraySegment<byte>(buffer);
                        result = await WebSocket.ReceiveAsync(bufferSegment, Cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            lock (_connectionLock)
                            {
                                IsRunning = false;
                                IsConnecting = false;
                            }
                            logMessage($"🔌 DISCONNECTED: WebSocket closed by server for {Config.SearchId.Value}");
                            LastErrorTime = DateTime.Now;
                            return;
                        }
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            continue;
                        }
                        memoryStream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(memoryStream, Encoding.UTF8))
                    {
                        string fullMessage = await reader.ReadToEndAsync();
                        fullMessage = CleanMessage(fullMessage, logMessage, logError);
                        await ProcessMessage(fullMessage, logMessage, logError, sessionId);
                    }
                }
                catch (Exception ex)
                {
                    lock (_connectionLock)
                    {
                        IsRunning = false;
                        IsConnecting = false;
                    }
                    logError($"❌ WEBSOCKET ERROR: {Config.SearchId.Value}: {ex.Message}");
                    LastErrorTime = DateTime.Now;
                    break;
                }
            }
            
            lock (_connectionLock)
            {
                IsRunning = false;
                IsConnecting = false;
            }
        }

        private string CleanMessage(string message, Action<string> logMessage, Action<string> logError)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            message = message.Trim('\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060');
            message = message.Trim();
            
            int jsonStart = message.IndexOf('{');
            if (jsonStart > 0)
            {
                message = message.Substring(jsonStart);
            }
            else if (jsonStart == -1)
            {
                logError($"No '{{' found in message! Message: '{message}'");
                LastErrorTime = DateTime.Now;
                return message;
            }
            
            if (message.Length > 0 && (char.IsControl(message[0]) || message[0] > 127))
            {
                var cleanBytes = new List<byte>();
                foreach (char c in message)
                {
                    if (c >= 32 && c <= 126)
                    {
                        cleanBytes.Add((byte)c);
                    }
                    else if (c == '\n' || c == '\r' || c == '\t')
                    {
                        cleanBytes.Add((byte)c);
                    }
                }
                message = Encoding.UTF8.GetString(cleanBytes.ToArray());
            }
            
            return message;
        }

        private string ExtractCompleteJson(string message)
        {
            if (string.IsNullOrEmpty(message))
                return null;

            int braceCount = 0;
            int startIndex = -1;
            
            for (int i = 0; i < message.Length; i++)
            {
                if (message[i] == '{')
                {
                    if (startIndex == -1)
                        startIndex = i;
                    braceCount++;
                }
                else if (message[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0 && startIndex != -1)
                    {
                        string json = message.Substring(startIndex, i - startIndex + 1);
                        return json;
                    }
                }
            }
            
            return null;
        }

        private async Task ProcessMessage(string message, Action<string> logMessage, Action<string> logError, string sessionId)
        {
            try
            {
                string cleanMessage = CleanMessage(message, logMessage, logError);
                
                try
                {
                    var wsResponse = JsonConvert.DeserializeObject<WsResponse>(cleanMessage);
                    if (wsResponse.New != null && wsResponse.New.Length > 0)
                    {
                        // Ensure rate limiter is initialized
                        if (_parent._rateLimiter == null)
                        {
                            _parent._rateLimiter = new ConservativeRateLimiter(_parent.LogMessage, _parent.LogError);
                        }

                        // Split items into batches of 10 (API limit)
                        const int maxItemsPerBatch = 10;
                        var itemBatches = new List<string[]>();
                        
                        for (int i = 0; i < wsResponse.New.Length; i += maxItemsPerBatch)
                        {
                            var batch = wsResponse.New.Skip(i).Take(maxItemsPerBatch).ToArray();
                            itemBatches.Add(batch);
                        }

                        logMessage($"📦 PROCESSING {wsResponse.New.Length} items in {itemBatches.Count} batches (max {maxItemsPerBatch} per batch)");

                        // Process each batch separately
                        foreach (var batch in itemBatches)
                        {
                            // REMOVED: Rate limiting for batch requests to make auto TP instant
                            string ids = string.Join(",", batch);
                            string fetchUrl = $"https://www.pathofexile.com/api/trade2/fetch/{ids}?query={Config.SearchId.Value}&realm=poe2";
                            
                            logMessage($"🔍 FETCHING batch of {batch.Length} items: {ids}");

                            using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
                            {
                                var cookie = $"POESESSID={sessionId}";
                                request.Headers.Add("Cookie", cookie);
                                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
                                request.Headers.Add("Accept", "*/*");
                                request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
                                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                                request.Headers.Add("Priority", "u=1, i");
                                request.Headers.Add("Referer", $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(Config.League.Value)}/{Config.SearchId.Value}/live");
                                request.Headers.Add("Sec-Ch-Ua", "\"Not;A=Brand\";v=\"99\", \"Google Chrome\";v=\"139\", \"Chromium\";v=\"139\"");
                                request.Headers.Add("Sec-Ch-Ua-Arch", "x86");
                                request.Headers.Add("Sec-Ch-Ua-Bitness", "64");
                                request.Headers.Add("Sec-Ch-Ua-Full-Version", "139.0.7258.157");
                                request.Headers.Add("Sec-Ch-Ua-Full-Version-List", "\"Not;A=Brand\";v=\"99.0.0.0\", \"Google Chrome\";v=\"139.0.7258.157\", \"Chromium\";v=\"139.0.7258.157\"");
                                request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
                                request.Headers.Add("Sec-Ch-Ua-Model", "");
                                request.Headers.Add("Sec-Ch-Ua-Platform", "Windows");
                                request.Headers.Add("Sec-Ch-Ua-Platform-Version", "19.0.0");
                                request.Headers.Add("Sec-Fetch-Dest", "empty");
                                request.Headers.Add("Sec-Fetch-Mode", "cors");
                                request.Headers.Add("Sec-Fetch-Site", "same-origin");
                                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                                using (var response = await _httpClient.SendAsync(request))
                                {
                                    // Handle rate limiting
                                    if (_parent._rateLimiter != null)
                                    {
                                        var rateLimitWaitTime = await _parent._rateLimiter.HandleRateLimitResponse(response);
                                        if (rateLimitWaitTime > 0)
                                        {
                                            return; // Rate limited, wait and return
                                        }
                                    }

                                    if (response.IsSuccessStatusCode)
                                    {
                                        string content = await response.Content.ReadAsStringAsync();
                                        ItemFetchResponse itemResponse = null;
                                        
                                        try
                                        {
                                            itemResponse = JsonConvert.DeserializeObject<ItemFetchResponse>(content);
                                        }
                                        catch (JsonException ex)
                                        {
                                            logError($"JSON parsing failed: {ex.Message}");
                                            logError($"Response content: {content}");
                                            continue; // Skip this batch and try the next one
                                        }
                                        
                                        if (itemResponse?.Result != null && itemResponse.Result.Any())
                                        {
                                            logMessage($"✅ BATCH SUCCESS: Received {itemResponse.Result.Length} items from batch");
                                            foreach (var item in itemResponse.Result)
                                            {
                                                string name = item.Item.Name;
                                                if (string.IsNullOrEmpty(name))
                                                {
                                                    name = item.Item.TypeLine;
                                                }
                                                string price = item.Listing?.Price != null ? $"{item.Listing.Price.Type} {item.Listing.Price.Amount} {item.Listing.Price.Currency}" : "No price";
                                                int x = item.Listing.Stash?.X ?? 0;
                                                int y = item.Listing.Stash?.Y ?? 0;
                                                logMessage($"{name} - {price} at ({x}, {y})");
                                                
                                                // Parse token expiration times
                                                var (issuedAt, expiresAt) = RecentItem.ParseTokenTimes(item.Listing.HideoutToken);
                                                
                                                var recentItem = new RecentItem
                                                {
                                                    Name = name,
                                                    Price = price,
                                                    HideoutToken = item.Listing.HideoutToken,
                                                    ItemId = item.Id,
                                                    X = x,
                                                    Y = y,
                                                    AddedTime = DateTime.Now,
                                                    TokenIssuedAt = issuedAt,
                                                    TokenExpiresAt = expiresAt
                                                };
                                                
                                                lock (_parent._recentItemsLock)
                                                {
                                                    _parent._recentItems.Enqueue(recentItem);
                                                    while (_parent._recentItems.Count > _parent.Settings.MaxRecentItems.Value)
                                                        _parent._recentItems.Dequeue();
                                                }
                                                if (_parent._playSound)
                                                {
                                                    logMessage("Attempting to play sound...");
                                                    try
                                                    {
                                                        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                                                        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
                                                        
                                                        int randomNumber = _random.Next(1, 10001); // 1 in 10,000 chance
                                                        bool playRareSound = randomNumber == 1;
                                                        string soundFileName = playRareSound ? "pulserare.wav" : "pulse.wav";
                                                        
                                                        // Debug logging for rare sound testing
                                                        logMessage($"🎲 SOUND DEBUG: Random={randomNumber}, PlayRare={playRareSound}, File={soundFileName}");
                                                        
                                                        if (playRareSound)
                                                        {
                                                            logMessage("🎉 WHAT ARE YOU DOING STEP BRO! 🎉 (1 in 10,000 chance)");
                                                        }
                                                        
                                                        var possiblePaths = new[]
                                                        {
                                                            Path.Combine(assemblyDir, "sound", soundFileName),
                                                            Path.Combine("sound", soundFileName),
                                                            Path.Combine(assemblyDir, "..", "sound", soundFileName),
                                                            Path.Combine(assemblyDir, "..", "..", "sound", soundFileName),
                                                            Path.Combine(assemblyDir, "..", "..", "..", "Source", "JewYourItem", "sound", soundFileName),
                                                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sound", soundFileName),
                                                            Path.Combine(Directory.GetCurrentDirectory(), "sound", soundFileName),
                                                            // Additional paths for development/compilation scenarios
                                                            Path.Combine(assemblyDir.Replace("Temp", "Source"), "sound", soundFileName),
                                                            Path.Combine(assemblyDir, "..", "..", "Source", "JewYourItem", "sound", soundFileName),
                                                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ExileCore2", "Plugins", "Source", "JewYourItem", "sound", soundFileName)
                                                        };
                                                        string soundPath = null;
                                                        foreach (var path in possiblePaths)
                                                        {
                                                            if (File.Exists(path))
                                                            {
                                                                soundPath = path;
                                                                break;
                                                            }
                                                        }
                                                        if (soundPath != null)
                                                        {
                                                            logMessage($"Playing sound file: {soundFileName}...");
                                                            await _parent.PlaySoundWithNAudio(soundPath, logMessage, logError);
                                                            logMessage("Sound played successfully!");
                                                        }
                                                        else
                                                        {
                                                            logError("Sound file not found in any of the expected locations:");
                                                            foreach (var path in possiblePaths)
                                                            {
                                                                logError($"  - {path}");
                                                            }
                                                        }
                                                    }
                                                    catch (Exception soundEx)
                                                    {
                                                        logError($"Sound playback failed: {soundEx.Message}");
                                                        logError($"Sound exception details: {soundEx}");
                                                        LastErrorTime = DateTime.Now;
                                                    }
                                                }
                                                if (_parent.Settings.AutoTp.Value && _parent.GameController.Area.CurrentArea.IsHideout)
                                                {
                                                    // Check if game is loading before attempting auto TP
                                                    if (_parent.GameController.IsLoading)
                                                    {
                                                        logMessage("⏳ Auto TP skipped: Game is loading");
                                                    }
                                                    else if (!_parent.GameController.InGame)
                                                    {
                                                        logMessage("🚫 Auto TP skipped: Not in valid game state");
                                                    }
                    // Check for 2-second delay after area change
                    else if ((DateTime.Now - _parent._lastTeleportTime).TotalSeconds < 2.0)
                    {
                        logMessage("⏳ Auto TP skipped: 2 second delay after area change to allow item purchase");
                    }
                                                    else
                                                    {
                                                        _parent.TravelToHideout();
                                                        lock (_parent._recentItemsLock)
                                                        {
                                                            _parent._recentItems.Clear();
                                                        }
                                                        _parent._lastTpTime = DateTime.Now;
                                                        logMessage("Auto TP executed due to new search result.");
                                                    }
                                                }
                                                // REMOVED: Mouse movement should only happen after manual teleports, not when auto TP is blocked by cooldown
                                            }
                                        }
                                        else
                                        {
                                            logMessage($"⚠️ BATCH EMPTY: No items returned from batch");
                                        }
                                    }
                                    else
                                    {
                                        string errorMessage = await response.Content.ReadAsStringAsync();
                                        LastErrorTime = DateTime.Now;
                                        // Rate limiting is now handled above
                                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && errorMessage.Contains("Resource not found; Item no longer available"))
                                        {
                                            logMessage($"⚠️ BATCH WARNING: Items unavailable in batch: {errorMessage}");
                                        }
                                        else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorMessage.Contains("Invalid query"))
                                        {
                                            logMessage($"⚠️ BATCH WARNING: Invalid query for batch: {errorMessage}");
                                            lock (_parent._recentItemsLock)
                                            {
                                                if (_parent._recentItems.Count > 0)
                                                {
                                                    _parent._recentItems.Dequeue();
                                                }
                                            }
                                        }
                                        else
                                        {
                                            logError($"❌ BATCH FAILED: {response.StatusCode} - {errorMessage}");
                                            break; // Stop processing remaining batches on serious error
                                        }
                                    }
                                } // End of using (var response = await _httpClient.SendAsync(request))
                            } // End of using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
                        } // End of batch processing foreach loop
                    } // End of if (wsResponse.New != null && wsResponse.New.Length > 0)
                }
                catch (JsonException parseEx)
                {
                    logError($"JSON parsing failed: {parseEx.Message}");
                    LastErrorTime = DateTime.Now;
                }
            } // End of outer try block
            catch (JsonException jsonEx)
            {
                logError($"JSON parsing failed: {jsonEx.Message}");
                LastErrorTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                logError($"Processing failed: {ex.Message}");
                LastErrorTime = DateTime.Now;
            }
        }

        public void Stop()
        {
            lock (_connectionLock)
            {
                _logMessage($"🛑 STOPPING: Search {Config.SearchId.Value} (Running: {IsRunning}, Connecting: {IsConnecting})");
                
                IsRunning = false;
                IsConnecting = false;
                
                // Clear logging delegates to prevent any delayed callbacks
                _logMessage = (msg) => { }; // No-op logger
                _logError = (msg) => { }; // No-op logger
                
                if (Cts != null && !Cts.IsCancellationRequested)
                {
                    try
                    {
                        Cts.Cancel();
                    }
                    catch { /* Ignore cancellation errors */ }
                }
                
                if (WebSocket != null)
                {
                    try
                    {
                        var webSocketToClose = WebSocket;
                        WebSocket = null; // Set to null immediately to prevent race conditions
                        
                        if (webSocketToClose.State == WebSocketState.Open)
                        {
                            // Use Task.Run to avoid blocking the main thread
                            Task.Run(async () =>
                            {
                                try
                                {
                                    // Use a timeout for closing the WebSocket
                                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                    await webSocketToClose.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin stopped", cts.Token);
                                }
                                catch (Exception ex)
                                {
                                    _logError($"Error closing WebSocket for {Config.SearchId.Value}: {ex.Message}");
                                }
                                finally
                                {
                                    try
                                    {
                                        webSocketToClose.Dispose();
                                    }
                                    catch (Exception ex)
                                    {
                                        _logError($"Error disposing WebSocket for {Config.SearchId.Value}: {ex.Message}");
                                    }
                                }
                            });
                            
                            // Fallback: Force dispose after 3 seconds if still not closed
                            Task.Delay(3000).ContinueWith(_ =>
                            {
                                try
                                {
                                    if (webSocketToClose.State != WebSocketState.Closed)
                                    {
                                        webSocketToClose.Dispose();
                                    }
                                }
                                catch { /* Ignore disposal errors in fallback */ }
                            });
                        }
                        else
                        {
                            try
                            {
                                webSocketToClose.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _logError($"Error disposing WebSocket for {Config.SearchId.Value}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logError($"Error during WebSocket cleanup for {Config.SearchId.Value}: {ex.Message}");
                        WebSocket = null; // Ensure it's null even if cleanup fails
                    }
                }
                
                _logMessage($"✅ STOPPED: Search {Config.SearchId.Value}");
            }
        }

        private async void MoveMouseToItemLocation(int x, int y, Action<string> logMessage)
        {
            try
            {
                var purchaseWindow = _parent.GameController.IngameState.IngameUi.PurchaseWindowHideout;
                if (!purchaseWindow.IsVisible)
                {
                    logMessage("MoveMouseToItemLocation: Purchase window is not visible");
                    return;
                }

                var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
                if (stashPanel == null)
                {
                    logMessage("MoveMouseToItemLocation: Stash panel is null");
                    return;
                }

                var rect = stashPanel.GetClientRectCache;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    logMessage("MoveMouseToItemLocation: Invalid stash panel dimensions");
                    return;
                }

                float cellWidth = rect.Width / 12.0f;
                float cellHeight = rect.Height / 12.0f;
                var topLeft = rect.TopLeft;
                
                // Calculate item position within the stash panel (bottom-right to avoid sockets)
                int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth * 7 / 8));
                int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight * 7 / 8));
                
                // Get game window position
                var windowRect = _parent.GameController.Window.GetWindowRectangle();
                System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
                
                // Calculate final screen position
                int finalX = windowPos.X + itemX;
                int finalY = windowPos.Y + itemY;
                
                // Move mouse cursor
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
                
                logMessage($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
                
                // Auto Buy: Perform Ctrl+Left Click if enabled
                if (_parent.Settings.AutoBuy.Value)
                {
                    logMessage("🛒 AUTO BUY: Enabled, performing purchase click...");
                    await Task.Delay(100); // Small delay to ensure mouse movement is complete
                    await _parent.PerformCtrlLeftClickAsync();
                }
            }
            catch (Exception ex)
            {
                logMessage($"MoveMouseToItemLocation failed: {ex.Message}");
            }
        }
    }
}
