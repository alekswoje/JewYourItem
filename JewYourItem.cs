using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Net.Http;
using System.IO;
using System.Net;
using System.IO.Compression;
using System.Media;
using System.Windows.Forms;

namespace JewYourItem;

public class JewYourItem : BaseSettingsPlugin<JewYourItemSettings>
{
    private List<SearchListener> _listeners = new List<SearchListener>();
    private string _sessionIdBuffer = "";
    private List<string> _recentResponses = new List<string>();
    private const int MaxResponses = 5; // Limit to 5 items
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli });
    private Queue<(string name, string price, string hideoutToken, int x, int y)> _recentItems = new Queue<(string, string, string, int, int)>(MaxResponses); // Added x, y coordinates
    private bool _playSound = true;
    private bool _lastHotkeyState = false;
    private DateTime _lastTpTime = DateTime.MinValue;
    private bool _settingsUpdated = false;
    private SearchListener _activeListener; // Track the active listener for referer URL

    public override bool Initialise()
    {
        _sessionIdBuffer = Settings.SessionId.Value ?? ""; // Sync with saved setting on load
        if (string.IsNullOrEmpty(_sessionIdBuffer))
        {
            LogMessage("Warning: Session ID is empty. Please set it in the settings.");
        }
        
        // Log ASCII art
        LogMessage("                                                                                                    ");
        LogMessage("::::::::::::::::::::::::::::::::::::::::::::::::::-*@@@@@@%+:::::*=::=-:::::::::::::::::::::::::: ");
        LogMessage("::::::::::::::::::::::::::::::::::::::::::::::=@@@+*@@#=@*+*@#@@@%@+=@+%+::::::::::::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::::::::::+@@@=+::*@@@#*+**%+@%@@*@%@@@#:=+:::::::::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::::::::#@%-::-*@@*:::::::::::=#*@#%@@@*@@+:::::::::::::::::::::: ");
        LogMessage("::::::::::::::::::::::::::::::::::::::-@@%:@:*@*:::::::::::::::::::::-@@+@#@::::::::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::::+@+@@=@%:::::::::::::::::::::::::::==::@*::::::::::::::::::: ");
        LogMessage("::::::::::::::::::::::::::::::::::::=@*@-@*@:::::::::::::::::::::::::::::::::=@-::::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::=@-@@@%#@*::::::::::::::::::::::::::::::::::@::::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::%@=@@@#%@#+%+-%@::::::::::::::::::::::::::::+@:::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::::@+%=@%#@*@@@#@@-:::::::::::::::::::::::::::::+*::::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::::::-@@@@@@@+@@%@*+=-=@*:::::::::::::::::::::::::::*%:::::::::::::: ");
        LogMessage("::::::::::::::::::::::::::::::=@%-@@@%@@*@@@=@@@@@@*::::*@%:#@--+::::+@@@@@#::::::+#::::::::::::: ");
        LogMessage(":::::::::::::::::::::::::::::*#*@+@%+@#@@#@%@@*%-=-::::%@#:*=@+*%@%=-::*@@@%-+@@:::@-:::::::::::: ");
        LogMessage(":::::::::::::::::::::-*%@@@#-#%=@@@@*@@@@@@@@%#@@*@+::::-##@@@*@@@@@@@-:=:::+-:::::**-=:::::::::: ");
        LogMessage("::::::::::::::::::*%*-:=:::::=%@@#@@*@@%@@##@#@@*=::::::@@@:::::::-+%@@@%*#@#*@#@@@+%@+:::::::::: ");
        LogMessage("::::::::::::::::%*-:::::=%+::+@%@@@@@#+@@@@%%+*#::::-:::@:=:::@@@@@@@#:=@@@-+@@*%@@*:@::::::::::: ");
        LogMessage(":::::::::::::::@::::::::::+*:::::@@-:---:=@@@@%-::==::#@%*-=*@@-=@++:@*:::+::-@@=-:%@%::::::::::: ");
        LogMessage(":::::::::::::-@:::%-:::::::@-::::@--@=-+@@-:@@@-::::::***@@@@-#@@@@@@@@::@+::::@@@@-*+::::::::::: ");
        LogMessage(":::::::::::::%:::=@:::::::::%:::=@:%:@@@+@%:@@*:**#--*%+-:==-##::::::+@-::::::::-@*-%:::::::::::: ");
        LogMessage("::::::::::::@-:::@::::::::::@+::-@:-@=:*@:%+@@::-:-+:::::::::-::-++@+=::::::::::::-@#:::::::::::: ");
        LogMessage(":::::::::::@::::=+::::::::::-@::+@=:%*:@@:-=@@=:+++-::::::::::::::::::::::::::::::::*#::::::::::: ");
        LogMessage("::::::::::*+::::#-:::::::::::#=:+@%::::-#%:#@#=:=*=::::::::::::::::::::::::::::::::::-@:::::::::: ");
        LogMessage("::::::::::@-::::@:::::::::::::@::@%*:::-:::@@@*=@==-::-:::::::::::::::::::::::::::::::=@::::::::: ");
        LogMessage(":::::::::=@*:::-@:::::::::::::=@:@-+@:@@::-@@@%@@-@@%:@%*@@=:=+:::::::@@%-:-%%:::::::::##:::::::: ");
        LogMessage(":::::::::*+%:::-@::::::::::::::#=@=:@+::::=@%+*@@=*@@%@:-:::-@*@==:+@@=::::::-@-:--:::::%:::::::: ");
        LogMessage("::::::::=#:-#-*=@::::::::::::::-*%@:+%:::@@@#@#@@%@@*@@-#@+@=::-#@%+:%:=#*-::::=-=-:::::%:::::::: ");
        LogMessage(":::::::-@:::::*-@:::::::::::::::@+@::#@@@@@@@@@@@@#%@#%%=%#*@%@%##@@*#%-@@@@@+::::::::::%:::::::: ");
        LogMessage(":::::::+*:::::=+@=::::::::::::::*%@::-%=@@+@@@@%@@+*@@@@@@@@@@@%@@@#@+#@+@@#--%#:::::-::#+::::::: ");
        LogMessage("::::::::%-:::::-+@%:::::::::::::::@@:::+@@#@@@%@@@*%*@@@@@*+@@@@@@@@@%@@@@@:@@@*#%@@=::::=%::::::: ");
        LogMessage("::::::::*-:::::-++#:::::::::::::::*=::::%*@*@+@@*@=#@@@@@%=@@+-::::--:::::=::-@@@-::@@+::#=::::::: ");
        LogMessage("::::::::##::::::#-#:::::::::::::::-%::::@-==@@@@@@@@%@@@=@=:@%:#@@@-::::::-::+@@=#=::::--::::::::: ");
        LogMessage("::::::::#:::::::@:%::::::::::::::::@-::-:@*@*@#@@*+@=#@@@%:=:@@@-:#++@@@@@%#@*@=:::::::::::::::::: ");
        LogMessage("::::::::@@::::::#-@::::::::::::::::*#::::=@#@@@@@%@%@@@@@*:*#:=@@#+@@#::=@#*@-:::::::::::::::::::: ");
        LogMessage("::::::::-*:#=::::*#@:::::::::::::::::%-::=@@-@@@@*@*@+@#@*@@-*#::=@@@#@%@@@*@=::::::::::::::::::::: ");
        LogMessage("::::::::@-:::::::=#%=::::::::::::::::-@::@@@@*%@@-@@@@@@@@*@@+%@+:::::+@@@#:#@@:::::::::::::::::::: ");
        LogMessage("::::::::-@-::::::::@=#:::::::::::::::::-@=*:=@#+##@@=@@@@@@@@@@@%@@*:::::::-#@#@@-:::::::::::::::::: ");
        LogMessage("::::::::@%:::::::::#-%::::::::::::::::::=@:%-@@%#@#@@@@@+#@@=@@@@@@@@@@@@@@*@@@@++%#*%:::::::::::::: ");
        LogMessage("::::::::%+%+::::::::=+#:::::::::::::::::::=%-*=%%%*#=@@=@#@@*@@@#@@@@@@@*@*@@@@@@#:=--@:::::::::::::: ");
        LogMessage("::::::::@::+:::::::::@==:::::::::::::::::::*-*+*%@@@@#@@@@@%@@@%@@@@#@#%@@@@@+::::::+#::::::::::::::: ");
        LogMessage("::::::=+:::::::::::::+*#::::::::::::::::::::#+@:*@+@@**@@@@@@@@@@@%@@@@@@%@@:::::*@==+%+*+-:::::::::: ");
        LogMessage("::::::+=:::::::::::::%@-::::::::::::::::::::#*+:-@@*-@@=#@+@@@@@@#@@@%*@@@:::::-@+:::@:::=+::::::::: ");
        LogMessage("::::::*-:::::::::::::+*-:::::::::::::::::::::@%::-#:@@@+@@@@#@@%@@+@=@@@-::::::-:@%-%=::-@@#:::::::: ");
        LogMessage("::::::+-::::::::::::::@+=:::::::::::::::::::::*+*%=%:=@+@@@@+=@@@@@@@@%-=::::::::=@@+::+=:::@::::::: ");
        LogMessage("::::::=+::::::::::::::+##+:::::::::::::::::::::%+*:=*@@:*@#@*@@@@#@@%:+::::::::::::::-+:::#@%::::::: ");
        LogMessage("::::::::::::::::::::::#--::::::::::::::::::-@@%%#::#@%:=#@@@*:::::=%-::::::::::::*%-::+*#-::#:::::: ");
        LogMessage("::::::::::::::::::::::@:::::::::::::::::=@%-::+@#*==-::::::::::::::@::::::%@=-=#@@@:::::::+*:::::: ");
        LogMessage(":::::::::::::::::::::::%::::::::::::::::@=:::-*:::::::::::::::::::*=@:::::::::::::-::+@+-*%::::::: ");
        LogMessage("::::::::::::::::::::::+#::::::::::::::::::::@-:::::::::::::::::::-**=:::+@@*@#%@*=@*@#@-#:::::::: ");
        LogMessage(":::::::::::::::::::::::+*::::::::::::::::::*@:::::::::::::::::::::*+=::-@+-@:%+*-=*#=:-@-:::::::: ");
        LogMessage("::::::::::::::::::::::::+%:::::::::::::::::*::::::::+-:::::::::::==#:*:+*::*:::*::+@-:*=::::::::: ");
        LogMessage("::::::::::::::::::::::::::#::::::::::::::::=::::::::#-::::::::::=@%===*-@@@@:-*@@#@-=*-:::::::::: ");
        LogMessage(":::::::::::::::::::::::::::@=:::::::::::::::::::::::#::::::::=#@=:::::::::::--::::::::::::::::::: ");
        LogMessage("::::::::::::::::::::::::::::##:::::::::::::::::::::#=::::::#*:::::::::::::::::::::::::::::::::::: ");
        LogMessage("                                                                                                    ");
        
        LogMessage("Plugin initialized");
        LogMessage($"Hotkey set to: {Settings.TravelHotkey.Value}");
        LogMessage($"Sound enabled: {Settings.PlaySound.Value}");
        LogMessage($"Auto TP enabled: {Settings.AutoTp.Value}");
        LogMessage($"GUI enabled: {Settings.ShowGui.Value}");
        LogMessage($"TP Cooldown set to: {Settings.TpCooldown.Value} seconds");
        LogMessage($"Move Mouse to Item enabled: {Settings.MoveMouseToItem.Value}");

        return true;
    }

    private class SearchListener
    {
        private readonly JewYourItem _parent;
        public JewYourItemInstanceSettings Config { get; }
        public ClientWebSocket WebSocket { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public bool IsRunning { get; set; }
        private StringBuilder _messageBuffer = new StringBuilder();
        private readonly Action<string> _logMessage; // Capture logMessage
        private readonly Action<string> _logError;   // Capture logError

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
            if (string.IsNullOrEmpty(Config.League.Value) || string.IsNullOrEmpty(Config.SearchId.Value) || string.IsNullOrEmpty(sessionId))
            {
                _logError("League, Search ID, or Session ID is empty for this search.");
                return;
            }

            if (IsRunning) return;

            IsRunning = true;
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

            try
            {
                string url = $"wss://www.pathofexile.com/api/trade2/live/poe2/{Uri.EscapeDataString(Config.League.Value)}/{Config.SearchId.Value}";
                await WebSocket.ConnectAsync(new Uri(url), Cts.Token);

                _logMessage($"Connected to WebSocket for search: {Config.SearchId.Value}");
                _parent._activeListener = this; // Update active listener
                _ = ReceiveMessagesAsync(_logMessage, _logError, sessionId);
            }
            catch (Exception ex)
            {
                _logError($"WebSocket connection failed for search {Config.SearchId.Value}: {ex.Message}");
                IsRunning = false;
            }
        }

        private async Task ReceiveMessagesAsync(Action<string> logMessage, Action<string> logError, string sessionId)
        {
            var buffer = new byte[1024 * 4];
            while (WebSocket.State == WebSocketState.Open && !Cts.Token.IsCancellationRequested)
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
                            logMessage($"WebSocket closed by server for {Config.SearchId.Value}.");
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
                    logError($"WebSocket error for {Config.SearchId.Value}: {ex.Message}");
                    break;
                }
            }
            IsRunning = false;
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
                        string ids = string.Join(",", wsResponse.New);
                        string fetchUrl = $"https://www.pathofexile.com/api/trade2/fetch/{ids}?query={Config.SearchId.Value}&realm=poe2";

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
                                if (response.IsSuccessStatusCode)
                                {
                                    string content = await response.Content.ReadAsStringAsync();
                                    var itemResponse = JsonConvert.DeserializeObject<ItemFetchResponse>(content);
                                    if (itemResponse.Result != null && itemResponse.Result.Any())
                                    {
                                        foreach (var item in itemResponse.Result)
                                        {
                                            string name = item.Item.Name;
                                            if (string.IsNullOrEmpty(name))
                                            {
                                                name = item.Item.TypeLine;
                                            }
                                            string price = item.Listing?.Price != null ? $"{item.Listing.Price.Type} {item.Listing.Price.Amount} {item.Listing.Price.Currency}" : "No price";
                                            int x = item.Listing.Stash?.X ?? 0; // Use Stash property
                                            int y = item.Listing.Stash?.Y ?? 0; // Use Stash property
                                            logMessage($"{name} - {price} at ({x}, {y})");
                                            _parent._recentItems.Enqueue((name, price, item.Listing.HideoutToken, x, y));
                                            if (_parent._recentItems.Count > MaxResponses)
                                                _parent._recentItems.Dequeue(); // Remove oldest item if over 5
                                            if (_parent._playSound)
                                            {
                                                logMessage("Attempting to play sound...");
                                                try
                                                {
                                                    var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                                                    var assemblyDir = Path.GetDirectoryName(assemblyLocation);
                                                    var possiblePaths = new[]
                                                    {
                                                        Path.Combine(assemblyDir, "sound", "pulse.wav"),
                                                        "sound/pulse.wav",
                                                        Path.Combine(assemblyDir, "..", "sound", "pulse.wav"),
                                                        Path.Combine(assemblyDir, "..", "..", "sound", "pulse.wav"),
                                                        Path.Combine(assemblyDir, "..", "..", "..", "Source", "JewYourItem", "sound", "pulse.wav"),
                                                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sound", "pulse.wav"),
                                                        Path.Combine(Directory.GetCurrentDirectory(), "sound", "pulse.wav")
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
                                                        logMessage("Playing sound file...");
                                                        new SoundPlayer(soundPath).Play();
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
                                                }
                                            }
                                            if (_parent.Settings.AutoTp.Value && _parent.GameController.Area.CurrentArea.IsHideout)
                                            {
                                                if ((DateTime.Now - _parent._lastTpTime).TotalSeconds >= _parent.Settings.TpCooldown.Value)
                                                {
                                                    _parent.TravelToHideout();
                                                    _parent._recentItems.Clear(); // Clear items after successful teleport
                                                    _parent._lastTpTime = DateTime.Now;
                                                    logMessage("Auto TP executed due to new search result.");
                                                }
                                                else
                                                {
                                                    logMessage($"Auto TP skipped: Cooldown of {_parent.Settings.TpCooldown.Value} seconds not met.");
                                                }
                                            }
                                            if (_parent.Settings.MoveMouseToItem.Value && _parent.GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                                            {
                                                MoveMouseToItemLocation(x, y, logMessage);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    string errorMessage = await response.Content.ReadAsStringAsync();
                                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound && errorMessage.Contains("Resource not found; Item no longer available"))
                                    {
                                        logMessage("[WARNING] Item unavailable: " + errorMessage);
                                    }
                                    else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorMessage.Contains("Invalid query"))
                                    {
                                        logMessage("[WARNING] Invalid query for item: " + errorMessage);
                                        if (_parent._recentItems.Count > 0)
                                        {
                                            _parent._recentItems.Dequeue(); // Remove the invalid item
                                        }
                                    }
                                    else
                                    {
                                        logError($"Fetch failed: {response.StatusCode} - {errorMessage}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (JsonException parseEx)
                {
                    logError($"JSON parsing failed: {parseEx.Message}");
                }
            }
            catch (JsonException jsonEx)
            {
                logError($"JSON parsing failed: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                logError($"Processing failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (WebSocket != null)
            {
                Cts.Cancel();
                WebSocket.Dispose();
                WebSocket = null;
                IsRunning = false;
            }
        }

        private void MoveMouseToItemLocation(int x, int y, Action<string> logMessage)
        {
            var purchaseWindow = _parent.GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (purchaseWindow.IsVisible)
            {
                var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
                var rect = stashPanel.GetClientRectCache;
                float cellWidth = rect.Width / 12.0f;
                float cellHeight = rect.Height / 12.0f;
                var topLeft = rect.TopLeft;
                int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth / 2));
                int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight / 2));
                var windowRect = _parent.GameController.Window.GetWindowRectangle();
                System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(windowPos.X + itemX, windowPos.Y + itemY);
                logMessage($"Moved mouse to item location: ({itemX}, {itemY})");
            }
        }
    }

    private void TravelToHideout()
    {
        if (!this.GameController.Area.CurrentArea.IsHideout)
        {
            LogMessage("Teleport skipped: Not in hideout zone.");
            return;
        }

        if ((DateTime.Now - _lastTpTime).TotalSeconds < Settings.TpCooldown.Value)
        {
            LogMessage($"Teleport skipped: Cooldown of {Settings.TpCooldown.Value} seconds not met.");
            return;
        }

        LogMessage("=== TRAVEL TO HIDEOUT HOTKEY PRESSED ===");
        LogMessage($"Recent items count: {_recentItems.Count}");
        
        if (_recentItems.Count == 0) 
        {
            LogMessage("No recent items available for travel");
            return;
        }

        var (name, price, hideoutToken, x, y) = _recentItems.Peek();
        LogMessage($"Attempting to travel to hideout for item: {name} - {price}");
        LogMessage($"Hideout token: {hideoutToken}");
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.pathofexile.com/api/trade2/whisper")
        {
            Content = new StringContent($"{{ \"token\": \"{hideoutToken}\" }}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Cookie", $"POESESSID={Settings.SessionId.Value}");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Priority", "u=1, i");
        if (_activeListener != null)
        {
            request.Headers.Add("Referer", $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(_activeListener.Config.League.Value)}/{_activeListener.Config.SearchId.Value}/live");
        }
        else
        {
            LogMessage("[WARNING] No active listener found for referer URL.");
        }
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

        try
        {
            LogMessage("Sending teleport request...");
            var response = _httpClient.SendAsync(request).Result;
            LogMessage($"Response status: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Teleport failed: {response.StatusCode}");
                var responseContent = response.Content.ReadAsStringAsync().Result;
                LogError($"Response content: {responseContent}");
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && responseContent.Contains("Invalid query"))
                {
                    _recentItems.Dequeue(); // Remove invalid item
                }
            }
            else
            {
                LogMessage("Teleport to hideout successful!");
                _recentItems.Clear(); // Clear items after successful teleport
                _lastTpTime = DateTime.Now;
                if (Settings.MoveMouseToItem.Value && GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                {
                    MoveMouseToItemLocation(x, y);
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Teleport request failed: {ex.Message}");
            LogError($"Exception details: {ex}");
        }
    }

    private void MoveMouseToItemLocation(int x, int y)
    {
        var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
        if (purchaseWindow.IsVisible)
        {
            var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
            var rect = stashPanel.GetClientRectCache;
            float cellWidth = rect.Width / 12.0f;
            float cellHeight = rect.Height / 12.0f;
            var topLeft = rect.TopLeft;
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth / 2));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight / 2));
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(windowPos.X + itemX, windowPos.Y + itemY);
            LogMessage($"Moved mouse to item location: ({itemX}, {itemY})");
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        StopAll();
    }

    public override void Tick()
    {
        if (!Settings.Enable.Value)
        {
            StopAll();
            return;
        }

        bool currentHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
        if (currentHotkeyState && !_lastHotkeyState)
        {
            LogMessage($"Hotkey {Settings.TravelHotkey.Value} pressed");
            TravelToHideout();
        }
        _lastHotkeyState = currentHotkeyState;

        var allConfigs = Settings.Groups
            .Where(g => g.Enable.Value)
            .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
            .ToList();

        if (allConfigs.Count > 20)
        {
            LogError("Exceeded max 20 searches; disabling extras.");
            allConfigs = allConfigs.Take(20).ToList();
        }

        // Create a set of active configurations for quick lookup
        var activeConfigs = allConfigs.ToHashSet();

        // Stop listeners for disabled configurations
        foreach (var listener in _listeners.ToList())
        {
            if (!activeConfigs.Contains(listener.Config))
            {
                listener.Stop();
                _listeners.Remove(listener);
                LogMessage($"Stopped listener for disabled search: {listener.Config.SearchId.Value}");
            }
            else if (!listener.IsRunning)
            {
                // Restart listener if it stopped unexpectedly but is still enabled
                listener.Start(LogMessage, LogError, Settings.SessionId.Value);
                LogMessage($"Restarted listener for search: {listener.Config.SearchId.Value}");
            }
        }

        // Start new listeners for newly enabled configurations
        foreach (var config in allConfigs)
        {
            var listener = _listeners.FirstOrDefault(l => l.Config == config);
            if (listener == null)
            {
                listener = new SearchListener(this, config, LogMessage, LogError);
                _listeners.Add(listener);
                listener.Start(LogMessage, LogError, Settings.SessionId.Value);
                LogMessage($"Started new listener for search: {config.SearchId.Value}");
            }
        }
    }

    public override void Render()
    {
        if (!Settings.Enable.Value || !Settings.ShowGui.Value) return;

        ImGui.SetNextWindowPos(Settings.WindowPosition);
        ImGui.Begin("JewYourItem Results", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 0.8f)); // Dark background
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.9f, 0.9f, 1.0f)); // Light text

        if (Settings.AutoTp.Value && (DateTime.Now - _lastTpTime).TotalSeconds < Settings.TpCooldown.Value)
        {
            float remainingCooldown = Settings.TpCooldown.Value - (float)(DateTime.Now - _lastTpTime).TotalSeconds;
            ImGui.Text($"TP Cooldown: {remainingCooldown:F1}s");
        }

        ImGui.Text($"JewYourItem: {_listeners.Count(l => l.IsRunning)} active");
        if (_recentItems.Count > 0)
        {
            ImGui.Separator();
            foreach (var item in _recentItems)
            {
                ImGui.Text($"{item.name} - {item.price} at ({item.x}, {item.y})");
            }
        }
        else
        {
            ImGui.Text("No recent items");
        }

        ImGui.PopStyleColor(2); // Restore colors
        ImGui.End();

        if (Settings.ShowGui.Value)
        {
            Graphics.DrawText($"JewYourItem: {_listeners.Count(l => l.IsRunning)} active", new Vector2(100, 100), Color.LightGreen);
        }
    }

    private void StopAll()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }
        _listeners.Clear();
    }

    public void OnPluginDestroy()
    {
        StopAll();
    }

    public override void DrawSettings()
    {
        ImGui.Text("Session ID:");
        ImGui.SameLine();
        ImGui.InputText("##SessionId", ref _sessionIdBuffer, 100, ImGuiInputTextFlags.Password);
        if (ImGui.IsItemEdited() && !_settingsUpdated)
        {
            Settings.SessionId.Value = _sessionIdBuffer;
            _settingsUpdated = true;
            LogMessage($"Session ID updated to: {Settings.SessionId.Value}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        var playSound = Settings.PlaySound.Value;
        ImGui.Checkbox("Play Sound on New Result", ref playSound);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.PlaySound.Value = playSound;
            _playSound = playSound;
            _settingsUpdated = true;
            LogMessage($"Sound setting changed to: {playSound}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        var hotkey = Settings.TravelHotkey.Value.ToString();
        ImGui.Text("Travel to Hideout Hotkey:");
        ImGui.SameLine();
        ImGui.InputText("##TravelHotkey", ref hotkey, 100, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.IsItemActive())
        {
            LogMessage("Waiting for new hotkey input...");
            foreach (Keys key in Enum.GetValues(typeof(Keys)))
            {
                if (Input.GetKeyState(key) && key != Keys.None)
                {
                    Settings.TravelHotkey.Value = key;
                    LogMessage($"Hotkey changed to: {key}");
                    break;
                }
            }
        }

        var autoTp = Settings.AutoTp.Value;
        ImGui.Checkbox("Auto TP", ref autoTp);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.AutoTp.Value = autoTp;
            _settingsUpdated = true;
            LogMessage($"Auto TP setting changed to: {autoTp}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        var showGui = Settings.ShowGui.Value;
        ImGui.Checkbox("Show GUI", ref showGui);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.ShowGui.Value = showGui;
            _settingsUpdated = true;
            LogMessage($"GUI visibility changed to: {showGui}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        var tpCooldown = Settings.TpCooldown.Value;
        ImGui.Text("TP Cooldown (seconds):");
        ImGui.SameLine();
        if (ImGui.InputInt("##TpCooldown", ref tpCooldown, 1, 10) && !_settingsUpdated)
        {
            Settings.TpCooldown.Value = Math.Clamp(tpCooldown, 1, 30); // Enforce range 1-30
            _settingsUpdated = true;
            LogMessage($"TP Cooldown updated to: {Settings.TpCooldown.Value} seconds");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        var moveMouseToItem = Settings.MoveMouseToItem.Value;
        ImGui.Checkbox("Move Mouse to Item on Load", ref moveMouseToItem);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.MoveMouseToItem.Value = moveMouseToItem;
            _settingsUpdated = true;
            LogMessage($"Move Mouse to Item setting changed to: {moveMouseToItem}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }

        base.DrawSettings();
    }
}

public class WsResponse
{
    [JsonProperty("new")]
    public string[] New { get; set; }
}

public class ItemFetchResponse
{
    [JsonProperty("result")]
    public ResultItem[] Result { get; set; }
}

public class ResultItem
{
    [JsonProperty("id")]
    public string Id { get; set; }
    [JsonProperty("listing")]
    public Listing Listing { get; set; }
    [JsonProperty("item")]
    public Item Item { get; set; }
}

public class Listing
{
    [JsonProperty("method")]
    public string Method { get; set; }
    [JsonProperty("indexed")]
    public string Indexed { get; set; }
    [JsonProperty("stash")]
    public Stash Stash { get; set; }
    [JsonProperty("price")]
    public Price Price { get; set; }
    [JsonProperty("fee")]
    public int Fee { get; set; }
    [JsonProperty("account")]
    public Account Account { get; set; }
    [JsonProperty("hideout_token")]
    public string HideoutToken { get; set; }
}

public class Stash
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("x")]
    public int X { get; set; }
    [JsonProperty("y")]
    public int Y { get; set; }
}

public class Account
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("online")]
    public object Online { get; set; }
}

public class Price
{
    [JsonProperty("type")]
    public string Type { get; set; }
    [JsonProperty("amount")]
    public int Amount { get; set; }
    [JsonProperty("currency")]
    public string Currency { get; set; }
}

public class Item
{
    [JsonProperty("realm")]
    public string Realm { get; set; }
    [JsonProperty("verified")]
    public bool Verified { get; set; }
    [JsonProperty("w")]
    public int W { get; set; }
    [JsonProperty("h")]
    public int H { get; set; }
    [JsonProperty("icon")]
    public string Icon { get; set; }
    [JsonProperty("league")]
    public string League { get; set; }
    [JsonProperty("id")]
    public string Id { get; set; }
    [JsonProperty("sockets")]
    public List<Socket> Sockets { get; set; }
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("typeLine")]
    public string TypeLine { get; set; }
    [JsonProperty("baseType")]
    public string BaseType { get; set; }
    [JsonProperty("rarity")]
    public string Rarity { get; set; }
    [JsonProperty("ilvl")]
    public int Ilvl { get; set; }
    [JsonProperty("identified")]
    public bool Identified { get; set; }
    [JsonProperty("note")]
    public string Note { get; set; }
    [JsonProperty("corrupted")]
    public bool Corrupted { get; set; }
    [JsonProperty("properties")]
    public List<Property> Properties { get; set; }
    [JsonProperty("requirements")]
    public List<Requirement> Requirements { get; set; }
    [JsonProperty("runeMods")]
    public List<string> RuneMods { get; set; }
    [JsonProperty("implicitMods")]
    public List<string> ImplicitMods { get; set; }
    [JsonProperty("explicitMods")]
    public List<string> ExplicitMods { get; set; }
    [JsonProperty("desecratedMods")]
    public List<string> DesecratedMods { get; set; }
    [JsonProperty("desecrated")]
    public bool Desecrated { get; set; }
    [JsonProperty("frameType")]
    public int FrameType { get; set; }
    [JsonProperty("socketedItems")]
    public List<SocketedItem> SocketedItems { get; set; }
    [JsonProperty("extended")]
    public Extended Extended { get; set; }
}

public class Socket
{
    [JsonProperty("group")]
    public int Group { get; set; }
    [JsonProperty("type")]
    public string Type { get; set; }
    [JsonProperty("item")]
    public string Item { get; set; }
}

public class Property
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("values")]
    public List<List<object>> Values { get; set; }
    [JsonProperty("displayMode")]
    public int DisplayMode { get; set; }
    [JsonProperty("type")]
    public int Type { get; set; }
}

public class Requirement
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("values")]
    public List<List<object>> Values { get; set; }
    [JsonProperty("displayMode")]
    public int DisplayMode { get; set; }
    [JsonProperty("type")]
    public int Type { get; set; }
}

public class SocketedItem
{
    [JsonProperty("realm")]
    public string Realm { get; set; }
    [JsonProperty("verified")]
    public bool Verified { get; set; }
    [JsonProperty("w")]
    public int W { get; set; }
    [JsonProperty("h")]
    public int H { get; set; }
    [JsonProperty("icon")]
    public string Icon { get; set; }
    [JsonProperty("stackSize")]
    public int StackSize { get; set; }
    [JsonProperty("maxStackSize")]
    public int MaxStackSize { get; set; }
    [JsonProperty("league")]
    public string League { get; set; }
    [JsonProperty("id")]
    public string Id { get; set; }
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("typeLine")]
    public string TypeLine { get; set; }
    [JsonProperty("baseType")]
    public string BaseType { get; set; }
    [JsonProperty("ilvl")]
    public int Ilvl { get; set; }
    [JsonProperty("identified")]
    public bool Identified { get; set; }
    [JsonProperty("properties")]
    public List<Property> Properties { get; set; }
    [JsonProperty("requirements")]
    public List<Requirement> Requirements { get; set; }
    [JsonProperty("explicitMods")]
    public List<string> ExplicitMods { get; set; }
    [JsonProperty("descrText")]
    public string DescrText { get; set; }
    [JsonProperty("frameType")]
    public int FrameType { get; set; }
    [JsonProperty("socket")]
    public int Socket { get; set; }
}

public class Extended
{
    [JsonProperty("dps")]
    public float Dps { get; set; }
    [JsonProperty("pdps")]
    public float Pdps { get; set; }
    [JsonProperty("edps")]
    public float Edps { get; set; }
    [JsonProperty("mods")]
    public Mods Mods { get; set; }
    [JsonProperty("hashes")]
    public Hashes Hashes { get; set; }
}

public class Mods
{
    [JsonProperty("explicit")]
    public List<ExplicitMod> Explicit { get; set; }
    [JsonProperty("implicit")]
    public List<ImplicitMod> Implicit { get; set; }
    [JsonProperty("desecrated")]
    public List<DesecratedMod> Desecrated { get; set; }
}

public class ExplicitMod
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("tier")]
    public string Tier { get; set; }
    [JsonProperty("level")]
    public int Level { get; set; }
    [JsonProperty("magnitudes")]
    public List<Magnitude> Magnitudes { get; set; }
}

public class ImplicitMod
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("tier")]
    public string Tier { get; set; }
    [JsonProperty("level")]
    public int Level { get; set; }
    [JsonProperty("magnitudes")]
    public List<Magnitude> Magnitudes { get; set; }
}

public class DesecratedMod
{
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("tier")]
    public string Tier { get; set; }
    [JsonProperty("level")]
    public int Level { get; set; }
    [JsonProperty("magnitudes")]
    public List<Magnitude> Magnitudes { get; set; }
}

public class Magnitude
{
    [JsonProperty("hash")]
    public string Hash { get; set; }
    [JsonProperty("min")]
    public string Min { get; set; }
    [JsonProperty("max")]
    public string Max { get; set; }
}

public class Hashes
{
    [JsonProperty("explicit")]
    public List<List<object>> Explicit { get; set; }
    [JsonProperty("implicit")]
    public List<List<object>> Implicit { get; set; }
    [JsonProperty("rune")]
    public List<List<object>> Rune { get; set; }
    [JsonProperty("desecrated")]
    public List<List<object>> Desecrated { get; set; }
}