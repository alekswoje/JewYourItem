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
using NAudio.Wave;
using System.Media;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace JewYourItem;

public class RateLimiter
{
    private readonly Dictionary<string, RateLimitState> _rateLimits = new Dictionary<string, RateLimitState>();
    private readonly object _lock = new object();
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;

    public RateLimiter(Action<string> logMessage, Action<string> logError)
    {
        _logMessage = logMessage;
        _logError = logError;
    }

    public class RateLimitState
    {
        public int Hits { get; set; }
        public int Max { get; set; }
        public DateTime ResetTime { get; set; }
        public int Period { get; set; } // in seconds
        public int Penalty { get; set; } // in seconds
        public int SafeMax { get; set; } // 50% of actual max for safety
        public int EmergencyThreshold { get; set; } // 40% of actual max for emergency brake
        public DateTime LastRequestTime { get; set; }
        public int RequestCount { get; set; }
        public int BurstCount { get; set; } // Track burst requests
    }

    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
            // Parse X-Rate-Limit-Rules header
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rulesHeader))
            {
                var rules = string.Join(",", rulesHeader).Split(',');
                foreach (var rule in rules)
                {
                    var parts = rule.Trim().Split(':');
                    if (parts.Length >= 4)
                    {
                        var scope = parts[0];
                        if (int.TryParse(parts[1], out var hits) &&
                            int.TryParse(parts[2], out var period) &&
                            int.TryParse(parts[3], out var penalty))
                        {
                            lock (_lock)
                            {
                                _rateLimits[scope] = new RateLimitState
                                {
                                    Max = hits,
                                    Period = period,
                                    Penalty = penalty,
                                    ResetTime = DateTime.Now.AddSeconds(period),
                                    SafeMax = Math.Max(1, hits / 2), // Use only 50% of actual limit
                                    EmergencyThreshold = Math.Max(1, (int)(hits * 0.4)), // Emergency brake at 40%
                                    LastRequestTime = DateTime.Now,
                                    RequestCount = 0,
                                    BurstCount = 0
                                };
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account", out var accountHeader))
            {
                var accountData = string.Join(",", accountHeader).Split(',');
                foreach (var data in accountData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        var scope = "account";
                        if (int.TryParse(parts[0], out var hits) &&
                            int.TryParse(parts[1], out var max) &&
                            int.TryParse(parts[2], out var remaining))
                        {
                            lock (_lock)
                            {
                                if (_rateLimits.ContainsKey(scope))
                                {
                                    _rateLimits[scope].Hits = hits;
                                    _rateLimits[scope].Max = max;
                                }
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account-State header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account-State", out var stateHeader))
            {
                var state = string.Join(",", stateHeader);
                _logMessage($"Rate limit state: {state}");
            }

            // Parse X-Rate-Limit-Account-Max header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account-Max", out var maxHeader))
            {
                var max = string.Join(",", maxHeader);
                _logMessage($"Rate limit max: {max}");
            }
        }
        catch (Exception ex)
        {
            _logError($"Error parsing rate limit headers: {ex.Message}");
        }
    }

    public async Task<bool> CheckAndWaitIfNeeded(string scope = "account")
    {
        var adaptiveDelay = GetAdaptiveDelay(scope);
        if (adaptiveDelay > 0)
        {
            _logMessage($"Adaptive delay: {scope} usage high, waiting {adaptiveDelay}ms...");
            await Task.Delay(adaptiveDelay);
        }

        RateLimitState state;
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return true; // No rate limit info, proceed

            state = _rateLimits[scope];
        }

        var usagePercentage = (double)state.Hits / state.Max;

        // If we're at 80% or more of the limit, wait
        if (usagePercentage >= 0.8)
        {
            var waitTime = CalculateWaitTime(state);
            if (waitTime > 0)
            {
                _logMessage($"Rate limit warning: {scope} at {usagePercentage:P1} ({state.Hits}/{state.Max}). Waiting {waitTime}ms...");
                await Task.Delay(waitTime);
                return true;
            }
        }

        return true;
    }

    public async Task<int> HandleRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Parse Retry-After header
            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                if (int.TryParse(retryAfterHeader.First(), out var retryAfterSeconds))
                {
                    var waitTime = retryAfterSeconds * 1000; // Convert to milliseconds
                    _logMessage($"Rate limited! Waiting {retryAfterSeconds} seconds before retry...");
                    await Task.Delay(waitTime);
                    return waitTime;
                }
            }
            else
            {
                // Fallback to 60 seconds if no Retry-After header
                _logMessage("Rate limited! No Retry-After header, waiting 60 seconds...");
                await Task.Delay(60000);
                return 60000;
            }
        }

        // Parse rate limit headers for future requests
        ParseRateLimitHeaders(response);
        return 0;
    }

    private int CalculateWaitTime(RateLimitState state)
    {
        var timeUntilReset = (state.ResetTime - DateTime.Now).TotalMilliseconds;
        if (timeUntilReset > 0)
        {
            // Wait until reset time, but cap at penalty time
            return Math.Min((int)timeUntilReset, state.Penalty * 1000);
        }
        return 0;
    }

    public void LogCurrentState()
    {
        lock (_lock)
        {
            foreach (var kvp in _rateLimits)
            {
                var state = kvp.Value;
                var usagePercentage = (double)state.Hits / state.Max;
                _logMessage($"Rate limit {kvp.Key}: {state.Hits}/{state.Max} ({usagePercentage:P1}) - Resets in {(state.ResetTime - DateTime.Now).TotalSeconds:F1}s");
            }
        }
    }

    public int GetAdaptiveDelay(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return 1000; // Default 1 second delay if no info

            var state = _rateLimits[scope];
            var now = DateTime.Now;
            
            // Update request tracking
            state.RequestCount++;
            if ((now - state.LastRequestTime).TotalSeconds < 1)
            {
                state.BurstCount++;
            }
            else
            {
                state.BurstCount = 1; // Reset burst count
            }
            state.LastRequestTime = now;

            // Calculate usage against our SAFE limit (50% of actual)
            var safeUsagePercentage = (double)state.Hits / state.SafeMax;
            var emergencyUsagePercentage = (double)state.Hits / state.EmergencyThreshold;
            var usagePercentage = (double)state.Hits / state.Max;

            // Calculate adaptive delay based on usage percentage
            if (usagePercentage >= 0.9)
                return 5000; // 5 seconds if at 90%+
            else if (usagePercentage >= 0.8)
                return 2000; // 2 seconds if at 80%+
            else if (usagePercentage >= 0.6)
                return 1000; // 1 second if at 60%+
            else if (usagePercentage >= 0.4)
                return 500;  // 0.5 seconds if at 40%+
            
            return 0; // No delay if under 40%
        }
    }
}

public class ConservativeRateLimiter
{
    private readonly Dictionary<string, ConservativeRateLimitState> _rateLimits = new Dictionary<string, ConservativeRateLimitState>();
    private readonly object _lock = new object();
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;

    public ConservativeRateLimiter(Action<string> logMessage, Action<string> logError)
    {
        _logMessage = logMessage;
        _logError = logError;
    }

    public class ConservativeRateLimitState
    {
        public int Hits { get; set; }
        public int Max { get; set; }
        public DateTime ResetTime { get; set; }
        public int Period { get; set; }
        public int Penalty { get; set; }
        public int SafeMax { get; set; } // 50% of actual max for safety
        public int EmergencyThreshold { get; set; } // 40% of actual max for emergency brake
        public DateTime LastRequestTime { get; set; }
        public int RequestCount { get; set; }
        public int BurstCount { get; set; }
        public int ConsecutiveHighUsage { get; set; } // Track consecutive high usage periods
    }

    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
            // Parse X-Rate-Limit-Rules header
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rulesHeader))
            {
                var rules = string.Join(",", rulesHeader).Split(',');
                foreach (var rule in rules)
                {
                    var parts = rule.Trim().Split(':');
                    if (parts.Length >= 4)
                    {
                        var scope = parts[0];
                        if (int.TryParse(parts[1], out var hits) &&
                            int.TryParse(parts[2], out var period) &&
                            int.TryParse(parts[3], out var penalty))
                        {
                            lock (_lock)
                            {
                                _rateLimits[scope] = new ConservativeRateLimitState
                                {
                                    Max = hits,
                                    Period = period,
                                    Penalty = penalty,
                                    ResetTime = DateTime.Now.AddSeconds(period),
                                    SafeMax = Math.Max(1, hits / 2), // Use only 50% of actual limit
                                    EmergencyThreshold = Math.Max(1, (int)(hits * 0.4)), // Emergency brake at 40%
                                    LastRequestTime = DateTime.Now,
                                    RequestCount = 0,
                                    BurstCount = 0,
                                    ConsecutiveHighUsage = 0
                                };
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account", out var accountHeader))
            {
                var accountData = string.Join(",", accountHeader).Split(',');
                foreach (var data in accountData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        var scope = "account";
                        if (int.TryParse(parts[0], out var hits) &&
                            int.TryParse(parts[1], out var max) &&
                            int.TryParse(parts[2], out var remaining))
                        {
                            lock (_lock)
                            {
                                if (_rateLimits.ContainsKey(scope))
                                {
                                    _rateLimits[scope].Hits = hits;
                                    _rateLimits[scope].Max = max;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"Error parsing rate limit headers: {ex.Message}");
        }
    }

    public async Task<bool> CheckAndWaitIfNeeded(string scope = "account")
    {
        var delay = GetConservativeDelay(scope);
        if (delay > 0)
        {
            _logMessage($"🛡️ CONSERVATIVE RATE LIMITING: {scope} - waiting {delay}ms for safety...");
            await Task.Delay(delay);
        }
        return true;
    }

    public async Task<int> HandleRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Parse Retry-After header
            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                if (int.TryParse(retryAfterHeader.First(), out var retryAfterSeconds))
                {
                    var waitTime = retryAfterSeconds * 1000;
                    _logMessage($"🚨 RATE LIMITED! Waiting {retryAfterSeconds} seconds before retry...");
                    await Task.Delay(waitTime);
                    return waitTime;
                }
            }
            else
            {
                _logMessage("🚨 RATE LIMITED! No Retry-After header, waiting 60 seconds...");
                await Task.Delay(60000);
                return 60000;
            }
        }

        // Parse rate limit headers for future requests
        ParseRateLimitHeaders(response);
        return 0;
    }

    private int GetConservativeDelay(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return 2000; // Default 2 second delay if no info

            var state = _rateLimits[scope];
            var now = DateTime.Now;
            
            // Update request tracking
            state.RequestCount++;
            if ((now - state.LastRequestTime).TotalSeconds < 1)
            {
                state.BurstCount++;
            }
            else
            {
                state.BurstCount = 1;
            }
            state.LastRequestTime = now;

            // Calculate usage against our SAFE limit (50% of actual)
            var safeUsagePercentage = (double)state.Hits / state.SafeMax;
            var emergencyUsagePercentage = (double)state.Hits / state.EmergencyThreshold;

            // EMERGENCY BRAKE: If we're approaching our emergency threshold (40% of actual limit)
            if (emergencyUsagePercentage >= 1.0)
            {
                state.ConsecutiveHighUsage++;
                _logMessage($"🚨 EMERGENCY BRAKE: {scope} at {emergencyUsagePercentage:P1} of emergency threshold! Waiting 15 seconds...");
                return 15000; // 15 seconds emergency brake
            }

            // BURST PROTECTION: If we're making too many requests too quickly
            if (state.BurstCount > 2) // More conservative than before
            {
                _logMessage($"⚠️ BURST PROTECTION: {scope} made {state.BurstCount} requests in 1 second. Waiting 5 seconds...");
                return 5000; // 5 seconds burst protection
            }

            // CONSERVATIVE DELAYS: Much more aggressive than before
            if (safeUsagePercentage >= 0.7) // 70% of our 50% = 35% of actual limit
            {
                state.ConsecutiveHighUsage++;
                return 8000; // 8 seconds if at 35% of actual limit
            }
            else if (safeUsagePercentage >= 0.5) // 50% of our 50% = 25% of actual limit
            {
                state.ConsecutiveHighUsage++;
                return 5000; // 5 seconds if at 25% of actual limit
            }
            else if (safeUsagePercentage >= 0.3) // 30% of our 50% = 15% of actual limit
            {
                state.ConsecutiveHighUsage++;
                return 3000; // 3 seconds if at 15% of actual limit
            }
            else if (safeUsagePercentage >= 0.1) // 10% of our 50% = 5% of actual limit
            {
                state.ConsecutiveHighUsage = 0; // Reset consecutive high usage
                return 1500; // 1.5 seconds if at 5% of actual limit
            }
            
            // Reset consecutive high usage if we're in safe zone
            state.ConsecutiveHighUsage = 0;
            
            // MINIMUM DELAY: Always have a delay to be safe
            return 1000; // 1 second minimum delay
        }
    }

    public void LogCurrentState()
    {
        lock (_lock)
        {
            foreach (var kvp in _rateLimits)
            {
                var state = kvp.Value;
                var safeUsagePercentage = (double)state.Hits / state.SafeMax;
                var actualUsagePercentage = (double)state.Hits / state.Max;
                _logMessage($"🛡️ Rate limit {kvp.Key}: {state.Hits}/{state.Max} (Actual: {actualUsagePercentage:P1}, Safe: {safeUsagePercentage:P1}) - Resets in {(state.ResetTime - DateTime.Now).TotalSeconds:F1}s");
            }
        }
    }
}

public class JewYourItem : BaseSettingsPlugin<JewYourItemSettings>
{
    // Windows API declarations for mouse clicks
    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    static extern void SetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Mouse event flags
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x08;
    private const uint MOUSEEVENTF_RIGHTUP = 0x10;

    // Key event flags
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;

    private List<SearchListener> _listeners = new List<SearchListener>();
    private string _sessionIdBuffer = "";
    private List<string> _recentResponses = new List<string>();
    private const int MaxResponses = 5;
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli });
    private Queue<(string name, string price, string hideoutToken, int x, int y)> _recentItems = new Queue<(string, string, string, int, int)>(MaxResponses);
    private bool _playSound = true;
    private bool _lastHotkeyState = false;
    private DateTime _lastTpTime = DateTime.MinValue;
    private bool _settingsUpdated = false;
    private SearchListener _activeListener;
    private Dictionary<JewYourItemInstanceSettings, DateTime> _lastRestartTimes = new Dictionary<JewYourItemInstanceSettings, DateTime>();
    private const int RestartCooldownSeconds = 300; // Increased to 5 minutes for safety
    private bool _lastEnableState = true;
    private string _lastActiveConfigsHash = "";
    private ConservativeRateLimiter _rateLimiter;
    private bool _lastPurchaseWindowVisible = false;
    private static int _globalConnectionAttempts = 0;
    private static DateTime _pluginStartTime = DateTime.Now;
    private static bool _emergencyShutdown = false;
    private DateTime _lastTickProcessTime = DateTime.MinValue;
    private DateTime _lastAreaChangeTime = DateTime.MinValue;
    private DateTime _lastSettingsChangeTime = DateTime.MinValue;
    private bool _areaChangeCooldownLogged = false;

    public override bool Initialise()
    {
        _rateLimiter = new ConservativeRateLimiter(LogMessage, LogError);
        _sessionIdBuffer = Settings.SessionId.Value ?? "";
        if (string.IsNullOrEmpty(_sessionIdBuffer))
        {
            LogMessage("Warning: Session ID is empty. Please set it in the settings.");
        }
        
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
        LogMessage(":::::::::::::%:::=@:::::::::%:::=@:-@=:*@:%+@@::-:-+:::::::::-::-++@+=::::::::::::-@#:::::::::::: ");
        LogMessage("::::::::::::@-:::@::::::::::@+::-@:-@*:@@:-=@@=:+++-::::::::::::::::::::::::::::::::*#::::::::::: ");
        LogMessage(":::::::::::@::::=+::::::::::-@::+@=:%*:@@:-@@#=:=*=::::::::::::::::::::::::::::::::::-@:::::::::: ");
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
        LogMessage("::::::::-*:#=::::*#@:::::::::::::::::%-::@@-@@@@*@*@+@#@*@@-*#::=@@@#@%@@@*@=::::::::::::::::::::: ");
        LogMessage("::::::::@-:::::::=#%=::::::::::::::::-@::@@@@*%@@-@@@@@@@@*@@+%@+:::::+@@@#:#@@:::::::::::::::::::: ");
        LogMessage("::::::::-@-::::::::@=#:::::::::::::::::-@=*:=@#+##@@=@@@@@@@@@@@%@@*:::::::-#@#@@-:::::::::::::::::: ");
        LogMessage("::::::::@%:::::::::#-%::::::::::::::::::=@:%-@@%#@#@@@@@+#@@=@@@@@@@@@@@@@@*@@@@++%#*%:::::::::::::: ");
        LogMessage("::::::::%+%+::::::::=+#:::::::::::::::::::=%-*=%%%*#=@@=@#@@*@@@#@@@@@@@*@*@@@@@@#:=--@:::::::::::::: ");
        LogMessage("::::::::@::+:::::::::@==:::::::::::::::::::*-*+*%@@@@#@@@@@%@@@%@@@@#@#%@@@@@+::::::+#::::::::::::::: ");
        LogMessage("::::::=+:::::::::::::+*#::::::::::::::::::::#+@:*@+@@**@@@@@@@@@@@%@@@@@@%@@:::::*@==+%+*+-:::::::::: ");
        LogMessage("::::::+=:::::::::::::%@-::::::::::::::::::::#*+:-@@*-@@=#@+@@@@@@#@@@%*@@@:::::-@+:::@:::=+::::::::: ");
        LogMessage("::::::*-:::::::::::::+*-:::::::::::::::::::::@%::-#:@@@+@@@@#@@%@@+@=@@@-::::::-:@%-%=::-@@#:::::::: ");
        LogMessage("::::::+-::::::::::::::@+=:::::::::::::::::::::*+*%=%:=@+@@@@+=@@@@@@@@%-=::::::::@@+::+=:::#@::::::: ");
        LogMessage("::::::=+::::::::::::::+##+:::::::::::::::::::::%+*:=*@@:*@#@*@@@@#@@%:+::::::::::::::-+:::#@%::::::: ");
        LogMessage("::::::::::::::::::::::#--::::::::::::::::::-@@%%#::#@%:=#@@@*:::::=%-::::::::::::*%-::+*#-::#:::::: ");
        LogMessage("::::::::::::::::::::::@:::::::::::::::::=@%-::+@#*==-::::::::::::::@::::::%@=-=#@@@:::::::+*:::::: ");
        LogMessage(":::::::::::::::::::::::%::::::::::::::::@=:::-*:::::::::::::::::::*=@:::::::::::::-::+@+-*%::::::: ");
        LogMessage("::::::::::::::::::::::+#::::::::::::::::::::@-:::::::::::::::::::-**=:::+@@*@#%@*=@*@#@-#:::::::: ");
        LogMessage(":::::::::::::::::::::::+*::::::::::::::::::*@:::::::::::::::::::::*+=::-@+-@:%+*-=*#=:-@-:::::::: ");
        LogMessage("::::::::::::::::::::::::+%:::::::::::::::::*::::::::+-:::::::::::==#:*:+*::*:::*::+@-:*=::::::::: ");
        LogMessage("::::::::::::::::::::::::::#::::::::::::::::=::::::::#-::::::::::=@%===*-@@@@:-*@@#@-=*-:::::::::: ");
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
        public bool IsConnecting { get; set; } = false;
        public DateTime LastConnectionAttempt { get; set; } = DateTime.MinValue;
        public DateTime LastErrorTime { get; set; } = DateTime.MinValue;
        public int ConnectionAttempts { get; set; } = 0;
        private readonly object _connectionLock = new object();
        private StringBuilder _messageBuffer = new StringBuilder();
        private readonly Action<string> _logMessage;
        private readonly Action<string> _logError;

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

                // EMERGENCY: Max connection attempts per hour (extremely limited)
                if (ConnectionAttempts >= 3 && (DateTime.Now - LastConnectionAttempt).TotalHours < 1)
                {
                    _logMessage($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} exceeded max connection attempts (3 per HOUR)");
                    return;
                }

                // EMERGENCY: Global session connection limit
                if (ConnectionAttempts >= 10)
                {
                    _logMessage($"🚨 EMERGENCY BLOCK: Search {Config.SearchId.Value} exceeded TOTAL session limit (10 attempts EVER)");
                    return;
                }

                // Reset connection attempts counter if more than 1 HOUR has passed
                if ((DateTime.Now - LastConnectionAttempt).TotalHours >= 1)
                {
                    ConnectionAttempts = Math.Min(ConnectionAttempts, 5); // Partial reset, keep some penalty
                }

                // EMERGENCY: Check global limits
                JewYourItem._globalConnectionAttempts++;
                if (JewYourItem._globalConnectionAttempts > 3)
                {
                    _logMessage($"🚨🚨🚨 GLOBAL EMERGENCY SHUTDOWN: Too many connection attempts ({JewYourItem._globalConnectionAttempts})");
                    JewYourItem._emergencyShutdown = true;
                    return;
                }

                // Set connection state flags
                IsConnecting = true;
                LastConnectionAttempt = DateTime.Now;
                ConnectionAttempts++;
                
                _logMessage($"🔌 STARTING CONNECTION: Search {Config.SearchId.Value} (Attempt #{ConnectionAttempts}, Global: {JewYourItem._globalConnectionAttempts})");
            }
            try
            {
                // Check rate limits before connecting WebSocket
                if (_parent._rateLimiter != null)
                {
                    await _parent._rateLimiter.CheckAndWaitIfNeeded("client");
                }
                
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
                lock (_connectionLock)
                {
                    IsConnecting = false;
                    IsRunning = false;
                }
                
                _logError($"❌ CONNECTION FAILED: Search {Config.SearchId.Value}: {ex.Message}");
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
                            // Check rate limits before each batch request
                            await _parent._rateLimiter.CheckAndWaitIfNeeded("account");

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
                                    var itemResponse = JsonConvert.DeserializeObject<ItemFetchResponse>(content);
                                    if (itemResponse.Result != null && itemResponse.Result.Any())
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
                                            _parent._recentItems.Enqueue((name, price, item.Listing.HideoutToken, x, y));
                                            if (_parent._recentItems.Count > MaxResponses)
                                                _parent._recentItems.Dequeue();
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
                                                if ((DateTime.Now - _parent._lastTpTime).TotalSeconds >= _parent.Settings.TpCooldown.Value)
                                                {
                                                    _parent.TravelToHideout();
                                                    _parent._recentItems.Clear();
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
                                        if (_parent._recentItems.Count > 0)
                                        {
                                            _parent._recentItems.Dequeue();
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
                
                // Calculate item position within the stash panel
                int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth / 2));
                int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight / 2));
                
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

    private async void TravelToHideout()
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Teleport blocked: Plugin is disabled");
            return;
        }

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
            // Ensure rate limiter is initialized
            if (_rateLimiter == null)
            {
                _rateLimiter = new ConservativeRateLimiter(LogMessage, LogError);
            }
            
            // Check rate limits before making request
            await _rateLimiter.CheckAndWaitIfNeeded("account");
            
            LogMessage("Sending teleport request...");
            var response = _httpClient.SendAsync(request).Result;
            LogMessage($"Response status: {response.StatusCode}");
            
            // Handle rate limiting
            if (_rateLimiter != null)
            {
                var rateLimitWaitTime = await _rateLimiter.HandleRateLimitResponse(response);
                if (rateLimitWaitTime > 0)
                {
                    return; // Rate limited, wait and return
                }
            }
            
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Teleport failed: {response.StatusCode}");
                var responseContent = response.Content.ReadAsStringAsync().Result;
                LogError($"Response content: {responseContent}");
                
                // Remove failed item and try next one if available
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound || 
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    LogMessage($"🗑️ ITEM EXPIRED: Removing expired item '{name}' and trying next...");
                    _recentItems.Dequeue();
                    
                    // Try the next item if available
                    if (_recentItems.Count > 0)
                    {
                        LogMessage($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                        await Task.Delay(500); // Small delay before retry
                        TravelToHideout(); // Recursive call to try next item
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have expired");
                    }
                }
            }
            else
            {
                LogMessage("Teleport to hideout successful!");
                _recentItems.Clear();
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

    public async Task PerformCtrlLeftClickAsync()
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Auto buy blocked: Plugin is disabled");
            return;
        }

        try
        {
            LogMessage("🖱️ AUTO BUY: Performing Ctrl+Left Click...");
            
            // Small delay to ensure mouse position is set
            await Task.Delay(50);
            
            // Press Ctrl key down
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            
            // Small delay
            await Task.Delay(10);
            
            // Perform left mouse button down
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            
            // Small delay
            await Task.Delay(10);
            
            // Perform left mouse button up
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            
            // Small delay
            await Task.Delay(10);
            
            // Release Ctrl key
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            
            LogMessage("✅ AUTO BUY: Ctrl+Left Click completed!");
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO BUY FAILED: {ex.Message}");
        }
    }

    private async void MoveMouseToItemLocation(int x, int y)
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Mouse movement blocked: Plugin is disabled");
            return;
        }

        try
        {
            var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (!purchaseWindow.IsVisible)
            {
                LogMessage("MoveMouseToItemLocation: Purchase window is not visible");
                return;
            }

            var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
            if (stashPanel == null)
            {
                LogMessage("MoveMouseToItemLocation: Stash panel is null");
                return;
            }

            var rect = stashPanel.GetClientRectCache;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                LogMessage("MoveMouseToItemLocation: Invalid stash panel dimensions");
                return;
            }

            float cellWidth = rect.Width / 12.0f;
            float cellHeight = rect.Height / 12.0f;
            var topLeft = rect.TopLeft;
            
            // Calculate item position within the stash panel
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth / 2));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight / 2));
            
            // Get game window position
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            
            // Calculate final screen position
            int finalX = windowPos.X + itemX;
            int finalY = windowPos.Y + itemY;
            
            // Move mouse cursor
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
            
            LogMessage($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
            
            // Auto Buy: Perform Ctrl+Left Click if enabled
            if (Settings.AutoBuy.Value)
            {
                LogMessage("🛒 AUTO BUY: Enabled, performing purchase click...");
                await Task.Delay(100); // Small delay to ensure mouse movement is complete
                await PerformCtrlLeftClickAsync();
            }
        }
        catch (Exception ex)
        {
            LogError($"MoveMouseToItemLocation failed: {ex.Message}");
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        LogMessage($"🌍 AREA CHANGE: {area?.Area?.Name ?? "Unknown"} - NOT stopping listeners (live searches continue)");
        _lastAreaChangeTime = DateTime.Now;
        
        // DON'T stop all listeners - let them continue running
        // Live searches should persist across zone changes
        // Only clear recent items as they're location-specific
        _recentItems.Clear();
        LogMessage("📦 Cleared recent items due to area change");
    }

    public async Task PlaySoundWithNAudio(string soundPath, Action<string> logMessage, Action<string> logError)
    {
        try
        {
            // Use Task.Run to avoid blocking the UI thread
            await Task.Run(() =>
            {
                using (var audioFile = new AudioFileReader(soundPath))
                using (var outputDevice = new WaveOutEvent())
                {
                    outputDevice.Init(audioFile);
                    outputDevice.Play();
                    
                    // Wait for playback to complete
                    while (outputDevice.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(100);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logError($"NAudio playback failed: {ex.Message}");
            
            // Fallback to System.Media.SoundPlayer if NAudio fails
            try
            {
                logMessage("Attempting fallback to System.Media.SoundPlayer...");
                using (var player = new System.Media.SoundPlayer(soundPath))
                {
                    player.Play();
                }
                logMessage("Fallback audio playback successful");
            }
            catch (Exception fallbackEx)
            {
                logError($"Fallback audio playback also failed: {fallbackEx.Message}");
            }
        }
    }

    public override async void Tick()
    {
        // CRITICAL: IMMEDIATE PLUGIN DISABLE CHECK - HIGHEST PRIORITY
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 PLUGIN DISABLED: Stopping all listeners immediately");
            ForceStopAll();
            return;
        }

        // EMERGENCY SHUTDOWN CHECK
        if (JewYourItem._emergencyShutdown)
        {
            LogError("🚨🚨🚨 EMERGENCY SHUTDOWN ACTIVE - Plugin disabled due to connection spam protection");
            Settings.Enable.Value = false;
            ForceStopAll();
            return;
        }

        // Ensure rate limiter is initialized
        if (_rateLimiter == null)
        {
            _rateLimiter = new ConservativeRateLimiter(LogMessage, LogError);
        }

        // Check if enable state changed
        if (_lastEnableState != Settings.Enable.Value)
        {
            _lastEnableState = Settings.Enable.Value;
            if (!Settings.Enable.Value)
            {
                LogMessage("🛑 PLUGIN JUST DISABLED: Force stopping all listeners");
                ForceStopAll();
                return;
            }
        }

        // CRITICAL: Throttle listener management EXCEPT for immediate settings changes
        bool recentSettingsChange = (DateTime.Now - _lastSettingsChangeTime).TotalSeconds < 1;
        if ((DateTime.Now - _lastTickProcessTime).TotalSeconds < 2 && !recentSettingsChange)
        {
            // ONLY process basic functionality IF plugin is enabled
            if (Settings.Enable.Value)
            {
                bool hotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
                if (hotkeyState && !_lastHotkeyState)
                {
                    LogMessage($"Hotkey {Settings.TravelHotkey.Value} pressed");
                    TravelToHideout();
                }
                _lastHotkeyState = hotkeyState;

                // Check if purchase window just became visible and move mouse to recent item
                bool purchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
                if (purchaseWindowVisible && !_lastPurchaseWindowVisible && Settings.MoveMouseToItem.Value && _recentItems.Count > 0)
                {
                    LogMessage("Purchase window opened - moving mouse to most recent item");
                    var (name, price, hideoutToken, x, y) = _recentItems.Peek();
                    MoveMouseToItemLocation(x, y);
                }
                _lastPurchaseWindowVisible = purchaseWindowVisible;
            }
            return; // Skip listener management
        }

        // AREA CHANGE PROTECTION: Reduced delay, can be overridden by settings changes
        bool recentAreaChange = (DateTime.Now - _lastAreaChangeTime).TotalSeconds < 5; // Reduced from 10 to 5 seconds
        if (recentAreaChange && !recentSettingsChange)
        {
            // Only log once to avoid spam
            if (!_areaChangeCooldownLogged)
            {
                LogMessage($"🌍 AREA CHANGE COOLDOWN: Skipping listener management for 5s after area change (can be overridden by settings changes)");
                _areaChangeCooldownLogged = true;
            }
            
            // ONLY process basic functionality IF plugin is enabled
            if (Settings.Enable.Value)
            {
                bool areaChangeHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
                if (areaChangeHotkeyState && !_lastHotkeyState)
                {
                    LogMessage($"Hotkey {Settings.TravelHotkey.Value} pressed");
                    TravelToHideout();
                }
                _lastHotkeyState = areaChangeHotkeyState;

                bool areaChangePurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
                if (areaChangePurchaseWindowVisible && !_lastPurchaseWindowVisible && Settings.MoveMouseToItem.Value && _recentItems.Count > 0)
                {
                    LogMessage("Purchase window opened - moving mouse to most recent item");
                    var (name, price, hideoutToken, x, y) = _recentItems.Peek();
                    MoveMouseToItemLocation(x, y);
                }
                _lastPurchaseWindowVisible = areaChangePurchaseWindowVisible;
            }
            return;
        }
        else if (!recentAreaChange)
        {
            // Reset the logging flag when cooldown is over
            _areaChangeCooldownLogged = false;
        }

        _lastTickProcessTime = DateTime.Now;
        if (recentSettingsChange)
        {
            LogMessage("🔄 TICK: Processing listener management (INSTANT - settings changed)...");
        }
        else
        {
            LogMessage("🔄 TICK: Processing listener management...");
        }

        bool currentHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
        if (currentHotkeyState && !_lastHotkeyState)
        {
            LogMessage($"Hotkey {Settings.TravelHotkey.Value} pressed");
            TravelToHideout();
        }
        _lastHotkeyState = currentHotkeyState;

        // Check if purchase window just became visible and move mouse to recent item
        bool currentPurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
        if (currentPurchaseWindowVisible && !_lastPurchaseWindowVisible && Settings.MoveMouseToItem.Value && _recentItems.Count > 0)
        {
            LogMessage("Purchase window opened - moving mouse to most recent item");
            var (name, price, hideoutToken, x, y) = _recentItems.Peek();
            MoveMouseToItemLocation(x, y);
        }
        _lastPurchaseWindowVisible = currentPurchaseWindowVisible;

        var allConfigs = Settings.Groups
            .Where(g => g.Enable.Value)
            .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
            .ToList();

        if (allConfigs.Count > 20)
        {
            LogError("Exceeded max 20 searches; disabling extras.");
            allConfigs = allConfigs.Take(20).ToList();
        }

        // Create a set of active config identifiers for comparison
        var activeConfigIds = allConfigs
            .Select(c => $"{c.League.Value}|{c.SearchId.Value}")
            .ToHashSet();

        // Check if the active configs have changed (for immediate response to settings changes)
        var currentConfigsHash = string.Join("|", activeConfigIds.OrderBy(x => x));
        bool settingsChanged = _lastActiveConfigsHash != currentConfigsHash;
        if (settingsChanged)
        {
            _lastActiveConfigsHash = currentConfigsHash;
            _lastSettingsChangeTime = DateTime.Now;
            LogMessage($"🔄 SETTINGS CHANGED: Config changed from {_listeners.Count} to {allConfigs.Count} searches - processing immediately");
            
            // IMMEDIATE CLEANUP: Remove duplicates and disabled listeners first
            var currentListenerIds = _listeners.Select(l => $"{l.Config.League.Value}|{l.Config.SearchId.Value}").ToList();
            LogMessage($"🔍 CURRENT LISTENERS: {string.Join(", ", currentListenerIds)}");
            LogMessage($"🎯 TARGET CONFIGS: {string.Join(", ", activeConfigIds)}");
        }

        // CRITICAL: Check for disabled listeners efficiently (avoid redundant processing)
        if (!settingsChanged)
        {
            // Only do full disable check if settings haven't changed (to avoid redundancy)
            StopDisabledListeners();
        }
        else
        {
            // When settings changed, do immediate cleanup of duplicates and disabled listeners
            LogMessage($"🧹 IMMEDIATE CLEANUP: Processing settings change for {allConfigs.Count} target configs");
            StopDisabledListeners();
            
            // Clean up any existing duplicates immediately
            var duplicateGroups = _listeners.GroupBy(l => $"{l.Config.League.Value}|{l.Config.SearchId.Value}")
                .Where(g => g.Count() > 1).ToList();
            
            foreach (var group in duplicateGroups)
            {
                var duplicates = group.Skip(1).ToList();
                LogMessage($"🚨 SETTINGS CLEANUP: Removing {duplicates.Count} duplicates for {group.Key}");
                foreach (var duplicate in duplicates)
                {
                    duplicate.Stop();
                    _listeners.Remove(duplicate);
                }
            }
        }

        // CRITICAL: Stop listeners for disabled searches (includes disabled groups and searches)
        foreach (var listener in _listeners.ToList())
        {
            var listenerId = $"{listener.Config.League.Value}|{listener.Config.SearchId.Value}";
            
            // Check if the search itself is disabled OR its parent group is disabled
            bool searchStillActive = false;
            foreach (var group in Settings.Groups)
            {
                if (!group.Enable.Value) continue; // Group disabled - skip all its searches
                
                foreach (var search in group.Searches)
                {
                    if (search.Enable.Value && 
                        search.League.Value == listener.Config.League.Value && 
                        search.SearchId.Value == listener.Config.SearchId.Value)
                    {
                        searchStillActive = true;
                        break;
                    }
                }
                if (searchStillActive) break;
            }
            
            if (!searchStillActive || !activeConfigIds.Contains(listenerId))
            {
                listener.Stop();
                _listeners.Remove(listener);
                LogMessage($"🛑 DISABLED: Stopped listener for disabled search/group: {listener.Config.SearchId.Value}");
            }
            else if (!listener.IsRunning && !listener.IsConnecting)
            {
                // Multiple safety checks before attempting restart
                if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < RestartCooldownSeconds)
                {
                    LogMessage($"🛡️ RESTART BLOCKED: Search {listener.Config.SearchId.Value} in error cooldown ({RestartCooldownSeconds - (DateTime.Now - listener.LastErrorTime).TotalSeconds:F1}s remaining)");
                    continue;
                }

                if ((DateTime.Now - listener.LastConnectionAttempt).TotalSeconds < 60)
                {
                    LogMessage($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} connection throttled ({60 - (DateTime.Now - listener.LastConnectionAttempt).TotalSeconds:F1}s remaining)");
                    continue;
                }

                if (listener.ConnectionAttempts >= 2 && (DateTime.Now - listener.LastConnectionAttempt).TotalHours < 1)
                {
                    LogMessage($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} exceeded restart attempts (2 per HOUR)");
                    continue;
                }

                if (listener.ConnectionAttempts >= 5)
                {
                    LogMessage($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} exceeded TOTAL session restart limit (5 EVER)");
                    continue;
                }

                // Check rate limits before restarting WebSocket
                if (_rateLimiter != null)
                {
                    await _rateLimiter.CheckAndWaitIfNeeded("client");
                }
                
                LogMessage($"🔄 ATTEMPTING RESTART: Search {listener.Config.SearchId.Value}");
                listener.Start(LogMessage, LogError, Settings.SessionId.Value);
            }
        }

        // Start new listeners for enabled searches
        foreach (var config in allConfigs)
        {
            var configId = $"{config.League.Value}|{config.SearchId.Value}";
            
            // CRITICAL: Comprehensive duplicate detection
            var existingListeners = _listeners.Where(l => 
                l.Config.League.Value == config.League.Value && 
                l.Config.SearchId.Value == config.SearchId.Value).ToList();
            
            if (existingListeners.Count > 1)
            {
                // REMOVE EXTRA DUPLICATES
                LogMessage($"🚨 DUPLICATE CLEANUP: Found {existingListeners.Count} listeners for {config.SearchId.Value}, removing {existingListeners.Count - 1} extras");
                for (int i = 1; i < existingListeners.Count; i++)
                {
                    existingListeners[i].Stop();
                    _listeners.Remove(existingListeners[i]);
                }
                LogMessage($"✅ CLEANED UP: Removed {existingListeners.Count - 1} duplicate listeners for {config.SearchId.Value}");
            }
            
            var existingListener = existingListeners.FirstOrDefault();
            
            if (existingListener == null)
            {
                // FINAL CHECK: Ensure absolutely no duplicates
                var finalDuplicateCheck = _listeners.Any(l => 
                    l.Config.League.Value == config.League.Value && 
                    l.Config.SearchId.Value == config.SearchId.Value);
                
                if (finalDuplicateCheck)
                {
                    LogMessage($"🛡️ FINAL DUPLICATE PREVENTION: Listener already exists for {config.SearchId.Value}");
                    continue;
                }
                
                // EMERGENCY: Prevent rapid creation (but allow user-initiated restarts)
                if (JewYourItem._globalConnectionAttempts >= 5 && !recentSettingsChange)
                {
                    LogMessage($"🚨 EMERGENCY: Preventing new listener creation - global limit reached ({JewYourItem._globalConnectionAttempts} attempts)");
                    continue;
                }
                else if (recentSettingsChange && JewYourItem._globalConnectionAttempts >= 2)
                {
                    // Reset global attempts for user-initiated changes
                    LogMessage($"🔄 USER ACTION: Resetting global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) for settings change");
                    JewYourItem._globalConnectionAttempts = 0;
                }
                
                // Check rate limits before creating new WebSocket (but allow immediate user actions)
                if (_rateLimiter != null && !recentSettingsChange)
                {
                    await _rateLimiter.CheckAndWaitIfNeeded("client");
                }
                else if (recentSettingsChange)
                {
                    LogMessage($"⚡ INSTANT START: Bypassing rate limit for user-initiated settings change");
                }
                
                var newListener = new SearchListener(this, config, LogMessage, LogError);
                _listeners.Add(newListener);
                LogMessage($"🆕 CREATING NEW LISTENER: Search {config.SearchId.Value} (Total listeners: {_listeners.Count})");
                newListener.Start(LogMessage, LogError, Settings.SessionId.Value);
            }
            else
            {
                LogMessage($"📍 EXISTING LISTENER: {config.SearchId.Value} (Running: {existingListener.IsRunning}, Connecting: {existingListener.IsConnecting})");
            }
        }
    }

    public override void Render()
    {
        // CRITICAL: Respect plugin enable state for ALL rendering
        if (!Settings.Enable.Value || !Settings.ShowGui.Value) return;

        // Set window position but allow auto-resizing
        ImGui.SetNextWindowPos(Settings.WindowPosition, ImGuiCond.FirstUseEver);
        
        // Set minimum window size for better appearance
        ImGui.SetNextWindowSizeConstraints(new Vector2(200, 100), new Vector2(float.MaxValue, float.MaxValue));
        
        // Enable auto-resize: Remove NoResize flag and add AlwaysAutoResize
        ImGui.Begin("JewYourItem Results", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize);
        
        // Add padding for better text spacing
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4));
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.9f, 0.9f, 1.0f));

        if (Settings.AutoTp.Value && (DateTime.Now - _lastTpTime).TotalSeconds < Settings.TpCooldown.Value)
        {
            float remainingCooldown = Settings.TpCooldown.Value - (float)(DateTime.Now - _lastTpTime).TotalSeconds;
            ImGui.Text($"TP Cooldown: {remainingCooldown:F1}s");
        }

        // Use child windows and proper spacing for better auto-sizing
        if (_listeners.Count > 0)
        {
            ImGui.Text("🔍 Search Listeners:");
            ImGui.Spacing();
            
            foreach (var listener in _listeners)
            {
                string status = "Unknown";
                if (listener.IsConnecting) status = "🔄 Connecting";
                else if (listener.IsRunning) status = "✅ Connected";
                else status = "❌ Disconnected";
                
                ImGui.BulletText($"{listener.Config.SearchId.Value}: {status}");
                
                // Indent additional info
                if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < RestartCooldownSeconds ||
                    (DateTime.Now - listener.LastConnectionAttempt).TotalSeconds < 10 ||
                    listener.ConnectionAttempts > 0)
                {
                    ImGui.Indent();
                    
                    if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < RestartCooldownSeconds)
                    {
                        float remainingCooldown = RestartCooldownSeconds - (float)(DateTime.Now - listener.LastErrorTime).TotalSeconds;
                        ImGui.Text($"⏱️ Error Cooldown: {remainingCooldown:F1}s");
                    }
                    
                    if ((DateTime.Now - listener.LastConnectionAttempt).TotalSeconds < 10)
                    {
                        float remainingThrottle = 10 - (float)(DateTime.Now - listener.LastConnectionAttempt).TotalSeconds;
                        ImGui.Text($"⏸️ Throttle: {remainingThrottle:F1}s");
                    }
                    
                    if (listener.ConnectionAttempts > 0)
                    {
                        ImGui.Text($"🔄 Attempts: {listener.ConnectionAttempts}");
                    }
                    
                    ImGui.Unindent();
                }
            }
            
            ImGui.Spacing();
            ImGui.Text($"📊 Status: {_listeners.Count(l => l.IsRunning)}/{_listeners.Count} active");
        }
        else
        {
            ImGui.Text("🔍 No active searches");
        }
        
        if (_recentItems.Count > 0)
        {
            ImGui.Separator();
            ImGui.Text("📦 Recent Items:");
            ImGui.Spacing();
            
            foreach (var item in _recentItems)
            {
                ImGui.BulletText($"{item.name} - {item.price}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({item.x}, {item.y})");
            }
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
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

    private void ForceStopAll()
    {
        LogMessage("🚨 FORCE STOPPING: All listeners due to plugin disable...");
        foreach (var listener in _listeners.ToList())
        {
            listener.Stop();
            _listeners.Remove(listener);
        }
        _recentItems.Clear();
        LogMessage("✅ All listeners force stopped - plugin fully disabled.");
    }

    private void StopDisabledListeners()
    {
        var listenersToRemove = new List<SearchListener>();
        
        foreach (var listener in _listeners.ToList())
        {
            // Check if the search itself is disabled OR its parent group is disabled
            bool searchStillActive = false;
            bool foundMatchingConfig = false;
            string disableReason = "";
            
            foreach (var group in Settings.Groups)
            {
                foreach (var search in group.Searches)
                {
                    if (search.League.Value == listener.Config.League.Value && 
                        search.SearchId.Value == listener.Config.SearchId.Value)
                    {
                        foundMatchingConfig = true;
                        
                        // CRITICAL: Group disable overrides everything
                        if (!group.Enable.Value)
                        {
                            disableReason = $"group '{group.Name.Value}' disabled";
                            searchStillActive = false;
                        }
                        else if (!search.Enable.Value)
                        {
                            disableReason = "search disabled";
                            searchStillActive = false;
                        }
                        else
                        {
                            searchStillActive = true;
                        }
                        break;
                    }
                }
                if (foundMatchingConfig) break;
            }
            
            // If we didn't find the config at all, or it's disabled, stop the listener
            if (!foundMatchingConfig || !searchStillActive)
            {
                if (!foundMatchingConfig)
                {
                    disableReason = "config not found";
                }
                LogMessage($"🛑 DISABLING: {listener.Config.SearchId.Value} - {disableReason}");
                listener.Stop();
                listenersToRemove.Add(listener);
            }
        }
        
        // Remove stopped listeners
        if (listenersToRemove.Count > 0)
        {
            LogMessage($"🛑 STOPPING: {listenersToRemove.Count} disabled listeners");
            foreach (var listener in listenersToRemove)
            {
                _listeners.Remove(listener);
            }
            LogMessage($"✅ STOPPED: {listenersToRemove.Count} listeners removed, {_listeners.Count} remaining");
        }
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
            Settings.TpCooldown.Value = Math.Clamp(tpCooldown, 1, 30);
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

        var autoBuy = Settings.AutoBuy.Value;
        ImGui.Checkbox("Auto Buy", ref autoBuy);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.AutoBuy.Value = autoBuy;
            _settingsUpdated = true;
            LogMessage($"Auto Buy setting changed to: {autoBuy}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Automatically perform Ctrl+Left Click after moving mouse to item location");
        }

        ImGui.Separator();
        
        // EMERGENCY CONTROLS
        if (JewYourItem._emergencyShutdown)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
            ImGui.Text("🚨🚨🚨 EMERGENCY SHUTDOWN ACTIVE 🚨🚨🚨");
            if (ImGui.Button("RESET EMERGENCY SHUTDOWN"))
            {
                JewYourItem._emergencyShutdown = false;
                JewYourItem._globalConnectionAttempts = 0;
                LogMessage("Emergency shutdown reset by user");
            }
            ImGui.PopStyleColor(1);
            ImGui.Separator();
        }
        
        if (ImGui.Button("Show Rate Limit Status"))
        {
            if (_rateLimiter != null)
            {
                _rateLimiter.LogCurrentState();
            }
            else
            {
                LogMessage("Rate limiter not initialized yet.");
            }
            LogMessage($"Global connection attempts: {JewYourItem._globalConnectionAttempts}");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Display current rate limit status in the console");
        }

        ImGui.Separator();
        if (ImGui.Button("Test Move Mouse to Recent Item"))
        {
            if (_recentItems.Count > 0)
            {
                var (name, price, hideoutToken, x, y) = _recentItems.Peek();
                LogMessage($"Testing move mouse to recent item: {name} at ({x}, {y})");
                MoveMouseToItemLocation(x, y);
            }
            else
            {
                LogMessage("No recent items available for mouse movement test");
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Test moving mouse to the most recent item (requires purchase window to be open). Will also test Auto Buy if enabled.");
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