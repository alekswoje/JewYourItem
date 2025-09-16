# JewYourItem - Path of Exile 2 Trade Plugin

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A powerful automation plugin for Path of Exile 2 that monitors trade search results and automatically teleports you to valuable items when they appear on the market.

## 🌟 Features

### 🔍 Real-time Trade Monitoring
- **Live Search Monitoring**: Continuously monitors your trade search results from pathofexile.com/trade2
- **Multi-Search Support**: Run multiple trade searches simultaneously with organized search groups
- **Automatic Updates**: Real-time notifications when new items matching your searches appear

### ⚡ Automated Actions
- **Auto-Teleport**: Automatically teleport to items when found (optional)
- **Smart Mouse Movement**: Automatically moves your mouse cursor to highlighted items in the purchase window
- **Auto-Purchase**: Automatically Ctrl+Left Click to purchase items (optional, with safety features)
- **Sound Alerts**: Audio notifications when new items are found

### 🛡️ Safety & Protection
- **Purchase Window Detection**: Only performs actions when the purchase window is actually open
- **Window State Tracking**: Prevents accidental purchases by tracking window close/open events
- **Dynamic TP Locking**: Intelligent teleport cooldown system to prevent spam teleporting
- **Emergency Shutdown**: Automatic throttling when connection limits are reached
- **Rate Limiting**: Built-in rate limiting to prevent overwhelming the trade API

### 📊 Logging & Analytics
- **CSV Logging**: Comprehensive logging of all search results to CSV files
- **Purchase Tracking**: Tracks manual vs automatic purchases
- **Performance Analytics**: Monitor search effectiveness and response times
- **Auto-buy Status Updates**: Real-time tracking of purchase attempts

### 🎮 User Interface
- **ImGui Interface**: Clean, modern GUI for easy configuration
- **Search Group Management**: Organize searches into logical groups
- **Real-time Status**: Live status of all active search listeners
- **One-click Log Access**: Easy access to CSV log files
- **Hotkey Support**: Configurable hotkeys for manual teleportation

## 🚀 Installation

### Prerequisites
- Path of Exile 2
- ExileCore2 framework
- .NET 8.0 Runtime
- Valid pathofexile.com session

### Setup Steps

1. **Download the Plugin**
   ```
   Place the JewYourItem folder in your ExileCore2 Plugins directory
   ```

2. **Configure Session ID**
   - Log in to pathofexile.com
   - Press F12 to open Developer Tools
   - Go to Application/Storage → Cookies → pathofexile.com
   - Copy the `POESESSID` value (32 characters)
   - Paste it in the plugin settings

3. **Add Trade Searches**
   - Create search groups to organize your searches
   - Add trade search URLs from pathofexile.com/trade2
   - Enable desired searches and groups

4. **Configure Settings**
   - Enable/disable auto-teleport, mouse movement, and auto-buy features
   - Set sound preferences and logging options
   - Configure hotkeys for manual control

## 📋 Configuration

### Core Settings
- **Enable**: Master switch for the entire plugin
- **Travel Hotkey**: Key to manually teleport to hideout
- **Play Sound**: Enable audio alerts for new items
- **Auto TP**: Automatically teleport to found items
- **Move Mouse to Item**: Auto-move mouse to items in purchase window
- **Auto Buy**: Automatically purchase items (use with caution)
- **Show GUI**: Display the plugin interface

### Search Management
- **Search Groups**: Organize searches into categories
- **Trade URLs**: Import searches directly from pathofexile.com URLs
- **Enable/Disable**: Fine-grained control over individual searches
- **League Configuration**: Set league for each search

### Advanced Settings
- **Log Search Results**: Enable CSV logging of all results
- **Debug Mode**: Detailed logging for troubleshooting
- **Max Recent Items**: Control how many items to track
- **Cancel With Right Click**: Cancel operations with right-click

## 🎯 Usage Guide

### Basic Operation

1. **Start Monitoring**
   - Enable the plugin and desired search groups
   - The plugin will automatically start monitoring active searches

2. **Receiving Alerts**
   - Sound alerts play when new items are found
   - Plugin GUI shows active status of all listeners
   - Items are logged to CSV files if enabled

3. **Auto-Teleport Flow**
   - Plugin detects new items matching your searches
   - Automatically teleports to the item location
   - Moves mouse to item when purchase window opens
   - Can auto-purchase if enabled (with safety checks)

4. **Manual Control**
   - Use the travel hotkey to manually teleport to hideout
   - Right-click to cancel operations
   - Monitor the GUI for real-time status

### Best Practices

#### Safety First
- **Start Conservative**: Begin with auto-teleport only, without auto-buy
- **Test Settings**: Verify behavior in safe situations before enabling auto-buy
- **Monitor Logs**: Regularly check CSV logs to ensure proper operation
- **Window Management**: Keep purchase windows closed when not actively trading

#### Performance Optimization
- **Limit Concurrent Searches**: Don't run too many searches simultaneously
- **Use Search Groups**: Organize searches logically for better management
- **Monitor Connection Status**: Watch for authentication errors in the GUI
- **Regular Cleanup**: Remove old/disabled searches periodically

#### Item Management
- **Price Monitoring**: Use CSV logs to analyze item prices over time
- **Search Effectiveness**: Track which searches are most productive
- **Auto-buy Tracking**: Monitor auto-buy success rates in logs

## 📁 File Structure

```
JewYourItem/
├── JewYourItem.cs              # Main plugin logic
├── JewYourItem.Gui.cs          # User interface
├── JewYourItem.SearchListener.cs # Trade API monitoring
├── JewYourItem.Teleport.cs     # Teleportation logic
├── JewYourItemSettings.cs      # Configuration classes
├── RecentItem.cs               # Item tracking
├── RateLimiter.cs              # API rate limiting
├── SecureSessionManager.cs     # Session ID management
├── EncryptedSettings.cs        # Secure storage
├── Models/
│   └── TradeModels.cs          # Trade API models
├── sound/
│   ├── pulse.wav               # Standard alert sound
│   └── pulserare.wav           # Rare item alert sound
├── obj/                        # Build artifacts
├── bin/                        # Compiled binaries
└── Plugins/Temp/JewYourItem/   # Log files location
    └── search_results.csv      # Search results log
```

## 🔧 Troubleshooting

### Common Issues

#### Authentication Problems
- **Invalid Session ID**: Verify POESESSID is correct and current
- **Session Expired**: Re-login to pathofexile.com and get fresh session ID
- **Rate Limited**: Wait a few minutes if you're being rate limited

#### Connection Issues
- **Search Not Starting**: Check league name and search ID are correct
- **Connection Failures**: Verify internet connectivity and API availability
- **Global Throttling**: Plugin automatically handles rate limiting

#### Auto-Buy Problems
- **Not Purchasing**: Ensure purchase window is properly detected
- **Wrong Items**: Check that mouse movement is enabled and working
- **Safety Blocks**: Plugin prevents purchases when windows aren't properly tracked

#### Performance Issues
- **High CPU Usage**: Reduce number of concurrent searches
- **Memory Usage**: Clear old searches and restart if needed
- **Slow Response**: Check for network issues or API problems

### Debug Mode
Enable debug mode in settings for detailed logging:
- Connection attempts and failures
- Mouse movement coordinates
- Purchase window detection
- Teleportation events

## 📊 CSV Log Format

The plugin logs all search results to CSV format:

```csv
Timestamp,Item Name,Price,Search Name,Search ID,League,Purchase Status,Auto Buy Enabled
2024-01-15 14:30:25,"Tabula Rasa Simple Robe","1 exalted","Rare Armor","abc123","Standard","AUTO-BUY ATTEMPTED","YES"
```

### Log Fields
- **Timestamp**: When the item was found
- **Item Name**: Full item name with modifiers
- **Price**: Item price and currency
- **Search Name**: Name of the search that found it
- **Search ID**: Unique search identifier
- **League**: PoE league name
- **Purchase Status**: MANUAL BUY / AUTO-BUY ATTEMPTED
- **Auto Buy Enabled**: Whether auto-buy was enabled when found

## 🤝 Contributing

### Development Setup
1. Clone the repository
2. Open in Visual Studio 2022 or VS Code
3. Build with .NET 8.0
4. Reference ExileCore2.dll and GameOffsets2.dll

### Code Guidelines
- Follow C# coding standards
- Use meaningful variable names
- Add XML documentation comments
- Handle exceptions appropriately
- Keep methods focused and single-purpose

### Testing
- Test in Standard league first
- Verify all safety features work
- Test with various item types
- Monitor performance impact

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## ⚠️ Disclaimer

This plugin is for educational and personal use only. Automated trading may violate Path of Exile's Terms of Service. Use at your own risk. The authors are not responsible for any consequences of using this software.

## 🙏 Acknowledgments

- Built for the ExileCore2 framework
- Uses ImGui.NET for the user interface
- Audio playback powered by NAudio
- Trade API integration with pathofexile.com

---

**Version**: 1.0.0
**Last Updated**: September 2025
**Compatibility**: Path of Exile 2 + ExileCore2
