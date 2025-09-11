# JewYourItem Plugin - Changelog

## Version 2.0.0 - Search Results Logging & Auto-Buy Enhancement

### 🆕 New Features

#### Search Results Logging System
- **CSV Logging**: Added comprehensive search results logging to CSV files
  - Logs all found items with timestamp, name, price, search details, and purchase status
  - Files saved to `Plugins/Temp/JewYourItem/search_results.csv`
  - Configurable via new "Log Search Results" setting in GUI
  - Includes auto-buy status tracking for each item

#### Enhanced Auto-Buy System
- **Auto-Buy Status Tracking**: Items now track which search they originated from
- **Log Integration**: Auto-buy attempts are logged and tracked in CSV files
- **Status Updates**: Purchase status updates from "AUTO-BUY PENDING" to "AUTO-BUY ATTEMPTED"
- **Item Tracking**: Added `SearchId` field to `RecentItem` class for better item identification

#### GUI Improvements
- **Log Management**: Added "Open Log Location" button to access CSV files
- **Streamlined Interface**: Removed complex session ID management UI
- **Simplified Settings**: Cleaner, more focused settings panel
- **Better Organization**: Grouped related settings together

### 🔧 Technical Improvements

#### Code Architecture
- **Method Refactoring**: Moved `PlaySoundWithNAudio` to fire-and-forget pattern
- **Async Optimization**: Improved async handling for better performance
- **Memory Management**: Better cleanup of teleported item information
- **Error Handling**: Enhanced error handling for CSV operations

#### Search Result Processing
- **Enhanced Logging**: Added detailed logging for search result processing
- **Item Identification**: Improved item matching by coordinates and search ID
- **Status Tracking**: Better tracking of auto-buy attempts and results
- **CSV Parsing**: Robust CSV parsing with proper quote and comma handling

#### Mouse Movement System
- **Coordinate Learning**: Improved purchase window coordinate learning
- **Item Finding**: Enhanced `FindRecentItemByCoordinates` method
- **Auto-Buy Integration**: Better integration between mouse movement and auto-buy
- **Status Updates**: Real-time status updates in logs

### 🐛 Bug Fixes

#### Auto-Buy System
- **Status Updates**: Fixed auto-buy status not being properly updated in logs
- **Item Tracking**: Fixed issues with item identification during auto-buy process
- **Coordinate Matching**: Improved coordinate-based item finding
- **Log Synchronization**: Fixed timing issues between auto-buy and logging

#### GUI Issues
- **Settings Persistence**: Fixed settings not being properly saved
- **UI Responsiveness**: Improved GUI responsiveness during operations
- **Error Display**: Better error message display in GUI

#### Search Processing
- **Duplicate Prevention**: Enhanced duplicate search prevention
- **Connection Management**: Improved connection queue management
- **Rate Limiting**: Better rate limiting and throttling

### 🔄 Removed Features

#### Session ID Management
- **Simplified Storage**: Removed complex session ID migration UI
- **Cleaner Interface**: Removed verbose session ID status displays
- **Streamlined Settings**: Simplified settings panel by removing redundant controls

#### Debug Controls
- **Removed Test Buttons**: Removed various test and debug buttons from GUI
- **Cleaner Interface**: Simplified interface by removing development tools
- **Production Ready**: Focused on production features over debug tools

### 📊 Performance Improvements

#### Logging System
- **Efficient CSV Writing**: Optimized CSV file writing with proper locking
- **Memory Usage**: Reduced memory usage for logging operations
- **File I/O**: Improved file I/O performance with better error handling

#### Search Processing
- **Faster Item Matching**: Improved item matching algorithms
- **Reduced Latency**: Better async handling for search operations
- **Memory Optimization**: Better memory management for recent items

### 🛠️ Configuration Changes

#### New Settings
- `LogSearchResults`: Enable/disable search results logging (default: true)
- Enhanced auto-buy tracking and status updates

#### Modified Settings
- Streamlined session ID management
- Simplified GUI layout
- Better organized settings groups

### 📝 File Changes

#### Modified Files
- `JewYourItem.cs`: Added logging system, enhanced auto-buy tracking
- `JewYourItem.Gui.cs`: Streamlined interface, added log management
- `JewYourItem.SearchListener.cs`: Enhanced search result processing
- `JewYourItem.Teleport.cs`: Improved auto-buy integration
- `JewYourItemSettings.cs`: Added logging configuration
- `RecentItem.cs`: Added SearchId field for better tracking

#### New Dependencies
- Enhanced CSV handling for search results logging
- Improved file system operations for log management

### 🎯 User Experience Improvements

#### Logging & Tracking
- **Comprehensive Logs**: All search results and auto-buy attempts are now logged
- **Easy Access**: One-click access to log files via GUI
- **Status Tracking**: Clear visibility into auto-buy process and results
- **Data Export**: CSV format allows easy analysis of search results

#### Interface
- **Cleaner Design**: Simplified, more intuitive interface
- **Better Organization**: Logical grouping of related settings
- **Reduced Clutter**: Removed unnecessary debug and test controls
- **Professional Look**: More polished, production-ready appearance

### 🔒 Security & Stability

#### Error Handling
- **Robust CSV Operations**: Better error handling for file operations
- **Safe File Access**: Proper locking and error recovery for log files
- **Memory Safety**: Improved memory management and cleanup

#### Data Integrity
- **CSV Validation**: Proper CSV field escaping and parsing
- **Data Consistency**: Better tracking of item states and status
- **Error Recovery**: Graceful handling of file system errors

---

## Migration Notes

### For Existing Users
- **Settings**: Existing settings will be preserved
- **Logs**: New logging system will start fresh (no migration of old data)
- **Session ID**: Secure session ID storage continues to work as before
- **Auto-Buy**: Enhanced auto-buy system is backward compatible

### New Features to Explore
1. **Enable Logging**: Check "Log Search Results" in settings
2. **View Logs**: Use "Open Log Location" button to access CSV files
3. **Monitor Auto-Buy**: Watch CSV files for auto-buy status updates
4. **Analyze Results**: Use CSV data to analyze search performance

---

*This update focuses on providing comprehensive logging and tracking capabilities while maintaining the core functionality of the JewYourItem plugin.*
