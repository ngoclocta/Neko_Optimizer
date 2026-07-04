# 🚀 Neko Cpu Optimizer - Installation Guide

## Quick Start

### Option 1: Automatic .NET Installation (Recommended)
Double-click **`launcher.vbs`** or **`launcher.bat`** to automatically:
1. Check if .NET 8.0 is installed
2. Download & install .NET 8.0 if needed
3. Launch Neko Cpu Optimizer

### Option 2: Direct Launch
If .NET 8.0 is already installed:
```
c:\Users\ngocloc\Desktop\bacon-tool\bin\Release\net8.0-windows\optimizer.exe
```

## Requirements

- **Windows 10/11** (64-bit)
- **Administrator privileges**
- **.NET 8.0 Runtime** (automatically installed by launcher if missing)

## Features

✅ **Auto-detect Missing Runtime** - Launcher detects and installs .NET 8.0 automatically
✅ **Multi-app Foreground Tracking** - Optimizes up to 3 most recent active apps
✅ **45/60 Minute Timeouts** - Tier 2: 45 min, Tier 3: 60 min with RAM-aware throttling
✅ **System Tray Integration** - Minimize to tray with real-time CPU load display
✅ **Keyboard Shortcuts:**
  - `SPACE` - Pause/Resume optimization
  - `H` - Hide to tray
  - `ESC` - Exit

## Files

- `launcher.bat` - Batch script launcher (auto-install .NET + run app)
- `launcher.vbs` - VBS wrapper (cleaner experience)
- `optimizer.exe` - Main application (in bin/Release/net8.0-windows/)
- `whitelist.txt` - App-specific optimization configuration

## Manual .NET 8.0 Installation

If auto-installation fails, install manually:
```powershell
winget install --id Microsoft.DotNet.SDK.8
```
Or visit: https://dotnet.microsoft.com/download/dotnet/8.0

## Version
**Neko Cpu Optimizer v1.0**
