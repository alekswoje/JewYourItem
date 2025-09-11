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
using JewYourItem.Models;
using JewYourItem.Utility;

namespace JewYourItem;

public partial class JewYourItem : BaseSettingsPlugin<JewYourItemSettings>
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

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    // Mouse event constants
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // System metrics constants
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

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
    private Queue<RecentItem> _recentItems = new Queue<RecentItem>();
    private bool _playSound = true;
    private bool _lastHotkeyState = false;
    private DateTime _lastTpTime = DateTime.MinValue;
    private bool _settingsUpdated = false;
    private SearchListener _activeListener;
    private Dictionary<JewYourItemInstanceSettings, DateTime> _lastRestartTimes = new Dictionary<JewYourItemInstanceSettings, DateTime>();
    private const int RestartCooldownSeconds = 80; // Increased to 5 minutes for safety
    private bool _lastEnableState = true;
    private string _lastActiveConfigsHash = "";
    private ConservativeRateLimiter _rateLimiter;
    private bool _lastPurchaseWindowVisible = false;
    private static int _globalConnectionAttempts = 0;
    private static DateTime _pluginStartTime = DateTime.Now;
    private static DateTime _lastGlobalReset = DateTime.Now;
    private static bool _emergencyShutdown = false;
    private static readonly Random _random = new Random();
    private DateTime _lastTickProcessTime = DateTime.MinValue;
    private DateTime _lastAreaChangeTime = DateTime.MinValue;
    private DateTime _lastSettingsChangeTime = DateTime.MinValue;
    private bool _areaChangeCooldownLogged = false;
    private (int x, int y)? _teleportedItemLocation = null;
    private bool _isManualTeleport = false;
    private RecentItem _currentTeleportingItem = null;
    private RecentItem _teleportedItemInfo = null; // Store item info for teleported items
    private readonly object _recentItemsLock = new object();

    // Dynamic TP cooldown state
    private bool _tpLocked = false;
    private DateTime _tpLockedTime = DateTime.MinValue;

    // Purchase window state tracking to prevent accidental purchases
    private bool _allowMouseMovement = true;
    private bool _windowWasClosedSinceLastMovement = true;

    // Connection queue system
    private readonly Queue<JewYourItemInstanceSettings> _connectionQueue = new Queue<JewYourItemInstanceSettings>();
    private DateTime _lastConnectionTime = DateTime.MinValue;
    private const int ConnectionDelayMs = 1500; // 1.5 seconds between connections

    public override bool Initialise()
    {
        _rateLimiter = new ConservativeRateLimiter(LogMessage, LogError);
        
        // Use secure session ID storage with fallback to regular session ID
        _sessionIdBuffer = Settings.SecureSessionId ?? "";
        
        // Fallback to regular session ID if secure storage is empty
        if (string.IsNullOrEmpty(_sessionIdBuffer))
        {
            _sessionIdBuffer = Settings.SessionId.Value ?? "";
            
            if (!string.IsNullOrEmpty(_sessionIdBuffer))
            {
                LogMessage("⚠️ Using regular session ID (secure storage empty) - consider updating to secure storage");
                // Automatically migrate to secure storage
                Settings.SecureSessionId = _sessionIdBuffer;
                LogMessage("✅ Session ID migrated to secure storage");
            }
        }
        
        if (string.IsNullOrEmpty(_sessionIdBuffer))
        {
            LogMessage("❌ ERROR: Session ID is empty. Please set it in the settings.");
            LogMessage("💡 TIP: Go to plugin settings and enter your POESESSID from pathofexile.com cookies");
            LogMessage("📋 HOW TO GET POESESSID:");
            LogMessage("   1. Go to pathofexile.com and log in");
            LogMessage("   2. Press F12 to open Developer Tools");
            LogMessage("   3. Go to Application/Storage → Cookies → pathofexile.com");
            LogMessage("   4. Copy the POESESSID value (32 characters)");
            LogMessage("   5. Paste it in the plugin settings");
        }
        else
        {
            LogMessage("✅ Session ID loaded successfully");
            
            // Validate session ID format (should be 32 characters, alphanumeric)
            if (_sessionIdBuffer.Length != 32)
            {
                LogMessage($"⚠️ WARNING: Session ID length is {_sessionIdBuffer.Length}, expected 32 characters");
            }
            
            if (!_sessionIdBuffer.All(c => char.IsLetterOrDigit(c)))
            {
                LogMessage("⚠️ WARNING: Session ID contains non-alphanumeric characters");
            }
            
            // Test session ID validity with a simple API call
            _ = TestSessionIdValidity(_sessionIdBuffer);
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
        LogMessage("TP Cooldown: Using dynamic locking (locked until window loads or 10s timeout)");
        LogMessage($"Move Mouse to Item enabled: {Settings.MoveMouseToItem.Value}");

        return true;
    }

    // Helper methods moved to JewYourItem.Teleport.cs

    // RemoveSpecificItem method moved to JewYourItem.Teleport.cs

    public override void AreaChange(AreaInstance area)
    {
        LogMessage($"🌍 AREA CHANGE: {area?.Area?.Name ?? "Unknown"} - NOT stopping listeners (live searches continue)");
        _lastAreaChangeTime = DateTime.Now;

        // DON'T stop all listeners - let them continue running
        // Live searches should persist across zone changes
        // Only clear recent items as they're location-specific
        _recentItems.Clear();
        // DON'T clear _teleportedItemLocation - we need it to persist until purchase window opens
        // Also clear teleported item info since it might be stale after area change
        _teleportedItemInfo = null;
        LogMessage("📦 Cleared recent items and teleported item info due to area change (preserved teleported location for mouse movement)");
    }

    // LearnPurchaseWindowCoordinates method
    private void LearnPurchaseWindowCoordinates(int x, int y)
    {
        try
        {
            LogMessage($"🎓 LEARNING COORDINATES: Storing purchase window coordinates ({x}, {y})");
            Settings.PurchaseWindowX.Value = x;
            Settings.PurchaseWindowY.Value = y;
            Settings.HasLearnedPurchaseWindow.Value = true;
            LogMessage($"✅ COORDINATES LEARNED: Purchase window coordinates saved for future instant mouse movement");
        }
        catch (Exception ex)
        {
            LogError($"❌ LEARNING FAILED: Could not save purchase window coordinates: {ex.Message}");
        }
    }

    // MoveMouseToLearnedPosition method moved to JewYourItem.Teleport.cs

    // MoveMouseToCalculatedPosition method moved to JewYourItem.Teleport.cs

    // PlaySoundWithNAudio method moved to JewYourItem.Teleport.cs

    // Helper method for partial classes to access Input
    private bool IsKeyPressed(Keys key)
    {
        return Input.GetKeyState(key);
    }

    // Logging helper methods
    private void LogDebug(string message)
    {
        if (Settings.DebugMode.Value)
        {
            LogMessage($"[DEBUG] {message}");
        }
    }

    private void LogInfo(string message)
    {
        LogMessage($"[INFO] {message}");
    }

    private void LogWarning(string message)
    {
        LogMessage($"[WARNING] {message}");
    }

    // Test session ID validity
    private async Task TestSessionIdValidity(string sessionId)
    {
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.pathofexile.com/api/profile"))
            {
                request.Headers.Add("Cookie", $"POESESSID={sessionId}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                
                using (var response = await _httpClient.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        LogMessage("✅ Session ID validated successfully");
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        LogMessage("❌ Session ID invalid - please get a fresh POESESSID from pathofexile.com cookies");
                        LogMessage("📋 HOW TO GET FRESH POESESSID:");
                        LogMessage("   1. Go to pathofexile.com and log in");
                        LogMessage("   2. Press F12 to open Developer Tools");
                        LogMessage("   3. Go to Application/Storage → Cookies → pathofexile.com");
                        LogMessage("   4. Copy the POESESSID value (32 characters)");
                        LogMessage("   5. Update it in the plugin settings");
                    }
                    else
                    {
                        LogMessage($"⚠️ Session ID test failed with status {response.StatusCode}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"⚠️ Session ID validation failed: {ex.Message}");
        }
    }

    // Process connection queue method
    private void ProcessConnectionQueue()
    {
        if (_connectionQueue.Count == 0) return;

        // Check if enough time has passed since last connection
        if ((DateTime.Now - _lastConnectionTime).TotalMilliseconds < ConnectionDelayMs)
            return;

        var config = _connectionQueue.Dequeue();
        _lastConnectionTime = DateTime.Now;

        // Final duplicate check
        if (_listeners.Any(l => l.Config.League.Value == config.League.Value &&
                               l.Config.SearchId.Value == config.SearchId.Value))
        {
            LogMessage($"🛡️ QUEUE SKIP: Listener already exists for {config.SearchId.Value}");
            return;
        }

        var newListener = new SearchListener(this, config, LogMessage, LogError);
        _listeners.Add(newListener);
        LogMessage($"🆕 STARTING FROM QUEUE: Search {config.SearchId.Value}");
        newListener.Start(LogMessage, LogError, Settings.SecureSessionId);
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

        // EMERGENCY SHUTDOWN CHECK - use throttling instead of shutdown
        if (JewYourItem._emergencyShutdown)
        {
            // Instead of disabling plugin, just log and wait for cooldown
            LogError("⚠️ CONNECTION THROTTLING: Global connection limit reached, waiting for cooldown...");

            // Reset emergency shutdown after time
            if ((DateTime.Now - _pluginStartTime).TotalMinutes >= 5)
            {
                LogMessage("🔄 Resetting emergency shutdown state after 5 minutes");
                JewYourItem._emergencyShutdown = false;
                JewYourItem._globalConnectionAttempts = 0;
            }
            return;
        }

        // CRITICAL: Check TP lock timeout to prevent infinite locks
        if (_tpLocked && (DateTime.Now - _tpLockedTime).TotalSeconds >= 10)
        {
            LogMessage("🔓 TP UNLOCKED: 10-second timeout reached in Tick(), unlocking TP");
            _tpLocked = false;
            _tpLockedTime = DateTime.MinValue;
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
                    TravelToHideout(isManual: true);
                }
                _lastHotkeyState = hotkeyState;

                // Check if purchase window just became visible and move mouse to teleported item
                bool purchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;

                // Track window close events (throttled)
                if (!purchaseWindowVisible && _lastPurchaseWindowVisible)
                {
                    LogMessage("🚪 PURCHASE WINDOW CLOSED (Throttled): Mouse movement will be allowed on next window open");
                    _windowWasClosedSinceLastMovement = true;
                    _allowMouseMovement = true;
                }

                if (purchaseWindowVisible && !_lastPurchaseWindowVisible)
                {
                    LogMessage($"🖱️ PURCHASE WINDOW OPENED (Throttled): MoveMouseToItem = {Settings.MoveMouseToItem.Value}, TeleportedLocation = {(_teleportedItemLocation.HasValue ? $"({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})" : "null")}, AllowMovement = {_allowMouseMovement}");

                    // Unlock TP when purchase window opens
                    if (_tpLocked)
                    {
                        LogMessage("🔓 TP UNLOCKED (Throttled): Purchase window opened successfully");
                        _tpLocked = false;
                        _tpLockedTime = DateTime.MinValue;
                    }

                    // Learn purchase window coordinates for future instant mouse movement
                    if (!Settings.HasLearnedPurchaseWindow.Value && _teleportedItemLocation.HasValue)
                    {
                        LogMessage($"🎓 LEARNING TRIGGER (Throttled): Purchase window opened, learning coordinates from teleported location ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                        LearnPurchaseWindowCoordinates(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
                    }
                }
                if (purchaseWindowVisible && !_lastPurchaseWindowVisible && Settings.MoveMouseToItem.Value)
                {
                    if (_allowMouseMovement && _windowWasClosedSinceLastMovement)
                    {
                        if (_teleportedItemLocation.HasValue)
                        {
                            LogMessage($"🖱️ SAFE MOUSE MOVE (Throttled): Window was closed, moving to teleported item at ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                            MoveMouseToItemLocation(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
                            _teleportedItemLocation = null; // Clear after use
                            _allowMouseMovement = false; // Block further movement until window closes
                            _windowWasClosedSinceLastMovement = false;
                        }
                        else if (_recentItems.Count > 0)
                        {
                            LogMessage("🖱️ SAFE FALLBACK MOVE (Throttled): Window was closed, using most recent item");
                            var item = _recentItems.Peek();
                            MoveMouseToItemLocation(item.X, item.Y);
                            _allowMouseMovement = false; // Block further movement until window closes
                            _windowWasClosedSinceLastMovement = false;
                        }
                    }
                    else
                    {
                        LogMessage("🚫 MOUSE MOVE BLOCKED (Throttled): Purchase window opened but previous window was not closed (preventing accidental purchases)");
                    }
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
                    TravelToHideout(isManual: true);
                }
                _lastHotkeyState = areaChangeHotkeyState;

                bool areaChangePurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;

                // Track window close events (area change)
                if (!areaChangePurchaseWindowVisible && _lastPurchaseWindowVisible)
                {
                    LogMessage("🚪 PURCHASE WINDOW CLOSED (Area): Mouse movement will be allowed on next window open");
                    _windowWasClosedSinceLastMovement = true;
                    _allowMouseMovement = true;
                }

                if (areaChangePurchaseWindowVisible && !_lastPurchaseWindowVisible)
                {
                    LogMessage($"🖱️ PURCHASE WINDOW OPENED (Area): MoveMouseToItem = {Settings.MoveMouseToItem.Value}, TeleportedLocation = {(_teleportedItemLocation.HasValue ? $"({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})" : "null")}, AllowMovement = {_allowMouseMovement}");

                    // Unlock TP when purchase window opens
                    if (_tpLocked)
                    {
                        LogMessage("🔓 TP UNLOCKED (Area): Purchase window opened successfully");
                        _tpLocked = false;
                        _tpLockedTime = DateTime.MinValue;
                    }

                    // Learn purchase window coordinates for future instant mouse movement
                    if (!Settings.HasLearnedPurchaseWindow.Value && _teleportedItemLocation.HasValue)
                    {
                        LogMessage($"🎓 LEARNING TRIGGER (Area): Purchase window opened, learning coordinates from teleported location ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                        LearnPurchaseWindowCoordinates(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
                    }
                }
                if (areaChangePurchaseWindowVisible && !_lastPurchaseWindowVisible && Settings.MoveMouseToItem.Value)
                {
                    if (_allowMouseMovement && _windowWasClosedSinceLastMovement)
                    {
                        if (_teleportedItemLocation.HasValue)
                        {
                            LogMessage($"🖱️ SAFE MOUSE MOVE (Area): Window was closed, moving to teleported item at ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                            MoveMouseToItemLocation(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
                            _teleportedItemLocation = null; // Clear after use
                            _allowMouseMovement = false; // Block further movement until window closes
                            _windowWasClosedSinceLastMovement = false;
                        }
                        else if (_recentItems.Count > 0)
                        {
                            LogMessage("🖱️ SAFE FALLBACK MOVE (Area): Window was closed, using most recent item");
                            var item = _recentItems.Peek();
                            MoveMouseToItemLocation(item.X, item.Y);
                            _allowMouseMovement = false; // Block further movement until window closes
                            _windowWasClosedSinceLastMovement = false;
                        }
                    }
                    else
                    {
                        LogMessage("🚫 MOUSE MOVE BLOCKED (Area): Purchase window opened but previous window was not closed (preventing accidental purchases)");
                    }
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

        // Process connection queue first
        ProcessConnectionQueue();

        // Periodic reset of global connection attempts (every 2 minutes)
        if ((DateTime.Now - _lastGlobalReset).TotalMinutes >= 2)
        {
            if (JewYourItem._globalConnectionAttempts > 0)
            {
                LogMessage($"🔄 PERIODIC RESET: Clearing global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) after 2 minutes");
                JewYourItem._globalConnectionAttempts = 0;
                _lastGlobalReset = DateTime.Now;

                // Unblock all blocked listeners
                foreach (var listener in _listeners.Where(l => !l.IsRunning && !l.IsConnecting))
                {
                    LogMessage($"🔄 UNBLOCKING: Search {listener.Config.SearchId.Value} after global reset");
                }
            }
        }

        if (recentSettingsChange)
        {
        }
        else
        {
        }

        bool currentHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
        if (currentHotkeyState && !_lastHotkeyState)
        {
            LogMessage($"🎮 MANUAL HOTKEY PRESSED: {Settings.TravelHotkey.Value} - initiating manual teleport");
            TravelToHideout(isManual: true);
        }
        _lastHotkeyState = currentHotkeyState;

        // Check if purchase window just became visible and learn coordinates + move mouse to teleported item
        bool currentPurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;

        // Track window close events to allow mouse movement on next open
        if (!currentPurchaseWindowVisible && _lastPurchaseWindowVisible)
        {
            LogMessage("🚪 PURCHASE WINDOW CLOSED: Mouse movement will be allowed on next window open");
            _windowWasClosedSinceLastMovement = true;
            _allowMouseMovement = true;
        }

        if (currentPurchaseWindowVisible && !_lastPurchaseWindowVisible)
        {
            LogMessage($"🖱️ PURCHASE WINDOW OPENED: MoveMouseToItem = {Settings.MoveMouseToItem.Value}, TeleportedLocation = {(_teleportedItemLocation.HasValue ? $"({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})" : "null")}, AllowMovement = {_allowMouseMovement}");

            // Unlock TP when purchase window opens
            if (_tpLocked)
            {
                LogMessage("🔓 TP UNLOCKED: Purchase window opened successfully");
                _tpLocked = false;
                _tpLockedTime = DateTime.MinValue;
            }

            // Learn purchase window coordinates for future instant mouse movement
            if (!Settings.HasLearnedPurchaseWindow.Value && _teleportedItemLocation.HasValue)
            {
                LogMessage($"🎓 LEARNING TRIGGER: Purchase window opened, learning coordinates from teleported location ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                LearnPurchaseWindowCoordinates(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
            }
            else if (Settings.HasLearnedPurchaseWindow.Value)
            {
                LogMessage($"🎓 ALREADY LEARNED: Using stored coordinates ({Settings.PurchaseWindowX.Value}, {Settings.PurchaseWindowY.Value})");
            }
            else if (!_teleportedItemLocation.HasValue)
            {
                LogMessage("🎓 LEARNING SKIPPED: No teleported item location available for learning");
            }
        }
        if (currentPurchaseWindowVisible && !_lastPurchaseWindowVisible)
        {
            LogMessage($"🪟 PURCHASE WINDOW EVENT: MoveMouseToItem={Settings.MoveMouseToItem.Value}, AllowMovement={_allowMouseMovement}, WindowWasClosed={_windowWasClosedSinceLastMovement}, HasTeleportedItem={_teleportedItemLocation.HasValue}, RecentItemsCount={_recentItems.Count}, AutoBuy={Settings.AutoBuy.Value}");

            if (!Settings.MoveMouseToItem.Value)
            {
                LogMessage("🚫 MOUSE MOVE DISABLED: MoveMouseToItem setting is disabled");
                return;
            }

            if (!_allowMouseMovement)
            {
                LogMessage("🚫 MOUSE MOVE BLOCKED: _allowMouseMovement is false (previous movement not completed)");
                return;
            }

            if (!_windowWasClosedSinceLastMovement)
            {
                LogMessage("🚫 MOUSE MOVE BLOCKED: Window was not closed since last movement (preventing accidental purchases)");
                return;
            }

            if (_teleportedItemLocation.HasValue)
            {
                LogMessage($"🖱️ SAFE MOUSE MOVE (Main): Window was closed, moving to teleported item at ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
                MoveMouseToItemLocation(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
                _teleportedItemLocation = null; // Clear after use
                _allowMouseMovement = false; // Block further movement until window closes
                _windowWasClosedSinceLastMovement = false;
            }
            else if (_recentItems.Count > 0)
            {
                LogMessage("🖱️ SAFE FALLBACK MOVE (Main): Window was closed, using most recent item");
                var item = _recentItems.Peek();
                MoveMouseToItemLocation(item.X, item.Y);
                _allowMouseMovement = false; // Block further movement until window closes
                _windowWasClosedSinceLastMovement = false;
            }
            else
            {
                LogMessage("🚫 NO ITEMS TO PROCESS: No teleported item or recent items available");
            }
        }
        _lastPurchaseWindowVisible = currentPurchaseWindowVisible;

        var allConfigs = Settings.Groups
            .Where(g => g.Enable.Value)
            .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
            .ToList();

        if (allConfigs.Count > 30)
        {
            LogError("Exceeded max 30 searches; disabling extras.");
            allConfigs = allConfigs.Take(30).ToList();
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

            // Reset global attempts for user actions to allow fresh start
            if (JewYourItem._globalConnectionAttempts > 0)
            {
                LogMessage($"🔄 SETTINGS RESET: Clearing global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) for fresh start");
                JewYourItem._globalConnectionAttempts = 0;
            }

            // Reset emergency shutdown on settings change (user is actively configuring)
            if (JewYourItem._emergencyShutdown)
            {
                LogMessage($"🔄 EMERGENCY RESET: Settings changed - clearing emergency shutdown state");
                JewYourItem._emergencyShutdown = false;
            }

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
            bool foundMatchingConfig = false;
            string disableReason = "";

            foreach (var group in Settings.Groups)
            {
                if (!group.Enable.Value) continue; // Group disabled - skip all its searches

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
                _listeners.Remove(listener);
            }
            else if (!listener.IsRunning && !listener.IsConnecting)
            {
                // Check global throttling first
                if (JewYourItem._globalConnectionAttempts >= 3)
                {
                    int globalWait = 5000 * (JewYourItem._globalConnectionAttempts - 2);
                    LogMessage($"🌐 GLOBAL THROTTLE: Skipping restart due to global attempts ({JewYourItem._globalConnectionAttempts}), waiting {globalWait}ms");
                    continue;
                }

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

                // Use delay for retries instead of immediate restart
                if (listener.ConnectionAttempts > 0)
                {
                    int delayMs = 1000 * (int)Math.Pow(2, listener.ConnectionAttempts - 1);
                    delayMs = Math.Min(delayMs, 30000); // Max 30 seconds

                    if ((DateTime.Now - listener.LastConnectionAttempt).TotalMilliseconds < delayMs)
                    {
                        LogMessage($"⏳ DELAYED RESTART: Search {listener.Config.SearchId.Value} waiting {delayMs - (DateTime.Now - listener.LastConnectionAttempt).TotalMilliseconds:F0}ms");
                        continue;
                    }
                }

                LogMessage($"🔄 ATTEMPTING RESTART: Search {listener.Config.SearchId.Value}");
                listener.Start(LogMessage, LogError, Settings.SecureSessionId);
            }
        }

        // Start new listeners for enabled searches - add to queue instead of immediate start
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
                else if (recentSettingsChange && JewYourItem._globalConnectionAttempts >= 1)
                {
                    // Reset global attempts for user-initiated changes (more forgiving)
                    LogMessage($"🔄 USER ACTION: Resetting global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) for settings change");
                    JewYourItem._globalConnectionAttempts = 0;
                }

                // Add to connection queue instead of immediate start
                if (!_connectionQueue.Any(q => q.League.Value == config.League.Value &&
                                              q.SearchId.Value == config.SearchId.Value))
                {
                    _connectionQueue.Enqueue(config);
                    LogMessage($"📋 QUEUED: Search {config.SearchId.Value} added to connection queue (Position: {_connectionQueue.Count})");
                }
            }
            else
            {
                // 🔹 LOG ONLY IF STATE CHANGED
                if (existingListener.IsRunning != existingListener.LastIsRunning ||
                    existingListener.IsConnecting != existingListener.LastIsConnecting)
                {
                    LogMessage($"📍 EXISTING LISTENER: {config.SearchId.Value} (Running: {existingListener.IsRunning}, Connecting: {existingListener.IsConnecting})");

                    // Update last known state
                    existingListener.LastIsRunning = existingListener.IsRunning;
                    existingListener.LastIsConnecting = existingListener.IsConnecting;
                }
            }
        }
    }
    // Render method moved to JewYourItem.Gui.cs

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
        _teleportedItemInfo = null;
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

    public void OnDisable()
    {
        LogMessage("🛑 PLUGIN DISABLED: Stopping all listeners immediately");
        ForceStopAll();
    }

    public void OnPluginDestroy()
    {
        StopAll();
    }

    // DrawSettings method moved to JewYourItem.Gui.cs

    /// <summary>
    /// Gets the plugin's temp directory path dynamically
    /// </summary>
    /// <returns>The full path to the plugin's temp directory</returns>
    private string GetPluginTempDirectory()
    {
        try
        {
            // Get the plugin's assembly location
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            // Get the directory containing the plugin DLL
            string pluginDir = Path.GetDirectoryName(assemblyPath);

            // Navigate up to find the Plugins directory
            DirectoryInfo pluginsDir = Directory.GetParent(pluginDir)?.Parent;

            if (pluginsDir != null && pluginsDir.Name == "Plugins")
            {
                // Get the plugin name from the current directory name
                string pluginName = Path.GetFileName(pluginDir);

                // Construct the temp path: Plugins/Temp/[PluginName]
                string tempDir = Path.Combine(pluginsDir.FullName, "Temp", pluginName);

                // Ensure the temp directory exists
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                return tempDir;
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to get plugin temp directory: {ex.Message}");
        }

        // Fallback to current directory if we can't determine the proper path
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Updates an existing log entry to mark auto-buy as attempted
    /// </summary>
    /// <param name="itemName">Name of the item to update</param>
    /// <param name="searchId">Search ID to identify the entry</param>
    public void UpdateAutoBuyAttempt(string itemName, string searchId)
    {
        LogMessage($"🚀 UPDATE AUTO-BUY ATTEMPT CALLED: Item='{itemName}', SearchId='{searchId}'");

        try
        {
            if (!Settings.LogSearchResults.Value)
            {
                LogMessage("🚫 LOGGING DISABLED: Search results logging is disabled in settings");
                return;
            }

            // Get the plugin's temp directory path dynamically
            string pluginTempDir = GetPluginTempDirectory();
            string logFileName = "search_results.csv";

            // Ensure CSV extension
            if (!logFileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                logFileName = Path.ChangeExtension(logFileName, ".csv");
            }

            string logFilePath = Path.Combine(pluginTempDir, logFileName);
            LogMessage($"📂 PLUGIN TEMP DIR: {pluginTempDir}");
            LogMessage($"📄 FULL LOG FILE PATH: {logFilePath}");

            if (!File.Exists(logFilePath))
            {
                LogMessage($"❌ CSV FILE DOES NOT EXIST: {logFilePath}");
                return;
            }

            LogMessage($"✅ CSV FILE EXISTS: {logFilePath}");

            // Read all lines
            string[] lines = File.ReadAllLines(logFilePath);
            LogMessage($"📄 READ {lines.Length} LINES FROM CSV FILE");

            if (lines.Length <= 1) // No data rows
            {
                LogMessage("⚠️ CSV FILE HAS NO DATA ROWS (only header)");
                return;
            }

            // Update the matching entry
            bool foundMatch = false;
            LogMessage($"🔍 SEARCHING FOR LOG ENTRY: Item='{itemName}', SearchId='{searchId}'");
            LogMessage($"📊 CSV FILE HAS {lines.Length} TOTAL LINES");

            for (int i = 1; i < lines.Length; i++) // Skip header
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue; // Skip empty lines

                string[] fields = ParseCsvLine(lines[i]);
                string csvItemName = fields.Length > 1 ? fields[1] : "";
                string csvSearchId = fields.Length > 4 ? fields[4] : "";
                string csvStatus = fields.Length > 6 ? fields[6] : "";

                LogMessage($"📋 LINE {i}: Raw='{lines[i]}'");
                LogMessage($"📋 LINE {i}: Parsed - Item='{csvItemName}', SearchId='{csvSearchId}', Status='{csvStatus}'");

                if (fields.Length >= 7)
                {
                    // Clean the strings for comparison (remove quotes and trim)
                    string cleanCsvItemName = csvItemName.Replace("\"", "").Trim();
                    string cleanCsvSearchId = csvSearchId.Replace("\"", "").Trim();
                    string cleanItemName = itemName.Replace("\"", "").Trim();
                    string cleanSearchId = searchId.Replace("\"", "").Trim();

                    LogMessage($"🧹 CLEANED: CSV Item='{cleanCsvItemName}', Search Item='{cleanItemName}'");
                    LogMessage($"🧹 CLEANED: CSV SearchId='{cleanCsvSearchId}', Search SearchId='{cleanSearchId}'");

                    // Try exact match first
                    if (cleanCsvItemName == cleanItemName && cleanCsvSearchId == cleanSearchId)
                    {
                        LogMessage($"✅ FOUND EXACT MATCH on line {i}: '{cleanCsvItemName}' with '{cleanCsvSearchId}'");
                        fields[6] = "AUTO-BUY ATTEMPTED"; // Update Purchase Status column
                        lines[i] = string.Join(",", fields.Select(f => EscapeCsvField(f)));
                        foundMatch = true;
                        LogMessage($"✏️ UPDATED LINE {i} TO: {lines[i]}");
                        break;
                    }
                    // Try partial match if exact fails (case-insensitive, substring matching)
                    else if (cleanCsvItemName.Contains(cleanItemName) || cleanItemName.Contains(cleanCsvItemName))
                    {
                        if (cleanCsvSearchId == cleanSearchId)
                        {
                            LogMessage($"✅ FOUND PARTIAL ITEM MATCH on line {i}: '{cleanCsvItemName}' contains/substring of '{cleanItemName}'");
                            fields[6] = "AUTO-BUY ATTEMPTED"; // Update Purchase Status column
                            lines[i] = string.Join(",", fields.Select(f => EscapeCsvField(f)));
                            foundMatch = true;
                            LogMessage($"✏️ UPDATED LINE {i} TO: {lines[i]}");
                            break;
                        }
                    }
                }
                else
                {
                    LogMessage($"⚠️ LINE {i} HAS INSUFFICIENT FIELDS: {fields.Length}");
                }
            }

            if (!foundMatch)
            {
                LogMessage($"❌ NO MATCH FOUND for Item='{itemName}', SearchId='{searchId}'");
            }

            // Write back the updated lines
            File.WriteAllLines(logFilePath, lines);
            LogMessage($"✅ UPDATED AUTO-BUY STATUS: {itemName} marked as attempted");

        }
        catch (Exception ex)
        {
            LogError($"❌ FAILED TO UPDATE AUTO-BUY STATUS: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses a CSV line into fields, handling quoted values
    /// </summary>
    private string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        string currentField = "";

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    currentField += '"';
                    i++; // Skip next quote
                }
                else
                {
                    // Toggle quote state
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // Field separator
                fields.Add(currentField);
                currentField = "";
            }
            else
            {
                currentField += c;
            }
        }

        // Add the last field
        fields.Add(currentField);

        return fields.ToArray();
    }

    /// <summary>
    /// Escapes a field for CSV format by wrapping in quotes if it contains commas, quotes, or newlines
    /// </summary>
    /// <param name="field">The field value to escape</param>
    /// <returns>The properly escaped CSV field</returns>
    private string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "\"\"";

        // If field contains comma, quote, or newline, wrap in quotes and escape internal quotes
        if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            // Escape internal quotes by doubling them
            string escaped = field.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        return field;
    }

    /// <summary>
    /// Logs search results to a text file with item name, price, and search name
    /// </summary>
    /// <param name="itemName">Name of the item</param>
    /// <param name="price">Price of the item</param>
    /// <param name="searchName">Name of the search that found this item</param>
    /// <param name="searchId">ID of the search</param>
    /// <param name="league">League name</param>
    /// <param name="autoBuyAttempted">Whether auto-buy was attempted for this item</param>
    private void LogSearchResult(string itemName, string price, string searchName, string searchId, string league, string autoBuyStatus = "MANUAL BUY")
    {
        try
        {
            LogMessage($"🔍 LOGGING ATTEMPT: LogSearchResults enabled = {Settings.LogSearchResults.Value}");

            if (!Settings.LogSearchResults.Value)
            {
                LogMessage("❌ LOGGING SKIPPED: Search results logging is disabled in settings");
                return;
            }

            // Get the plugin's temp directory path dynamically
            string pluginTempDir = GetPluginTempDirectory();
            LogMessage($"📂 PLUGIN TEMP DIR: {pluginTempDir}");

            string logFileName = "search_results.csv";
            LogMessage("📝 USING DEFAULT LOG FILE NAME: search_results.csv");

            // Ensure CSV extension
            if (!logFileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                logFileName = Path.ChangeExtension(logFileName, ".csv");
                LogMessage($"📝 CHANGED EXTENSION TO CSV: {logFileName}");
            }

            // Combine the plugin temp directory with the log file name
            string logFilePath = Path.Combine(pluginTempDir, logFileName);
            LogMessage($"📄 FULL LOG FILE PATH: {logFilePath}");

            // Ensure the directory exists
            string directory = Path.GetDirectoryName(logFilePath);
            LogMessage($"📁 LOG DIRECTORY: {directory}");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                LogMessage($"✅ CREATED LOG DIRECTORY: {directory}");
            }

            // Check if file exists and add CSV header if it's new
            bool isNewFile = !File.Exists(logFilePath);
            if (isNewFile)
            {
                string csvHeader = "Timestamp,Item Name,Price,Search Name,Search ID,League,Purchase Status,Auto Buy Enabled";
                File.WriteAllText(logFilePath, csvHeader + Environment.NewLine);
                LogMessage("📝 CREATED NEW CSV FILE WITH HEADER");
            }

            // Format timestamp
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            bool autoBuyEnabled = Settings.AutoBuy.Value;
            string autoBuyEnabledStatus = autoBuyEnabled ? "YES" : "NO";

            // Escape CSV fields that might contain commas or quotes
            string escapedItemName = EscapeCsvField(itemName);
            string escapedPrice = EscapeCsvField(price);
            string escapedSearchName = EscapeCsvField(searchName);
            string escapedLeague = EscapeCsvField(league);
            string escapedPurchaseStatus = EscapeCsvField(autoBuyStatus);

            // Create CSV line
            string csvLine = $"{timestamp},{escapedItemName},{escapedPrice},{escapedSearchName},{searchId},{escapedLeague},{escapedPurchaseStatus},{autoBuyEnabledStatus}";
            LogMessage($"📝 WRITING CSV ENTRY: {csvLine}");

            // Use a lock to prevent concurrent access issues
            lock (_recentItemsLock)
            {
                File.AppendAllText(logFilePath, csvLine + Environment.NewLine);
                LogMessage($"✅ SUCCESSFULLY WROTE TO CSV FILE: {logFilePath}");
            }

            // Log debug message if debug mode is enabled
            if (Settings.DebugMode.Value)
            {
                LogDebug($"📝 LOGGED CSV: {csvLine}");
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ FAILED TO LOG SEARCH RESULT: {ex.Message}");
            LogError($"❌ STACK TRACE: {ex.StackTrace}");
        }
    }
}