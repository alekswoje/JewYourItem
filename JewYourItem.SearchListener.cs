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
        public bool IsAuthenticationError { get; set; } = false;
        public DateTime LastConnectionAttempt { get; set; } = DateTime.MinValue;
        public DateTime LastErrorTime { get; set; } = DateTime.MinValue;
        public int ConnectionAttempts { get; set; } = 0;
        private readonly object _connectionLock = new object();
        private StringBuilder _messageBuffer = new StringBuilder();
        private readonly Action<string> _logMessage;
        private readonly Action<string> _logError;

        // ✅ DODAJ TUTAJ
        public bool LastIsRunning { get; set; } = false;
        public bool LastIsConnecting { get; set; } = false;

        // Transient upstream errors from GGG that should NOT trigger cooldown
        private bool IsBenignTransientError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                var t = text.ToLowerInvariant();
                // Expandable list; starting with error code 7
                if (t.Contains("error code 7") || t.Contains("code 7") || t.Contains("error code: 7"))
                    return true;
                // Some reports mention "us" code; keep flexible match
                if (t.Contains("error code us") || t.Contains("code us"))
                    return true;
            }
            catch { }
            return false;
        }

        // Exponential backoff fields
        private int _currentRetryDelay = 1000; // Start with 1 second
        private const int MaxRetryDelay = 30000; // Max 30 seconds
        private const int BackoffMultiplier = 2;

        /// <summary>
        /// Calculates exponential backoff delay based on attempt count
        /// </summary>
        /// <param name="attemptCount">Number of connection attempts</param>
        /// <returns>Delay in milliseconds</returns>
        private int CalculateExponentialBackoffDelay(int attemptCount)
        {
            if (attemptCount <= 0) return 0;
            
            // Exponential backoff with reasonable limits:
            // Attempt 1: 1s, 2: 2s, 3: 4s, 4: 8s, 5: 16s, 6: 32s, 7: 60s, 8: 120s, 9: 300s, 10: 600s, 11+: 1800s (30min)
            int[] delays = { 1000, 2000, 4000, 8000, 16000, 32000, 60000, 120000, 300000, 600000, 1800000 };
            
            int index = Math.Min(attemptCount - 1, delays.Length - 1);
            return delays[index];
        }

        public SearchListener(JewYourItem parent, JewYourItemInstanceSettings config, Action<string> logMessage, Action<string> logError)
        {
            _parent = parent;
            Config = config;
            Cts = new CancellationTokenSource();
            _logMessage = logMessage;
            _logError = logError;
        }

        public async void Start(Action<string> logMessage, Action<string> logError, string sessionId)
        {
            // Add delay before connection attempt
            if (_currentRetryDelay > 0)
            {
                logMessage($"⏳ Delaying connection attempt for {_currentRetryDelay}ms");
                await Task.Delay(_currentRetryDelay);
            }

            lock (_connectionLock)
            {
                // STRICT CONNECTION SAFETY CHECKS
                if (string.IsNullOrEmpty(Config.League.Value) || string.IsNullOrEmpty(Config.SearchId.Value) || string.IsNullOrEmpty(sessionId))
                {
                    _logError($"❌ VALIDATION FAILED: League='{Config.League.Value}', SearchId='{Config.SearchId.Value}', SessionId length={sessionId?.Length ?? 0}");
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

                // Rate limit cooldown check
                if ((DateTime.Now - LastErrorTime).TotalSeconds < JewYourItem.RestartCooldownSeconds)
                {
                    _logMessage($"🛡️ CONNECTION BLOCKED: Search {Config.SearchId.Value} in rate limit cooldown ({JewYourItem.RestartCooldownSeconds - (DateTime.Now - LastErrorTime).TotalSeconds:F1}s remaining)");
                    return;
                }

                // EMERGENCY: Connection attempt throttling (max 1 attempt per 30 seconds)
                if ((DateTime.Now - LastConnectionAttempt).TotalSeconds < 30)
                {
                    _logMessage($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} connection throttled ({30 - (DateTime.Now - LastConnectionAttempt).TotalSeconds:F1}s remaining)");
                    return;
                }

                // Gradual reset: Reduce attempt count over time to allow recovery
                if ((DateTime.Now - LastConnectionAttempt).TotalHours >= 1)
                {
                    // Reset attempts after 1 hour to allow recovery
                    ConnectionAttempts = 0;
                    _logMessage($"♻️ ATTEMPT RESET: Search {Config.SearchId.Value} attempts counter reset after 1 hour, resuming search");
                }
                else if ((DateTime.Now - LastConnectionAttempt).TotalMinutes >= 30)
                {
                    // Partial reset after 30 minutes
                    ConnectionAttempts = Math.Max(0, ConnectionAttempts - 2);
                    _logMessage($"♻️ PARTIAL RESET: Search {Config.SearchId.Value} attempts reduced to {ConnectionAttempts} after 30 minutes");
                }

                // Set connection state flags
                IsConnecting = true;
                LastConnectionAttempt = DateTime.Now;
                ConnectionAttempts++;

                _logMessage($"🔌 STARTING CONNECTION: Search {Config.SearchId.Value} (Attempt #{ConnectionAttempts})");
            }

            try
            {
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
                
                // Add additional authentication headers that might be required
                WebSocket.Options.SetRequestHeader("Accept", "*/*");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua", "\"Not;A=Brand\";v=\"99\", \"Google Chrome\";v=\"139\", \"Chromium\";v=\"139\"");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Arch", "x86");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Bitness", "64");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Full-Version", "139.0.7258.157");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Full-Version-List", "\"Not;A=Brand\";v=\"99.0.0.0\", \"Google Chrome\";v=\"139.0.7258.157\", \"Chromium\";v=\"139.0.7258.157\"");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Mobile", "?0");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Model", "");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Platform", "Windows");
                WebSocket.Options.SetRequestHeader("Sec-Ch-Ua-Platform-Version", "19.0.0");
                WebSocket.Options.SetRequestHeader("Sec-Fetch-Dest", "websocket");
                WebSocket.Options.SetRequestHeader("Sec-Fetch-Mode", "websocket");
                WebSocket.Options.SetRequestHeader("Sec-Fetch-Site", "same-origin");
                WebSocket.Options.SetRequestHeader("Priority", "u=1, i");
                WebSocket.Options.SetRequestHeader("Referer", $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(Config.League.Value)}/{Config.SearchId.Value}/live");

                string url = $"wss://www.pathofexile.com/api/trade2/live/poe2/{Uri.EscapeDataString(Config.League.Value)}/{Config.SearchId.Value}";

                _logMessage($"🔌 CONNECTING: {Config.SearchId.Value} to {url}");
                await WebSocket.ConnectAsync(new Uri(url), Cts.Token);

                // Only increment global counter AFTER successful connection
                JewYourItem._globalConnectionAttempts++;

                // Reset retry delay and connection attempts after successful connection
                _currentRetryDelay = 1000;
                ConnectionAttempts = 0; // Reset attempts on successful connection

                lock (_connectionLock)
                {
                    IsConnecting = false;
                    IsRunning = true;
                }

                _logMessage($"✅ CONNECTED: WebSocket for search {Config.SearchId.Value}");
                _parent._activeListener = this;
                _ = ReceiveMessagesAsync(_logMessage, _logError, sessionId);
            }
            catch (Exception ex)
            {
                // Use the same exponential backoff system as the main plugin
                _currentRetryDelay = CalculateExponentialBackoffDelay(ConnectionAttempts);

                lock (_connectionLock)
                {
                    IsConnecting = false;
                    IsRunning = false;
                }

                _logError($"❌ CONNECTION FAILED: Search {Config.SearchId.Value}: {ex.Message} - Retry in {_currentRetryDelay}ms");
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
                        try
                        {
                            await ProcessMessage(fullMessage, logMessage, logError, sessionId);
                        }
                        catch (Exception pmEx)
                        {
                            if (!IsBenignTransientError(pmEx.Message))
                            {
                                LastErrorTime = DateTime.Now;
                            }
                            throw;
                        }
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
                    if (!IsBenignTransientError(ex.Message))
                    {
                        LastErrorTime = DateTime.Now;
                    }
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

                        // DEBUG: Log detailed batch information if debug mode is enabled
                        if (_parent.Settings.DebugMode.Value)
                        {
                            _parent.LogMessage($"🔍 DEBUG: Batch processing started at {DateTime.Now:HH:mm:ss.fff}");
                            _parent.LogMessage($"🔍 DEBUG: Total items: {wsResponse.New.Length}, Batches: {itemBatches.Count}");
                            for (int i = 0; i < itemBatches.Count; i++)
                            {
                                _parent.LogMessage($"🔍 DEBUG: Batch {i + 1}: {itemBatches[i].Length} items [{string.Join(", ", itemBatches[i])}]");
                            }
                            
                            // Log current rate limiter state before processing
                            if (_parent._rateLimiter != null)
                            {
                                _parent.LogMessage("🔍 DEBUG: Rate limiter state before batch processing:");
                                _parent._rateLimiter.LogCurrentState();
                            }
                        }

                        // Process each batch separately
                        int batchNumber = 0;
                        DateTime batchProcessingStartTime = DateTime.Now;
                        foreach (var batch in itemBatches)
                        {
                            batchNumber++;
                            DateTime batchStartTime = DateTime.Now;
                            
                            // DEBUG: Log batch timing and rate limiter state
                            if (_parent.Settings.DebugMode.Value)
                            {
                                _parent.LogMessage($"🔍 DEBUG: Starting batch {batchNumber}/{itemBatches.Count} at {batchStartTime:HH:mm:ss.fff}");
                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} items: [{string.Join(", ", batch)}]");
                                
                                // Check rate limiter state before each batch
                                if (_parent._rateLimiter != null)
                                {
                                    _parent.LogMessage($"🔍 DEBUG: Rate limiter state before batch {batchNumber}:");
                                    _parent._rateLimiter.LogCurrentState();
                                }
                            }

                            // REMOVED: Rate limiting for batch requests to make auto TP instant
                            string ids = string.Join(",", batch);
                            string fetchUrl = $"https://www.pathofexile.com/api/trade2/fetch/{ids}?query={Config.SearchId.Value}&realm=poe2";

                            logMessage($"🔍 FETCHING batch {batchNumber} of {batch.Length} items: {ids}");

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

                                DateTime requestStartTime = DateTime.Now;
                                using (var response = await _httpClient.SendAsync(request))
                                {
                                    DateTime responseTime = DateTime.Now;
                                    var requestDuration = responseTime - requestStartTime;
                                    
                                    // DEBUG: Log request timing and response details
                                    if (_parent.Settings.DebugMode.Value)
                                    {
                                        _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} HTTP request completed at {responseTime:HH:mm:ss.fff}");
                                        _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} request duration: {requestDuration.TotalMilliseconds:F0}ms");
                                        _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} response status: {response.StatusCode}");
                                        _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} response headers: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(";", h.Value)}"))}");
                                    }

                                    // Handle rate limiting
                                    if (_parent._rateLimiter != null)
                                    {
                                        // DEBUG: Log rate limiter state before handling response
                                        if (_parent.Settings.DebugMode.Value)
                                        {
                                            _parent.LogMessage($"🔍 DEBUG: Handling rate limit response for batch {batchNumber}");
                                        }
                                        
                                        var rateLimitWaitTime = await _parent._rateLimiter.HandleRateLimitResponse(response);
                                        if (rateLimitWaitTime > 0)
                                        {
                                            if (_parent.Settings.DebugMode.Value)
                                            {
                                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} RATE LIMITED! Wait time: {rateLimitWaitTime}ms, stopping batch processing");
                                            }
                                            return; // Rate limited, wait and return
                                        }
                                        
                                        // DEBUG: Log rate limiter state after handling response
                                        if (_parent.Settings.DebugMode.Value)
                                        {
                                            _parent.LogMessage($"🔍 DEBUG: Rate limiter state after batch {batchNumber} response:");
                                            _parent._rateLimiter.LogCurrentState();
                                        }
                                    }

                                    if (response.IsSuccessStatusCode)
                                    {
                                        string content = await response.Content.ReadAsStringAsync();
                                        var itemResponse = JsonConvert.DeserializeObject<ItemFetchResponse>(content);
                                        if (itemResponse.Result != null && itemResponse.Result.Any())
                                        {
                                            logMessage($"✅ BATCH {batchNumber} SUCCESS: Received {itemResponse.Result.Length} items from batch");
                                            
                                            // DEBUG: Log detailed success information
                                            if (_parent.Settings.DebugMode.Value)
                                            {
                                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} processed {itemResponse.Result.Length} items successfully");
                                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} items: {string.Join(", ", itemResponse.Result.Select(r => r.Item.Name ?? r.Item.TypeLine))}");
                                            }
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
                                                    SearchId = Config.SearchId.Value, // Track which search this item came from
                                                    X = x,
                                                    Y = y,
                                                    AddedTime = DateTime.Now,
                                                    TokenIssuedAt = issuedAt,
                                                    TokenExpiresAt = expiresAt
                                                };

                                                _parent._recentItems.Enqueue(recentItem);
                                                while (_parent._recentItems.Count > _parent.Settings.MaxRecentItems.Value)
                                                    _parent._recentItems.Dequeue();
                                                
                                                // Log search result to file if enabled
                                                bool autoBuyEnabled = _parent.Settings.AutoBuy.Value;
                                                string initialStatus = autoBuyEnabled ? "AUTO-BUY PENDING" : "MANUAL BUY";
                                                _parent.LogSearchResult(name, price, Config.Name.Value, Config.SearchId.Value, Config.League.Value, initialStatus);
                                                if (_parent._playSound)
                                                {
                                                    logMessage("Attempting to play sound...");
                                                    try
                                                    {
                                                        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                                                        var assemblyDir = string.IsNullOrEmpty(assemblyLocation) ? null : Path.GetDirectoryName(assemblyLocation);

                                                        int randomNumber = _random.Next(1, 10001); // 1 in 10,000 chance
                                                        bool playRareSound = randomNumber == 1;
                                                        string soundFileName = playRareSound ? "pulserare.wav" : "pulse.wav";

                                                        // Debug logging for rare sound testing
                                                        logMessage($"🎲 SOUND DEBUG: Random={randomNumber}, PlayRare={playRareSound}, File={soundFileName}");

                                                        if (playRareSound)
                                                        {
                                                            logMessage("🎉 WHAT ARE YOU DOING STEP BRO! 🎉 (1 in 10,000 chance)");
                                                        }

                                                        var candidatePaths = new List<string>();
                                                        if (!string.IsNullOrEmpty(assemblyDir))
                                                        {
                                                            candidatePaths.Add(Path.Combine(assemblyDir, "sound", soundFileName));
                                                            candidatePaths.Add(Path.Combine(assemblyDir, "..", "sound", soundFileName));
                                                            candidatePaths.Add(Path.Combine(assemblyDir, "..", "..", "sound", soundFileName));
                                                            candidatePaths.Add(Path.Combine(assemblyDir, "..", "..", "..", "Source", "JewYourItem", "sound", soundFileName));
                                                            var replaced = assemblyDir.Replace("Temp", "Source");
                                                            if (!string.Equals(replaced, assemblyDir, StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                candidatePaths.Add(Path.Combine(replaced, "sound", soundFileName));
                                                            }
                                                            candidatePaths.Add(Path.Combine(assemblyDir, "..", "..", "Source", "JewYourItem", "sound", soundFileName));
                                                        }
                                                        candidatePaths.Add(Path.Combine("sound", soundFileName));
                                                        candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, "sound", soundFileName));
                                                        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "sound", soundFileName));
                                                        candidatePaths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ExileCore2", "Plugins", "Source", "JewYourItem", "sound", soundFileName));
                                                        string soundPath = null;
                                                        foreach (var path in candidatePaths)
                                                        {
                                                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                                            {
                                                                soundPath = path;
                                                                break;
                                                            }
                                                        }
                                                        if (!string.IsNullOrEmpty(soundPath))
                                                        {
                                                            logMessage($"Playing sound file: {soundFileName}...");
                                                            _parent.PlaySoundWithNAudio(soundPath, logMessage, logError);
                                                            logMessage("Sound played successfully!");
                                                        }
                                                        else
                                                        {
                                                            logError("Sound file not found in any of the expected locations:");
                                                            foreach (var path in candidatePaths)
                                                            {
                                                                if (!string.IsNullOrEmpty(path))
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
                                                if (_parent.Settings.AutoTp.Value && _parent.GameController.Area.CurrentArea.IsHideout && !_parent._autoTpPaused)
                                                {
                                                    // Check if TP is locked and if timeout has expired
                                                    if (_parent._tpLocked && (DateTime.Now - _parent._tpLockedTime).TotalSeconds >= 10)
                                                    {
                                                        logMessage("🔓 TP UNLOCKED: 10-second timeout reached in SearchListener, unlocking TP");
                                                        _parent._tpLocked = false;
                                                        _parent._tpLockedTime = DateTime.MinValue;
                                                    }

                                                    if (!_parent._tpLocked)
                                                    {
                                                        _parent.TravelToHideout();
                                                        _parent._recentItems.Clear();
                                                        _parent._lastTpTime = DateTime.Now;
                                                        logMessage("Auto TP executed due to new search result.");
                                                    }
                                                    else
                                                    {
                                                        double remainingTime = 10 - (DateTime.Now - _parent._tpLockedTime).TotalSeconds;
                                                        logMessage($"Auto TP skipped: TP locked, waiting for window or timeout ({Math.Max(0, remainingTime):F1}s remaining)");
                                                    }
                                                }
                                                // REMOVED: Mouse movement should only happen after manual teleports, not when auto TP is blocked by cooldown
                                            }
                                        }
                                        else
                                        {
                                            logMessage($"⚠️ BATCH {batchNumber} EMPTY: No items returned from batch");
                                            
                                            // DEBUG: Log empty batch details
                                            if (_parent.Settings.DebugMode.Value)
                                            {
                                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} returned empty result");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        string errorMessage = await response.Content.ReadAsStringAsync();
                                        
                                        // DEBUG: Log detailed error information
                                        if (_parent.Settings.DebugMode.Value)
                                        {
                                            _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} failed with status {response.StatusCode}");
                                            _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} error message: {errorMessage}");
                                            _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} is benign transient error: {IsBenignTransientError(errorMessage)}");
                                        }
                                        
                                        if (!IsBenignTransientError(errorMessage))
                                        {
                                            LastErrorTime = DateTime.Now;
                                        }
                                        // Rate limiting is now handled above
                                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && errorMessage.Contains("Resource not found; Item no longer available"))
                                        {
                                            logMessage($"⚠️ BATCH {batchNumber} WARNING: Items unavailable in batch: {errorMessage}");
                                        }
                                        else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorMessage.Contains("Invalid query"))
                                        {
                                            logMessage($"⚠️ BATCH {batchNumber} WARNING: Invalid query for batch: {errorMessage}");
                                            if (_parent._recentItems.Count > 0)
                                            {
                                                _parent._recentItems.Dequeue();
                                            }
                                        }
                                        else
                                        {
                                            logError($"❌ BATCH {batchNumber} FAILED: {response.StatusCode} - {errorMessage}");
                                            
                                            // DEBUG: Log that we're stopping batch processing
                                            if (_parent.Settings.DebugMode.Value)
                                            {
                                                _parent.LogMessage($"🔍 DEBUG: Stopping batch processing due to serious error in batch {batchNumber}");
                                            }
                                            break; // Stop processing remaining batches on serious error
                                        }
                                    }
                                } // End of using (var response = await _httpClient.SendAsync(request))
                            } // End of using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
                            
                            // DEBUG: Log batch completion timing
                            if (_parent.Settings.DebugMode.Value)
                            {
                                DateTime batchEndTime = DateTime.Now;
                                var batchDuration = batchEndTime - batchStartTime;
                                var totalDuration = batchEndTime - batchProcessingStartTime;
                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} completed at {batchEndTime:HH:mm:ss.fff}");
                                _parent.LogMessage($"🔍 DEBUG: Batch {batchNumber} duration: {batchDuration.TotalMilliseconds:F0}ms");
                                _parent.LogMessage($"🔍 DEBUG: Total processing time so far: {totalDuration.TotalMilliseconds:F0}ms");
                                
                                // Log rate limiter state after batch completion
                                if (_parent._rateLimiter != null)
                                {
                                    _parent.LogMessage($"🔍 DEBUG: Rate limiter state after batch {batchNumber} completion:");
                                    _parent._rateLimiter.LogCurrentState();
                                }
                            }
                        } // End of batch processing foreach loop
                        
                        // DEBUG: Log final batch processing summary
                        if (_parent.Settings.DebugMode.Value)
                        {
                            DateTime finalEndTime = DateTime.Now;
                            var totalProcessingTime = finalEndTime - batchProcessingStartTime;
                            _parent.LogMessage($"🔍 DEBUG: All batches completed at {finalEndTime:HH:mm:ss.fff}");
                            _parent.LogMessage($"🔍 DEBUG: Total batch processing time: {totalProcessingTime.TotalMilliseconds:F0}ms");
                            _parent.LogMessage($"🔍 DEBUG: Processed {batchNumber}/{itemBatches.Count} batches successfully");
                        }
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
                                    await webSocketToClose.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin stopped", CancellationToken.None);
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
                    logMessage("🛒 AUTO BUY: Enabled, attempting guarded purchase...");

                    // Try to resolve the item by coordinates with retries
                    RecentItem itemBeingProcessed = null;
                    const int maxFindAttempts = 3;
                    for (int attempt = 1; attempt <= maxFindAttempts && itemBeingProcessed == null; attempt++)
                    {
                        itemBeingProcessed = _parent.FindRecentItemByCoordinates(x, y);
                        if (itemBeingProcessed == null && _parent._teleportedItemInfo != null)
                        {
                            logMessage($"🔄 USING TELEPORTED ITEM INFO: '{_parent._teleportedItemInfo.Name}' (Search: {_parent._teleportedItemInfo.SearchId})");
                            itemBeingProcessed = _parent._teleportedItemInfo;
                        }
                        if (itemBeingProcessed == null)
                        {
                            logMessage($"⏳ ITEM NOT FOUND (attempt {attempt}/{maxFindAttempts}), retrying in 100ms...");
                            await Task.Delay(100);
                        }
                    }

                    if (itemBeingProcessed != null)
                    {
                        logMessage($"✅ FOUND ITEM FOR LOG UPDATE: '{itemBeingProcessed.Name}' (Search: {itemBeingProcessed.SearchId}) at ({x}, {y})");
                        _parent.UpdateAutoBuyAttempt(itemBeingProcessed.Name, itemBeingProcessed.SearchId);

                        // Small delay to ensure mouse movement is complete
                        await Task.Delay(100);
                        await _parent.PerformCtrlLeftClickAsync();

                        // Clear teleported info after purchase attempt
                        _parent._teleportedItemInfo = null;
                    }
                    else
                    {
                        logMessage($"❌ ITEM NOT FOUND AFTER RETRIES for coordinates ({x}, {y}) - skipping auto-buy click");
                    }
                }
            }
            catch (Exception ex)
            {
                logMessage($"MoveMouseToItemLocation failed: {ex.Message}");
            }
        }
    }
}