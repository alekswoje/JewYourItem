using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2.Shared.Nodes;
using ImGuiNET;

namespace JewYourItem;

public partial class JewYourItem
{
    public override void Render()
    {
        // Debug logging to track when Render is called during loading
        if (GameController.IsLoading)
        {
            LogDebug("🖼️ RENDER: Called during loading screen");
        }
        
        // CRITICAL: Respect plugin enable state for ALL rendering
        if (!Settings.Enable.Value)
        {
            // If plugin is disabled, ensure all listeners are stopped
            if (_listeners.Count > 0)
            {
                LogMessage("🛑 PLUGIN DISABLED: Force stopping all listeners from Render method");
                ForceStopAll();
            }
            return;
        }
        
        if (!Settings.ShowGui.Value) return;

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

        // Show current teleporting item if we're in the process of teleporting
        if (_currentTeleportingItem != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.8f, 0.0f, 1.0f)); // Orange color
            ImGui.Text("🚀 Teleporting to:");
            ImGui.SameLine();
            ImGui.Text($"{_currentTeleportingItem.Name} - {_currentTeleportingItem.Price}");
            ImGui.PopStyleColor();
            ImGui.Spacing();
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
                else if (listener.IsAuthenticationError) status = "🔐 Authentication Error";
                else status = "❌ Disconnected";
                
                // Use red text for authentication errors
                if (listener.IsAuthenticationError)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color (ABGR format)
                    ImGui.BulletText($"{listener.Config.SearchId.Value}: {status}");
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.BulletText($"{listener.Config.SearchId.Value}: {status}");
                }
                
                // Indent additional info
                double cooldownSeconds = listener.IsAuthenticationError ? 10 : RestartCooldownSeconds;
                if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < cooldownSeconds ||
                    listener.ConnectionAttempts > 0)
                {
                    ImGui.Indent();
                    
                    if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < cooldownSeconds)
                    {
                        float remainingCooldown = (float)(cooldownSeconds - (DateTime.Now - listener.LastErrorTime).TotalSeconds);
                        if (listener.IsAuthenticationError)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color for auth errors
                            ImGui.Text($"🔐 Auth Error - Fix Session ID: {remainingCooldown:F1}s");
                            ImGui.PopStyleColor();
                        }
                        else
                        {
                            ImGui.Text($"⏱️ Error Cooldown: {remainingCooldown:F1}s");
                        }
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
            
            // Show authentication error help if any listener has auth errors
            if (_listeners.Any(l => l.IsAuthenticationError))
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color
                ImGui.Text("🔐 Authentication Error Detected!");
                ImGui.PopStyleColor();
                ImGui.Text("💡 Fix: Update your POESESSID in settings");
                ImGui.Text("📋 Get it from browser cookies on pathofexile.com");
            }
        }
        else
        {
            ImGui.Text("🔍 No active searches");
        }
        
        RecentItem[] itemsArray;
        lock (_recentItemsLock)
        {
            itemsArray = _recentItems.ToArray(); // Convert to array to avoid modification during iteration
        }
        
        if (itemsArray.Length > 0)
        {
            ImGui.Separator();
            ImGui.Text("📦 Recent Items:");
            ImGui.Spacing();
            
            var itemsToRemove = new List<RecentItem>();
            
            for (int i = 0; i < itemsArray.Length; i++)
            {
                var item = itemsArray[i];
                
                // Use unique IDs for buttons
                ImGui.PushID($"item_{i}");
                
                // Start a horizontal layout
                ImGui.AlignTextToFramePadding();
                
                // TP Button (Door icon) - make it smaller and green
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.7f, 0.2f, 0.8f)); // Green
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.8f, 0.3f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.6f, 0.1f, 1.0f));
                
                if (ImGui.Button("🚪", new Vector2(30, 0)))
                {
                    LogMessage($"🚪 TP Button clicked for: {item.Name}");
                    TeleportToSpecificItem(item);
                }
                
                ImGui.PopStyleColor(3);
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Teleport to {item.Name}");
                }
                
                ImGui.SameLine();
                
                // X Button (Remove) - make it smaller and red
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 0.8f)); // Red
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.1f, 0.1f, 1.0f));
                
                if (ImGui.Button("❌", new Vector2(30, 0)))
                {
                    LogMessage($"❌ Remove Button clicked for: {item.Name}");
                    itemsToRemove.Add(item);
                }
                
                ImGui.PopStyleColor(3);
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Remove {item.Name} from list");
                }
                
                ImGui.SameLine();
                
                // Item text
                ImGui.BulletText($"{item.Name} - {item.Price}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({item.X}, {item.Y})");
                
                // Show token expiration status
                if (item.TokenExpiresAt != DateTime.MinValue)
                {
                    ImGui.SameLine();
                    var timeUntilExpiry = item.TokenExpiresAt - DateTime.Now;
                    if (timeUntilExpiry.TotalSeconds > 0)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"[Expires: {timeUntilExpiry.Minutes:D2}:{timeUntilExpiry.Seconds:D2}]");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "[EXPIRED]");
                    }
                }
                
                ImGui.PopID();
            }
            
            // Remove items that were marked for removal
            foreach (var itemToRemove in itemsToRemove)
            {
                RemoveSpecificItem(itemToRemove);
            }
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.End();

        if (Settings.ShowGui.Value)
        {
            // Show basic status
            Graphics.DrawText($"JewYourItem: {_listeners.Count(l => l.IsRunning)} active", new Vector2(100, 100), Color.LightGreen);
            
            // Show current teleporting item during loading screens
            if (_currentTeleportingItem != null)
            {
                Graphics.DrawText($"🚀 Teleporting to: {_currentTeleportingItem.Name} - {_currentTeleportingItem.Price}", new Vector2(100, 120), Color.Orange);
            }
            
            // Show loading screen indicator and additional info
            if (GameController.IsLoading)
            {
                Graphics.DrawText("⏳ Loading...", new Vector2(100, 140), Color.Yellow);
                
                // Show recent items count
                int recentItemsCount;
                lock (_recentItemsLock)
                {
                    recentItemsCount = _recentItems.Count;
                }
                if (recentItemsCount > 0)
                {
                    Graphics.DrawText($"📦 {recentItemsCount} items in queue", new Vector2(100, 160), Color.LightBlue);
                }
            }
        }
    }

    public override void DrawSettings()
    {
        // Check if plugin was just disabled and clean up if needed
        if (!Settings.Enable.Value && _listeners.Count > 0)
        {
            LogMessage("🛑 PLUGIN DISABLED: Force stopping all listeners from DrawSettings method");
            ForceStopAll();
        }
        
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
                if (IsKeyPressed(key) && key != Keys.None)
                {
                    Settings.TravelHotkey.Value = key;
                    LogMessage($"Hotkey changed to: {key}");
                    break;
                }
            }
        }

        var stopAllHotkey = Settings.StopAllHotkey.Value.ToString();
        ImGui.Text("Stop All Searches Hotkey:");
        ImGui.SameLine();
        ImGui.InputText("##StopAllHotkey", ref stopAllHotkey, 100, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.IsItemActive())
        {
            LogMessage("Waiting for new stop all hotkey input...");
            foreach (Keys key in Enum.GetValues(typeof(Keys)))
            {
                if (IsKeyPressed(key) && key != Keys.None)
                {
                    Settings.StopAllHotkey.Value = key;
                    LogMessage($"Stop All hotkey changed to: {key}");
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

        ImGui.Text("TP Cooldown: Dynamic (locked until window loads or 10s timeout)");

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

        var maxRecentItems = Settings.MaxRecentItems.Value;
        ImGui.SliderInt("Max Recent Items", ref maxRecentItems, 1, 20);
        if (ImGui.IsItemDeactivatedAfterEdit() && !_settingsUpdated)
        {
            Settings.MaxRecentItems.Value = maxRecentItems;
            // Trim queue if new limit is smaller
            lock (_recentItemsLock)
            {
                while (_recentItems.Count > maxRecentItems)
                    _recentItems.Dequeue();
            }
            _settingsUpdated = true;
            LogDebug($"Max Recent Items setting changed to: {maxRecentItems}");
        }
        if (!ImGui.IsItemActive())
        {
            _settingsUpdated = false;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Maximum number of recent items to keep in the list (1-20)");
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
        
        // Stop All button
        if (ImGui.Button("🛑 Stop All Searches"))
        {
            LogMessage("🛑 STOP ALL BUTTON PRESSED: Force stopping all searches");
            ForceStopAll();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Stop all active search listeners");
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
            RecentItem item = null;
            lock (_recentItemsLock)
            {
                if (_recentItems.Count > 0)
                {
                    item = _recentItems.Peek();
                }
            }
            
            if (item != null)
            {
                LogDebug($"Testing move mouse to recent item: {item.Name} at ({item.X}, {item.Y})");
                MoveMouseToItemLocation(item.X, item.Y);
            }
            else
            {
                LogDebug("No recent items available for mouse movement test");
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Test moving mouse to the most recent item (requires purchase window to be open). Will also test Auto Buy if enabled.");
        }

        base.DrawSettings();
    }

}
