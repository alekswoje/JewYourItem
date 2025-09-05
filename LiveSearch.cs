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

namespace LiveSearch;

public class LiveSearch : BaseSettingsPlugin<LiveSearchSettings>
{
    private List<SearchListener> _listeners = new List<SearchListener>();
    private string _sessionIdBuffer = "";
    private string _cfClearanceBuffer = "";
    private List<string> _recentResponses = new List<string>();
    private const int MaxResponses = 10;
    private static readonly HttpClient _httpClient = new HttpClient();

    public override bool Initialise()
    {
        _sessionIdBuffer = Settings.GlobalSessionId.Value;
        _cfClearanceBuffer = Settings.CfClearance.Value;

        Settings.AddFromUrl.OnPressed += AddSearchFromUrl;

        return true;
    }

    private void AddSearchFromUrl()
    {
        try
        {
            var uri = new Uri(Settings.TradeUrl.Value);
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segments.Length >= 5 && segments[0] == "trade2" && segments[1] == "search" && segments[2] == "poe2" && (segments.Length == 5 || segments[5] == "live"))
            {
                var league = Uri.UnescapeDataString(segments[3]);
                var searchId = segments[4];
                if (Settings.Groups.Count == 0)
                {
                    Settings.Groups.Add(new SearchGroup { Name = new TextNode("Default") });
                }
                Settings.Groups[0].Searches.Add(new LiveSearchInstanceSettings
                {
                    League = new TextNode(league),
                    SearchId = new TextNode(searchId)
                });
                LogMessage($"Added search from URL: {searchId} in league {league}");
                Settings.TradeUrl.Value = "";
            }
            else
            {
                LogError("Invalid trade URL format.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error parsing URL: {ex.Message}");
        }
    }

    private class SearchListener
    {
        public LiveSearchInstanceSettings Config { get; }
        public ClientWebSocket WebSocket { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public bool IsRunning { get; set; }

        public SearchListener(LiveSearchInstanceSettings config)
        {
            Config = config;
            Cts = new CancellationTokenSource();
        }

        public async void Start(Action<string> logMessage, Action<string> logError, string sessionId, string cfClearance)
        {
            if (string.IsNullOrEmpty(Config.League.Value) || string.IsNullOrEmpty(Config.SearchId.Value) || string.IsNullOrEmpty(sessionId))
            {
                logError("League, Search ID, or Session ID is empty for this search.");
                return;
            }

            if (IsRunning) return;

            IsRunning = true;
            WebSocket = new ClientWebSocket();
            var cookie = $"POESESSID={sessionId}";
            if (!string.IsNullOrEmpty(cfClearance))
            {
                cookie += $"; cf_clearance={cfClearance}";
            }
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

                logMessage($"Connected to WebSocket for search: {Config.SearchId.Value}");
                _ = ReceiveMessagesAsync(logMessage, logError, sessionId, cfClearance);
            }
            catch (Exception ex)
            {
                logError($"WebSocket connection failed for search {Config.SearchId.Value}: {ex.Message}");
                IsRunning = false;
            }
        }

        private async Task ReceiveMessagesAsync(Action<string> logMessage, Action<string> logError, string sessionId, string cfClearance)
        {
            var buffer = new byte[1024 * 4];
            while (WebSocket.State == WebSocketState.Open && !Cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), Cts.Token);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        int startIndex = message.IndexOf('{');
                        if (startIndex >= 0)
                        {
                            message = message.Substring(startIndex);
                            if (message.Contains("\"auth\""))
                            {
                                logMessage("Auth message received.");
                                continue;
                            }
                            await ProcessMessage(message, logMessage, logError, sessionId, cfClearance);
                        }
                        else
                        {
                            logError($"Invalid message format: {message}");
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logMessage($"WebSocket closed by server for {Config.SearchId.Value}.");
                        break;
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

        private async Task ProcessMessage(string message, Action<string> logMessage, Action<string> logError, string sessionId, string cfClearance)
        {
            try
            {
                var wsResponse = JsonConvert.DeserializeObject<WsResponse>(message);
                if (wsResponse.New != null && wsResponse.New.Length > 0)
                {
                    string ids = string.Join(",", wsResponse.New);
                    string fetchUrl = $"https://www.pathofexile.com/api/trade2/fetch/{ids}?query={Config.SearchId.Value}&realm=poe2";

                    using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
                    {
                        var cookie = $"POESESSID={sessionId}";
                        if (!string.IsNullOrEmpty(cfClearance))
                        {
                            cookie += $"; cf_clearance={cfClearance}";
                        }
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
                                foreach (var item in itemResponse.Result)
                                {
                                    string name = item.Item.Name;
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        name = item.Item.TypeLine;
                                    }
                                    string price = item.Listing.Price?.Type + " " + item.Listing.Price?.Amount + " " + item.Listing.Price?.Currency;
                                    logMessage($"{name} - {price}");
                                }
                            }
                            else
                            {
                                logError($"Fetch failed: {response.StatusCode}");
                            }
                        }
                    }
                }
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
    }

    public override void AreaChange(AreaInstance area)
    {
        StopAll();
    }

    public override void Tick()
    {
        if (!Settings.Enable.Value) return;

        var allConfigs = Settings.Groups
            .Where(g => g.Enable.Value)
            .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
            .ToList();

        if (allConfigs.Count > 20)
        {
            LogError("Exceeded max 20 searches; disabling extras.");
            allConfigs = allConfigs.Take(20).ToList();
        }

        foreach (var config in allConfigs)
        {
            var listener = _listeners.FirstOrDefault(l => l.Config == config);
            if (listener == null)
            {
                listener = new SearchListener(config);
                _listeners.Add(listener);
                listener.Start(LogMessage, LogError, Settings.GlobalSessionId.Value, Settings.CfClearance.Value);
            }
            else if (!listener.IsRunning)
            {
                listener.Start(LogMessage, LogError, Settings.GlobalSessionId.Value, Settings.CfClearance.Value);
            }
        }

        var activeConfigs = allConfigs.ToHashSet();
        foreach (var listener in _listeners.ToList())
        {
            if (!activeConfigs.Contains(listener.Config))
            {
                listener.Stop();
                _listeners.Remove(listener);
            }
        }
    }

    public override void Render()
    {
        Graphics.DrawText($"LiveSearch: {_listeners.Count(l => l.IsRunning)} running", new Vector2(100, 100), Color.Green);

        var y = 120;
        foreach (var msg in _recentResponses)
        {
            Graphics.DrawText(msg, new Vector2(100, y), Color.White);
            y += 20;
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
        ImGui.Text("Global Session ID:");
        ImGui.SameLine();
        ImGui.InputText("##GlobalSessionId", ref _sessionIdBuffer, 100, ImGuiInputTextFlags.Password);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Settings.GlobalSessionId.Value = _sessionIdBuffer;
        }

        ImGui.Text("Cloudflare Clearance:");
        ImGui.SameLine();
        ImGui.InputText("##CfClearance", ref _cfClearanceBuffer, 200, ImGuiInputTextFlags.Password);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Settings.CfClearance.Value = _cfClearanceBuffer;
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
    [JsonProperty("price")]
    public Price Price { get; set; }
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
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("typeLine")]
    public string TypeLine { get; set; }
}