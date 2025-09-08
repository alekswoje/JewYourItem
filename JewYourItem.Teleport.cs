using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using NAudio.Wave;
using System.Media;
using JewYourItem.Utility;
using System.Threading;
using System.IO;
using System.Net;

namespace JewYourItem;

public partial class JewYourItem
{
    private async Task<bool> RefreshItemToken(RecentItem item)
    {
        try
        {
            LogDebug($"🔄 REFRESHING TOKEN: Fetching fresh token for item {item.ItemId}");
            
            // Use the trade2/fetch API to get fresh item data
            var fetchUrl = $"https://www.pathofexile.com/api/trade2/fetch/{item.ItemId}";
            
            using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
            {
                request.Headers.Add("Cookie", $"POESESSID={Settings.SessionId.Value}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                
                using (var response = await _httpClient.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        dynamic fetchResponse = JsonConvert.DeserializeObject(responseBody);
                        
                        if (fetchResponse?.result != null && fetchResponse.result.Count > 0)
                        {
                            var refreshedItem = fetchResponse.result[0];
                            string newToken = refreshedItem?.listing?.hideout_token;
                            
                            if (!string.IsNullOrEmpty(newToken))
                            {
                                var (issuedAt, expiresAt) = RecentItem.ParseTokenTimes(newToken);
                                
                                // Update the item with fresh token
                                item.HideoutToken = newToken;
                                item.TokenIssuedAt = issuedAt;
                                item.TokenExpiresAt = expiresAt;
                                
                                LogInfo($"✅ TOKEN REFRESHED: New token expires at {expiresAt:HH:mm:ss}");
                                return true;
                            }
                        }
                    }
                    
                    LogWarning($"❌ TOKEN REFRESH FAILED: API returned {response.StatusCode}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ TOKEN REFRESH ERROR: {ex.Message}");
            return false;
        }
    }

    private async void TravelToHideout(bool isManual = false)
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogDebug("🛑 Teleport blocked: Plugin is disabled");
            return;
        }
        
        // Set manual teleport flag
        _isManualTeleport = isManual;
        if (isManual)
        {
            LogInfo("🎯 MANUAL TELEPORT: User initiated teleport via hotkey");
        }
        else
        {
            LogDebug("🤖 AUTO TELEPORT: Auto teleport triggered by new item");
        }

        if (!this.GameController.Area.CurrentArea.IsHideout)
        {
            LogDebug("Teleport skipped: Not in hideout zone.");
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
            return;
        }

        // Check if game is loading - prevent teleports during loading screens
        if (GameController.IsLoading)
        {
            LogDebug("⏳ LOADING SCREEN: Game is loading, skipping teleport for safety");
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
            return;
        }
        
        // Check if we're in a valid game state for teleporting
        if (!GameController.InGame)
        {
            LogDebug("🚫 NOT IN GAME: Not in valid game state for teleporting");
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
            return;
        }

        LogDebug("=== TRAVEL TO HIDEOUT HOTKEY PRESSED ===");
        
        RecentItem currentItem;
        lock (_recentItemsLock)
        {
            LogDebug($"Recent items count: {_recentItems.Count}");
            
            if (_recentItems.Count == 0) 
            {
                LogDebug("No recent items available for travel");
                _isManualTeleport = false; // Reset flag on early return
                _currentTeleportingItem = null; // Clear current teleporting item
                return;
            }

            currentItem = _recentItems.Peek();
        }
        
        // Check if token is expired and refresh if needed
        if (currentItem.IsTokenExpired())
        {
            LogDebug($"🔄 TOKEN EXPIRED: Token for {currentItem.Name} expired at {currentItem.TokenExpiresAt:HH:mm:ss}, refreshing...");
            var refreshSuccess = await RefreshItemToken(currentItem);
            if (!refreshSuccess)
            {
                LogWarning("❌ TOKEN REFRESH FAILED: Unable to refresh token, removing item from queue");
                
                bool hasMoreItems;
                lock (_recentItemsLock)
                {
                    // Double-check queue isn't empty before dequeue (race condition protection)
                    if (_recentItems.Count > 0)
                    {
                        _recentItems.Dequeue();
                    }
                    else
                    {
                        LogWarning("⚠️ RACE CONDITION: Queue became empty before dequeue attempt");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null; // Clear current teleporting item
                        return;
                    }
                    
                    hasMoreItems = _recentItems.Count > 0;
                    if (hasMoreItems)
                    {
                        LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                    }
                }
                
                if (hasMoreItems)
                {
                    await Task.Delay(500);
                    TravelToHideout(_isManualTeleport);
                    return;
                }
                else
                {
                    LogDebug("No valid items remaining for teleport");
                    _isManualTeleport = false;
                    _currentTeleportingItem = null; // Clear current teleporting item
                    return;
                }
            }
        }
        
        LogDebug($"Attempting to travel to hideout for item: {currentItem.Name} - {currentItem.Price}");
        LogDebug($"Hideout token: {currentItem.HideoutToken}");
        
        // Set the current teleporting item for GUI display
        _currentTeleportingItem = currentItem;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.pathofexile.com/api/trade2/whisper")
        {
            Content = new StringContent($"{{ \"token\": \"{currentItem.HideoutToken}\", \"continue\": true }}", Encoding.UTF8, "application/json")
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
            
            // REMOVED: Rate limiting for teleport requests to make auto TP instant
            LogDebug("Sending teleport request...");
            
            var response = await _httpClient.SendAsync(request);
            LogDebug($"Response status: {response.StatusCode}");
            
            // Note: No longer using TP lock - using loading screen check instead
            LogDebug("📡 TELEPORT REQUEST: Sent successfully, waiting for response");
            
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
                var responseContent = await response.Content.ReadAsStringAsync();
                LogError($"Response content: {responseContent}");
                
                // Note: No TP lock to unlock - using loading screen check instead
                
                // Remove failed item and try next one if available
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound || 
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        LogMessage($"🔄 SERVICE UNAVAILABLE: Token likely expired for '{currentItem.Name}', attempting refresh...");
                        var refreshSuccess = await RefreshItemToken(currentItem);
                        if (refreshSuccess)
                        {
                            LogMessage("✅ TOKEN REFRESHED: Retrying teleport with fresh token");
                            await Task.Delay(1000); // Short delay before retry
                            TravelToHideout(_isManualTeleport);
                            return;
                        }
                        else
                        {
                            LogMessage("❌ TOKEN REFRESH FAILED: Removing item from queue");
                        }
                    }
                    else
                    {
                        LogMessage($"🗑️ ITEM EXPIRED: Removing expired item '{currentItem.Name}' and trying next...");
                    }
                    
                    bool hasMoreItems;
                    lock (_recentItemsLock)
                    {
                        // Double-check queue isn't empty before dequeue (race condition protection)
                        if (_recentItems.Count > 0)
                        {
                            _recentItems.Dequeue();
                        }
                        else
                        {
                            LogMessage("⚠️ RACE CONDITION: Queue became empty before dequeue");
                            _isManualTeleport = false; // Reset flag
                            _currentTeleportingItem = null; // Clear current teleporting item
                            return;
                        }
                        
                        hasMoreItems = _recentItems.Count > 0;
                        if (hasMoreItems)
                        {
                            LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                        }
                    }
                    
                    // Try the next item if available
                    if (hasMoreItems)
                    {
                        await Task.Delay(500); // Small delay before retry
                        TravelToHideout(_isManualTeleport); // Recursive call to try next item, preserve manual flag
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have expired");
                        _isManualTeleport = false; // Reset flag when no more items
                        _currentTeleportingItem = null; // Clear current teleporting item
                    }
                }
            }
            else
            {
                // Check response content to verify actual success
                var responseContent = await response.Content.ReadAsStringAsync();
                LogDebug($"🔍 TELEPORT RESPONSE: Status={response.StatusCode}, Content={responseContent}");
                
                // Check if the response indicates actual success
                bool actualSuccess = !responseContent.Contains("\"error\"") && 
                                   !responseContent.Contains("failed") && 
                                   !responseContent.Contains("invalid");
                
                if (actualSuccess)
                {
                    LogInfo("✅ Teleport to hideout successful!");
                    
                    // Store location for BOTH manual and auto teleports to enable mouse movement
                    _teleportedItemLocation = (currentItem.X, currentItem.Y);
                    if (_isManualTeleport)
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Manual teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    else
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Auto teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    
                    // Schedule a verification check after a short delay to confirm we're actually in hideout
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000); // Wait 3 seconds for area change
                        try
                        {
                            // Note: Area verification removed due to API access issues
                            // The teleport success is already validated by response content analysis
                            LogDebug($"✅ TELEPORT VERIFICATION: Response validation passed, assuming successful teleport");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"🔍 TELEPORT VERIFICATION: Could not verify area - {ex.Message}");
                        }
                    });
                }
                else
                {
                    LogWarning($"⚠️ TELEPORT RESPONSE INDICATES FAILURE: {responseContent}");
                    LogMessage($"❌ TELEPORT FAILED: API returned success but response indicates failure for '{currentItem.Name}'");
                    
                    // Remove this item and try next one
                    bool hasMoreItems;
                    lock (_recentItemsLock)
                    {
                        if (_recentItems.Count > 0)
                        {
                            _recentItems.Dequeue();
                        }
                        hasMoreItems = _recentItems.Count > 0;
                    }
                    
                    if (hasMoreItems)
                    {
                        LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                        await Task.Delay(500);
                        TravelToHideout(_isManualTeleport);
                        return;
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have failed");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null; // Clear current teleporting item
                    }
                }
                
                // INSTANT MOUSE MOVEMENT: Move cursor during loading screen if coordinates have been learned
                // This only works when we have learned coordinates, otherwise we wait for the purchase window
                if (Settings.MoveMouseToItem.Value && Settings.HasLearnedPurchaseWindow.Value && !GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                {
                    string tpType = _isManualTeleport ? "manual" : "auto";
                    LogDebug($"⚡ LOADING SCREEN MOUSE MOVE: {tpType} teleport request sent, moving cursor during loading for item at ({currentItem.X}, {currentItem.Y})");
                    MoveMouseToCalculatedPosition(currentItem.X, currentItem.Y);
                }
                // CRITICAL: Do NOT move mouse if purchase window is open during hideout TP
                // This prevents buying wrong items when hideout TP fails silently
                else if (Settings.MoveMouseToItem.Value && GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                {
                    LogWarning($"⚠️ MERCHANT WINDOW OPEN: Skipping mouse movement to prevent buying wrong item during hideout TP");
                    LogDebug($"🛡️ SAFETY: Will move mouse only after merchant window closes and new hideout loads");
                }
                else if (Settings.MoveMouseToItem.Value && !Settings.HasLearnedPurchaseWindow.Value)
                {
                    LogDebug($"🎓 NO LEARNED COORDINATES: Will learn and move mouse when purchase window opens");
                }
                
                lock (_recentItemsLock)
                {
                    _recentItems.Clear();
                }
                _lastTpTime = DateTime.Now;
                _isManualTeleport = false; // Reset flag
                _currentTeleportingItem = null; // Clear current teleporting item
            }
        }
        catch (Exception ex)
        {
            LogError($"Teleport request failed: {ex.Message}");
            LogError($"Exception details: {ex}");
            
            // Note: No TP lock to unlock - using loading screen check instead
            
            _isManualTeleport = false; // Reset flag on exception
            _currentTeleportingItem = null; // Clear current teleporting item on exception
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
            
            LogInfo("✅ AUTO BUY: Ctrl+Left Click completed!");
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
            
            // Calculate item position within the stash panel (bottom-right to avoid sockets)
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth * 7 / 8));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight * 7 / 8));
            
            // Get game window position
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            
            // Calculate final screen position
            int finalX = windowPos.X + itemX;
            int finalY = windowPos.Y + itemY;
            
            // Move mouse cursor
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
            
            LogDebug($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
            
            // Auto Buy: Perform Ctrl+Left Click if enabled
            if (Settings.AutoBuy.Value)
            {
                LogInfo("🛒 AUTO BUY: Enabled, performing purchase click...");
                await Task.Delay(100); // Small delay to ensure mouse movement is complete
                await PerformCtrlLeftClickAsync();
            }
        }
        catch (Exception ex)
        {
            LogError($"MoveMouseToItemLocation failed: {ex.Message}");
        }
    }

    private void TeleportToSpecificItem(RecentItem item)
    {
        try
        {
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

            // GUI teleports (clicking buttons) are considered manual
            LogDebug("🎯 GUI TELEPORT: User clicked teleport button");

            LogMessage($"🎯 SPECIFIC ITEM TP: Teleporting to {item.Name} at ({item.X}, {item.Y})");

            // Move the specific item to the front of the queue
            lock (_recentItemsLock)
            {
                var tempQueue = new Queue<RecentItem>();
                tempQueue.Enqueue(item);
                
                // Add all other items back (except the one we're teleporting to)
                foreach (var otherItem in _recentItems)
                {
                    if (otherItem != item)
                    {
                        tempQueue.Enqueue(otherItem);
                    }
                }
                
                _recentItems = tempQueue;
            }
            
            // Set this as a manual teleport and call the main teleport method
            TravelToHideout(isManual: true);
        }
        catch (Exception ex)
        {
            LogError($"TeleportToSpecificItem failed: {ex.Message}");
        }
    }

    private void RemoveSpecificItem(RecentItem itemToRemove)
    {
        try
        {
            LogMessage($"🗑️ REMOVING ITEM: {itemToRemove.Name} from recent items list");
            
            lock (_recentItemsLock)
            {
                var tempQueue = new Queue<RecentItem>();
                
                // Add all items except the one to remove
                foreach (var item in _recentItems)
                {
                    if (item != itemToRemove)
                    {
                        tempQueue.Enqueue(item);
                    }
                }
                
                _recentItems = tempQueue;
                LogMessage($"📦 Item removed. {_recentItems.Count} items remaining.");
            }
        }
        catch (Exception ex)
        {
            LogError($"RemoveSpecificItem failed: {ex.Message}");
        }
    }

    private void MoveMouseToLearnedPosition()
    {
        try
        {
            // CRITICAL: Respect plugin enable state
            if (!Settings.Enable.Value) return;

            int x = Settings.PurchaseWindowX.Value;
            int y = Settings.PurchaseWindowY.Value;

            // Move mouse to learned coordinates instantly
            mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 
                       (uint)((x * 65535) / GetSystemMetrics(SM_CXSCREEN)), 
                       (uint)((y * 65535) / GetSystemMetrics(SM_CYSCREEN)), 0, 0);

            LogDebug($"⚡ MOVED MOUSE TO LEARNED POSITION: Screen({x},{y})");

            // Auto Buy: Wait for purchase window to actually load, then click
            if (Settings.AutoBuy.Value)
            {
                LogInfo("🛒 AUTO BUY: Scheduling click after window loads...");
                Task.Run(async () =>
                {
                    // Wait up to 3 seconds for purchase window to be visible
                    for (int i = 0; i < 30; i++) // 30 * 100ms = 3 seconds
                    {
                        if (GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                        {
                            await Task.Delay(100); // Small delay to ensure window is fully loaded
                            await PerformCtrlLeftClickAsync();
                            LogInfo("🛒 AUTO BUY: Executed click after window loaded");
                            return;
                        }
                        await Task.Delay(100);
                    }
                    LogMessage("⚠️ AUTO BUY: Timeout waiting for purchase window to load");
                });
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to move mouse to learned position: {ex.Message}");
        }
    }

    private void MoveMouseToCalculatedPosition(int stashX, int stashY)
    {
        try
        {
            // CRITICAL: Respect plugin enable state
            if (!Settings.Enable.Value) return;

            // Check if we have learned panel dimensions
            if (Settings.PanelWidth.Value <= 0 || Settings.PanelHeight.Value <= 0)
            {
                // Fallback to stored learned position if we don't have panel dimensions yet
                int fallbackX = Settings.PurchaseWindowX.Value;
                int fallbackY = Settings.PurchaseWindowY.Value;
                
                mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 
                           (uint)((fallbackX * 65535) / GetSystemMetrics(SM_CXSCREEN)), 
                           (uint)((fallbackY * 65535) / GetSystemMetrics(SM_CYSCREEN)), 0, 0);

                LogDebug($"⚡ MOVED MOUSE TO CALCULATED POSITION: Stash({stashX},{stashY}) -> Screen({fallbackX},{fallbackY}) [Using fallback learned position]");
            }
            else
            {
                // Calculate the actual position using stored panel dimensions
                float cellWidth = (float)Settings.PanelWidth.Value / 12f;
                float cellHeight = (float)Settings.PanelHeight.Value / 12f;
                int screenX = (int)(Settings.PanelLeft.Value + (stashX * cellWidth) + (cellWidth * 7 / 8));
                int screenY = (int)(Settings.PanelTop.Value + (stashY * cellHeight) + (cellHeight * 7 / 8));

                // Move mouse to calculated coordinates instantly
                mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 
                           (uint)((screenX * 65535) / GetSystemMetrics(SM_CXSCREEN)), 
                           (uint)((screenY * 65535) / GetSystemMetrics(SM_CYSCREEN)), 0, 0);

                LogDebug($"⚡ MOVED MOUSE TO CALCULATED POSITION: Stash({stashX},{stashY}) -> Screen({screenX},{screenY}) [Calculated from panel dimensions]");
            }

            // If Auto Buy is enabled, wait for purchase window to be visible before clicking
            if (Settings.AutoBuy.Value)
            {
                LogInfo("🛒 AUTO BUY: Waiting for purchase window to be visible before clicking...");
                Task.Run(async () =>
                {
                    // Wait up to 10 seconds for purchase window to become visible
                    int attempts = 0;
                    while (attempts < 100 && !GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                    {
                        await Task.Delay(100);
                        attempts++;
                    }

                    if (GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible)
                    {
                        LogInfo("🛒 AUTO BUY: Purchase window visible, performing Ctrl+Left click...");
                        await PerformCtrlLeftClickAsync();
                    }
                    else
                    {
                        LogInfo("🛒 AUTO BUY: Timeout waiting for purchase window to become visible");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ MOVE MOUSE TO CALCULATED POSITION FAILED: {ex.Message}");
        }
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
}
