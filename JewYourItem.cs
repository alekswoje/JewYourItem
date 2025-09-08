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
    // Debug-aware logging methods
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
        LogMessage($"[WARN] {message}");
    }
    
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
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
    {
        Timeout = TimeSpan.FromSeconds(30) // 30 second timeout to prevent infinite hangs
    };
    private Queue<RecentItem> _recentItems = new Queue<RecentItem>();
    private readonly object _recentItemsLock = new object(); // Thread safety for _recentItems queue
    private bool _playSound = true;
    private bool _lastHotkeyState = false;
    private bool _lastStopAllState = false;
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
    public static DateTime _pluginStartTime = DateTime.Now;
    private static DateTime _lastGlobalReset = DateTime.Now;
    private static bool _emergencyShutdown = false;
    private static readonly Random _random = new Random();
    private DateTime _lastTickProcessTime = DateTime.MinValue;
    private DateTime _lastAreaChangeTime = DateTime.MinValue;
    private DateTime _lastSettingsChangeTime = DateTime.MinValue;
    private bool _areaChangeCooldownLogged = false;
    private DateTime _lastTeleportTime = DateTime.MinValue;
    private (int x, int y)? _teleportedItemLocation = null;
    private bool _isManualTeleport = false;
    private RecentItem _currentTeleportingItem = null;
    
    // Note: Removed TP lock system - now using loading screen check instead
    
    // Purchase window state tracking to prevent accidental purchases
    private bool _allowMouseMovement = true;
    private bool _windowWasClosedSinceLastMovement = true;

    public override bool Initialise()
    {
        _rateLimiter = new ConservativeRateLimiter(LogMessage, LogError);
        _sessionIdBuffer = Settings.SessionId.Value ?? "";
        if (string.IsNullOrEmpty(_sessionIdBuffer))
        {
            LogWarning("Warning: Session ID is empty. Please set it in the settings.");
        }
        
        LogDebug("                                                                                                    ");
        LogDebug("::::::::::::::::::::::::::::::::::::::::::::::::::-*@@@@@@%+:::::*=::=-:::::::::::::::::::::::::: ");
        LogDebug("::::::::::::::::::::::::::::::::::::::::::::::=@@@+*@@#=@*+*@#@@@%@+=@+%+::::::::::::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::::::::::+@@@=+::*@@@#*+**%+@%@@*@%@@@#:=+:::::::::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::::::::#@%-::-*@@*:::::::::::=#*@#%@@@*@@+:::::::::::::::::::::: ");
        LogDebug("::::::::::::::::::::::::::::::::::::::-@@%:@:*@*:::::::::::::::::::::-@@+@#@::::::::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::::+@+@@=@%:::::::::::::::::::::::::::==::@*::::::::::::::::::: ");
        LogDebug("::::::::::::::::::::::::::::::::::::=@*@-@*@:::::::::::::::::::::::::::::::::=@-::::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::=@-@@@%#@*::::::::::::::::::::::::::::::::::@::::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::%@=@@@#%@#+%+-%@::::::::::::::::::::::::::::+@:::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::::@+%=@%#@*@@@#@@-:::::::::::::::::::::::::::::+*::::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::::::-@@@@@@@+@@%@*+=-=@*:::::::::::::::::::::::::::*%:::::::::::::: ");
        LogDebug("::::::::::::::::::::::::::::::=@%-@@@%@@*@@@=@@@@@@*::::*@%:#@--+::::+@@@@@#::::::+#::::::::::::: ");
        LogDebug(":::::::::::::::::::::::::::::*#*@+@%+@#@@#@%@@*%-=-::::%@#:*=@+*%@%=-::*@@@%-+@@:::@-:::::::::::: ");
        LogDebug(":::::::::::::::::::::-*%@@@#-#%=@@@@*@@@@@@@@%#@@*@+::::-##@@@*@@@@@@@-:=:::+-:::::**-=:::::::::: ");
        LogDebug("::::::::::::::::::*%*-:=:::::=%@@#@@*@@%@@##@#@@*=::::::@@@:::::::-+%@@@%*#@#*@#@@@+%@+:::::::::: ");
        LogDebug("::::::::::::::::%*-:::::=%+::+@%@@@@@#+@@@@%%+*#::::-:::@:=:::@@@@@@@#:=@@@-+@@*%@@*:@::::::::::: ");
        LogDebug(":::::::::::::::@::::::::::+*:::::@@-:---:=@@@@%-::==::#@%*-=*@@-=@++:@*:::+::-@@=-:%@%::::::::::: ");
        LogDebug(":::::::::::::-@:::%-:::::::@-::::@--@=-+@@-:@@@-::::::***@@@@-#@@@@@@@@::@+::::@@@@-*+::::::::::: ");
        LogDebug(":::::::::::::%:::=@:::::::::%:::=@:-@=:*@:%+@@::-:-+:::::::::-::-++@+=::::::::::::-@#:::::::::::: ");
        LogDebug("::::::::::::@-:::@::::::::::@+::-@:-@*:@@:-=@@=:+++-::::::::::::::::::::::::::::::::*#::::::::::: ");
        LogDebug(":::::::::::@::::=+::::::::::-@::+@=:%*:@@:-@@#=:=*=::::::::::::::::::::::::::::::::::-@:::::::::: ");
        LogDebug("::::::::::@-::::@:::::::::::::@::@%*:::-:::@@@*=@==-::-:::::::::::::::::::::::::::::::=@::::::::: ");
        LogDebug(":::::::::=@*:::-@:::::::::::::=@:@-+@:@@::-@@@%@@-@@%:@%*@@=:=+:::::::@@%-:-%%:::::::::##:::::::: ");
        LogDebug(":::::::::*+%:::-@::::::::::::::#=@=:@+::::=@%+*@@=*@@%@:-:::-@*@==:+@@=::::::-@-:--:::::%:::::::: ");
        LogDebug("::::::::=#:-#-*=@::::::::::::::-*%@:+%:::@@@#@#@@%@@*@@-#@+@=::-#@%+:%:=#*-::::=-=-:::::%:::::::: ");
        LogDebug(":::::::-@:::::*-@:::::::::::::::@+@::#@@@@@@@@@@@@#%@#%%=%#*@%@%##@@*#%-@@@@@+::::::::::%:::::::: ");
        LogDebug(":::::::+*:::::=+@=::::::::::::::*%@::-%=@@+@@@@%@@+*@@@@@@@@@@@%@@@#@+#@+@@#--%#:::::-::#+::::::: ");
        LogDebug("::::::::%-:::::-+@%:::::::::::::::@@:::+@@#@@@%@@@*%*@@@@@*+@@@@@@@@@%@@@@@:@@@*#%@@=::::=%::::::: ");
        LogDebug("::::::::*-:::::-++#:::::::::::::::*=::::%*@*@+@@*@=#@@@@@%=@@+-::::--:::::=::-@@@-::@@+::#=::::::: ");
        LogDebug("::::::::##::::::#-#:::::::::::::::-%::::@-==@@@@@@@@%@@@=@=:@%:#@@@-::::::-::+@@=#=::::--::::::::: ");
        LogDebug("::::::::#:::::::@:%::::::::::::::::@-::-:@*@*@#@@*+@=#@@@%:=:@@@-:#++@@@@@%#@*@=:::::::::::::::::: ");
        LogDebug("::::::::@@::::::#-@::::::::::::::::*#::::=@#@@@@@%@%@@@@@*:*#:=@@#+@@#::=@#*@-:::::::::::::::::::: ");
        LogDebug("::::::::-*:#=::::*#@:::::::::::::::::%-::@@-@@@@*@*@+@#@*@@-*#::=@@@#@%@@@*@=::::::::::::::::::::: ");
        LogDebug("::::::::@-:::::::=#%=::::::::::::::::-@::@@@@*%@@-@@@@@@@@*@@+%@+:::::+@@@#:#@@:::::::::::::::::::: ");
        LogDebug("::::::::-@-::::::::@=#:::::::::::::::::-@=*:=@#+##@@=@@@@@@@@@@@%@@*:::::::-#@#@@-:::::::::::::::::: ");
        LogDebug("::::::::@%:::::::::#-%::::::::::::::::::=@:%-@@%#@#@@@@@+#@@=@@@@@@@@@@@@@@*@@@@++%#*%:::::::::::::: ");
        LogDebug("::::::::%+%+::::::::=+#:::::::::::::::::::=%-*=%%%*#=@@=@#@@*@@@#@@@@@@@*@*@@@@@@#:=--@:::::::::::::: ");
        LogDebug("::::::::@::+:::::::::@==:::::::::::::::::::*-*+*%@@@@#@@@@@%@@@%@@@@#@#%@@@@@+::::::+#::::::::::::::: ");
        LogDebug("::::::=+:::::::::::::+*#::::::::::::::::::::#+@:*@+@@**@@@@@@@@@@@%@@@@@@%@@:::::*@==+%+*+-:::::::::: ");
        LogDebug("::::::+=:::::::::::::%@-::::::::::::::::::::#*+:-@@*-@@=#@+@@@@@@#@@@%*@@@:::::-@+:::@:::=+::::::::: ");
        LogDebug("::::::*-:::::::::::::+*-:::::::::::::::::::::@%::-#:@@@+@@@@#@@%@@+@=@@@-::::::-:@%-%=::-@@#:::::::: ");
        LogDebug("::::::+-::::::::::::::@+=:::::::::::::::::::::*+*%=%:=@+@@@@+=@@@@@@@@%-=::::::::@@+::+=:::#@::::::: ");
        LogDebug("::::::=+::::::::::::::+##+:::::::::::::::::::::%+*:=*@@:*@#@*@@@@#@@%:+::::::::::::::-+:::#@%::::::: ");
        LogDebug("::::::::::::::::::::::#--::::::::::::::::::-@@%%#::#@%:=#@@@*:::::=%-::::::::::::*%-::+*#-::#:::::: ");
        LogDebug("::::::::::::::::::::::@:::::::::::::::::=@%-::+@#*==-::::::::::::::@::::::%@=-=#@@@:::::::+*:::::: ");
        LogDebug(":::::::::::::::::::::::%::::::::::::::::@=:::-*:::::::::::::::::::*=@:::::::::::::-::+@+-*%::::::: ");
        LogDebug("::::::::::::::::::::::+#::::::::::::::::::::@-:::::::::::::::::::-**=:::+@@*@#%@*=@*@#@-#:::::::: ");
        LogDebug(":::::::::::::::::::::::+*::::::::::::::::::*@:::::::::::::::::::::*+=::-@+-@:%+*-=*#=:-@-:::::::: ");
        LogDebug("::::::::::::::::::::::::+%:::::::::::::::::*::::::::+-:::::::::::==#:*:+*::*:::*::+@-:*=::::::::: ");
        LogDebug("::::::::::::::::::::::::::#::::::::::::::::=::::::::#-::::::::::=@%===*-@@@@:-*@@#@-=*-:::::::::: ");
        LogDebug("::::::::::::::::::::::::::::##:::::::::::::::::::::#=::::::#*:::::::::::::::::::::::::::::::::::: ");
        LogDebug("                                                                                                    ");
        
        LogDebug("Plugin initialized");
        LogDebug($"Hotkey set to: {Settings.TravelHotkey.Value}");
        LogDebug($"Sound enabled: {Settings.PlaySound.Value}");
        LogDebug($"Auto TP enabled: {Settings.AutoTp.Value}");
        LogDebug($"GUI enabled: {Settings.ShowGui.Value}");
        LogDebug("TP Cooldown: Using dynamic locking (locked until window loads or 10s timeout)");
        LogDebug($"Move Mouse to Item enabled: {Settings.MoveMouseToItem.Value}");

        return true;
    }

    // Helper methods moved to JewYourItem.Teleport.cs

    // RemoveSpecificItem method moved to JewYourItem.Teleport.cs

    public override void AreaChange(AreaInstance area)
    {
        LogDebug($"🌍 AREA CHANGE: {area?.Area?.Name ?? "Unknown"} - NOT stopping listeners (live searches continue)");
        _lastAreaChangeTime = DateTime.Now;
        _lastTeleportTime = DateTime.Now; // Set teleport delay after area change
        
        // DON'T stop all listeners - let them continue running
        // Live searches should persist across zone changes
        // DON'T clear recent items - let the user manage them manually
        // Items can still be valid across zone changes (especially to/from hideout)
        
        // DON'T clear _teleportedItemLocation - we need it to persist until purchase window opens
        LogDebug("🌍 AREA CHANGE: Keeping recent items list intact for user to manage");
        LogDebug("⏳ TELEPORT DELAY: 1 second delay after area change to allow item purchase");
    }



    // PlaySoundWithNAudio method moved to JewYourItem.Teleport.cs

    // Helper method for partial classes to access Input
    private bool IsKeyPressed(Keys key)
    {
        return Input.GetKeyState(key);
    }

    public override async void Tick()
    {
        // CRITICAL: IMMEDIATE PLUGIN DISABLE CHECK - HIGHEST PRIORITY
        if (!Settings.Enable.Value)
        {
            LogDebug("🛑 PLUGIN DISABLED: Stopping all listeners immediately");
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

        // Note: Removed TP lock timeout check - now using loading screen check instead

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
                LogDebug("🛑 PLUGIN JUST DISABLED: Force stopping all listeners");
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
                    LogDebug($"Hotkey {Settings.TravelHotkey.Value} pressed");
                    TravelToHideout(isManual: true);
                }
                _lastHotkeyState = hotkeyState;

                // Check if purchase window just became visible and move mouse to teleported item
                bool purchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
                
                // Track window close events (throttled)
                if (!purchaseWindowVisible && _lastPurchaseWindowVisible)
                {
                    LogDebug("🚪 PURCHASE WINDOW CLOSED (Throttled): Mouse movement will be allowed on next window open");
                    _windowWasClosedSinceLastMovement = true;
                    _allowMouseMovement = true;
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
                LogDebug($"🌍 AREA CHANGE COOLDOWN: Skipping listener management for 5s after area change (can be overridden by settings changes)");
                _areaChangeCooldownLogged = true;
            }
            
            // ONLY process basic functionality IF plugin is enabled
            if (Settings.Enable.Value)
            {
                bool areaChangeHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
                if (areaChangeHotkeyState && !_lastHotkeyState)
                {
                    LogDebug($"Hotkey {Settings.TravelHotkey.Value} pressed");
                    TravelToHideout(isManual: true);
                }
                _lastHotkeyState = areaChangeHotkeyState;

                bool areaChangePurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
                
                // Track window close events (area change)
                if (!areaChangePurchaseWindowVisible && _lastPurchaseWindowVisible)
                {
                    LogDebug("🚪 PURCHASE WINDOW CLOSED (Area): Mouse movement will be allowed on next window open");
                    _windowWasClosedSinceLastMovement = true;
                    _allowMouseMovement = true;
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
        
        // Periodic reset of global connection attempts (every 5 minutes)
        if ((DateTime.Now - _lastGlobalReset).TotalMinutes >= 5 && JewYourItem._globalConnectionAttempts > 0)
        {
            LogDebug($"🔄 PERIODIC RESET: Clearing global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) after 5 minutes");
            JewYourItem._globalConnectionAttempts = 0;
            _lastGlobalReset = DateTime.Now;
        }
        
        if (recentSettingsChange)
        {
            LogDebug("🔄 TICK: Processing listener management (INSTANT - settings changed)...");
        }
        else
        {
            LogDebug("🔄 TICK: Processing listener management...");
        }

        bool currentHotkeyState = Input.GetKeyState(Settings.TravelHotkey.Value);
        if (currentHotkeyState && !_lastHotkeyState)
        {
            LogInfo($"🎮 MANUAL HOTKEY PRESSED: {Settings.TravelHotkey.Value} - initiating manual teleport");
            TravelToHideout(isManual: true);
        }
        _lastHotkeyState = currentHotkeyState;

        // Check Stop All hotkey
        bool currentStopAllState = Input.GetKeyState(Settings.StopAllHotkey.Value);
        if (currentStopAllState && !_lastStopAllState)
        {
            LogMessage($"🛑 STOP ALL HOTKEY PRESSED: {Settings.StopAllHotkey.Value} - force stopping all searches");
            ForceStopAll();
            LogMessage("🛑 ALL SEARCHES STOPPED: Stop All hotkey pressed");
        }
        _lastStopAllState = currentStopAllState;

        // Mouse movement logic: Move mouse when purchase window is open and we have a teleported item
        bool currentPurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
        bool isGameLoading = GameController.IsLoading;
        
        // Track when purchase window closes or game starts loading
        if (!currentPurchaseWindowVisible && _lastPurchaseWindowVisible)
        {
            LogMessage($"🚪 PURCHASE WINDOW CLOSED: Ready to move mouse on next window open. HasLocation: {_teleportedItemLocation.HasValue}");
        }
        else if (isGameLoading && _teleportedItemLocation.HasValue)
        {
            LogMessage($"⏳ GAME LOADING: Ready to move mouse on next window open. HasLocation: {_teleportedItemLocation.HasValue}");
        }
        
        // Move mouse when purchase window is open and we have a teleported item
        if (currentPurchaseWindowVisible && Settings.MoveMouseToItem.Value && _teleportedItemLocation.HasValue)
        {
            // Check if this is a new window opening or if we have a new teleported item
            bool isNewWindowOpen = !_lastPurchaseWindowVisible;
            
            if (isNewWindowOpen)
            {
                LogMessage($"🖱️ PURCHASE WINDOW OPENED: MoveMouseToItem = {Settings.MoveMouseToItem.Value}");
            }
            else
            {
                LogMessage($"🖱️ PURCHASE WINDOW ALREADY OPEN: Moving mouse to new teleported item");
            }
            
            LogInfo($"🎯 MOUSE MOVE: Moving to teleported item at ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
            MoveMouseToItemLocation(_teleportedItemLocation.Value.x, _teleportedItemLocation.Value.y);
            _teleportedItemLocation = null; // Clear after successful mouse movement
        }
        else if (currentPurchaseWindowVisible && Settings.MoveMouseToItem.Value && !_teleportedItemLocation.HasValue)
        {
            LogDebug($"🎯 MOUSE MOVE SKIPPED: Purchase window open but no teleported item location available");
        }
        else if (!currentPurchaseWindowVisible && Settings.MoveMouseToItem.Value && _teleportedItemLocation.HasValue)
        {
            LogDebug($"🎯 MOUSE MOVE WAITING: Purchase window closed, waiting for it to open to move mouse to ({_teleportedItemLocation.Value.x}, {_teleportedItemLocation.Value.y})");
        }
        else if (!Settings.MoveMouseToItem.Value)
        {
            LogDebug($"🎯 MOUSE MOVE DISABLED: MoveMouseToItem setting is disabled");
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
                // Multiple safety checks before attempting restart - use shorter cooldown for auth errors
                double cooldownSeconds = listener.IsAuthenticationError ? 10 : RestartCooldownSeconds; // 10 seconds for auth errors, 300 for others
                LogDebug($"🔍 COOLDOWN CHECK: Search {listener.Config.SearchId.Value}, IsAuthError={listener.IsAuthenticationError}, UsingCooldown={cooldownSeconds}s");
                if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < cooldownSeconds)
                {
                    if (listener.IsAuthenticationError)
                    {
                        LogDebug($"🔐 AUTH ERROR COOLDOWN: Search {listener.Config.SearchId.Value} waiting for session ID fix ({cooldownSeconds - (DateTime.Now - listener.LastErrorTime).TotalSeconds:F1}s remaining)");
                    }
                    else
                    {
                        LogDebug($"🛡️ RESTART BLOCKED: Search {listener.Config.SearchId.Value} in error cooldown ({cooldownSeconds - (DateTime.Now - listener.LastErrorTime).TotalSeconds:F1}s remaining)");
                    }
                    continue;
                }

                if ((DateTime.Now - listener.LastConnectionAttempt).TotalSeconds < 60)
                {
                    LogDebug($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} connection throttled ({60 - (DateTime.Now - listener.LastConnectionAttempt).TotalSeconds:F1}s remaining)");
                    continue;
                }

                if (listener.ConnectionAttempts >= 2 && (DateTime.Now - listener.LastConnectionAttempt).TotalHours < 1)
                {
                    LogDebug($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} exceeded restart attempts (2 per HOUR)");
                    continue;
                }

                if (listener.ConnectionAttempts >= 5)
                {
                    LogDebug($"🚨 EMERGENCY RESTART BLOCK: Search {listener.Config.SearchId.Value} exceeded TOTAL session restart limit (5 EVER)");
                    continue;
                }

                // REMOVED: Rate limiting for WebSocket restarts to make reconnection instant
                
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
                
                // EMERGENCY: Prevent rapid creation (but allow user-initiated restarts and startup)
                var timeSinceStart = DateTime.Now - _pluginStartTime;
                bool isInitialStartup = timeSinceStart.TotalMinutes < 2;
                int creationLimit = isInitialStartup ? 20 : 5; // Allow many during startup, few during runtime
                
                if (JewYourItem._globalConnectionAttempts >= creationLimit && !recentSettingsChange)
                {
                    LogDebug($"🚨 EMERGENCY: Preventing new listener creation - global limit reached ({JewYourItem._globalConnectionAttempts}/{creationLimit} attempts)");
                    continue;
                }
                else if (recentSettingsChange && JewYourItem._globalConnectionAttempts >= (isInitialStartup ? 20 : 2))
                {
                    // Reset global attempts for user-initiated changes
                    LogDebug($"🔄 USER ACTION: Resetting global connection attempts ({JewYourItem._globalConnectionAttempts} -> 0) for settings change");
                    JewYourItem._globalConnectionAttempts = 0;
                }
                
                // REMOVED: All rate limiting for listener creation to make startup instant
                LogDebug($"⚡ INSTANT START: No rate limiting for maximum speed");
                
                // FINAL SAFETY CHECK: Ensure no duplicate was created during processing
                var lastMinuteCheck = _listeners.Any(l => 
                    l.Config.League.Value == config.League.Value && 
                    l.Config.SearchId.Value == config.SearchId.Value);
                
                if (lastMinuteCheck)
                {
                    LogMessage($"🛡️ LAST MINUTE PREVENTION: Duplicate detected right before creation for {config.SearchId.Value}");
                    continue;
                }
                
                var newListener = new SearchListener(this, config, LogMessage, LogError, LogDebug, LogInfo, LogWarning);
                _listeners.Add(newListener);
                LogMessage($"🆕 CREATING NEW LISTENER: Search {config.SearchId.Value} (Total listeners: {_listeners.Count})");
                newListener.Start(LogMessage, LogError, Settings.SessionId.Value);
            }
            else
            {
                LogDebug($"📍 EXISTING LISTENER: {config.SearchId.Value} (Running: {existingListener.IsRunning}, Connecting: {existingListener.IsConnecting})");
            }
        }
    }

    // Render method moved to JewYourItem.Gui.cs

    private void StopAll()
    {
        LogMessage($"🛑 STOPPING ALL: {_listeners.Count} listeners");
        foreach (var listener in _listeners)
        {
            LogMessage($"🛑 STOPPING: Search {listener.Config.SearchId.Value}");
            listener.Stop();
        }
        _listeners.Clear();
        LogMessage("✅ ALL LISTENERS STOPPED");
    }

    private void ForceStopAll()
    {
        LogMessage("🚨 FORCE STOPPING: All listeners...");
        LogMessage($"📊 Current listener count: {_listeners.Count}");
        
        // Create a copy of the list to avoid modification during iteration
        var listenersToStop = _listeners.ToList();
        LogMessage($"📋 Listeners to stop: {listenersToStop.Count}");
        
        foreach (var listener in listenersToStop)
        {
            try
            {
                LogMessage($"🛑 STOPPING: Search {listener.Config.SearchId.Value} (Running: {listener.IsRunning}, Connecting: {listener.IsConnecting})");
                listener.Stop();
                LogMessage($"✅ STOPPED: Search {listener.Config.SearchId.Value}");
            }
            catch (Exception ex)
            {
                LogError($"❌ Error stopping listener {listener.Config.SearchId.Value}: {ex.Message}");
            }
        }
        
        // Clear the listeners list
        _listeners.Clear();
        
        // Clear recent items
        lock (_recentItemsLock)
        {
            _recentItems.Clear();
        }
        
        // Reset global connection attempts
        _globalConnectionAttempts = 0;
        
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

    // DrawSettings method moved to JewYourItem.Gui.cs
}