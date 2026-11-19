# nyotis - DynamicPill

A modern, fluid Dynamic Island / Pill notification overlay for Windows built with .NET 8, WPF, and Win32 Interop.

## Features

- **Fluid Pill Overlay**: Anchored at the top-center of the active display with smooth entrance, swap, expand, and retract animations.
- **Acrylic Backdrop**: Native Windows DWM acrylic / blurbehind styling with rounded pill aesthetics.
- **Modern System Tray Controls**:
  - Custom rendered high-contrast pill icon.
  - Left-click instant test trigger.
  - Context menu with Compact, Swap, and Merged Burst animations.
  - Quick access to Windows Notification Settings and custom pill configuration.
- **Named Pipe IPC Server**: Accepts external notifications directly from CLI scripts, automation tools, or third-party apps via `\\.\pipe\DynamicPill_Pipe`.
- **PowerShell Notification Dispatcher**: Parameterized script (`test-notification.ps1`) to send notifications directly to the overlay with fallback to Windows Action Center toasts.
- **Full-Screen & Quiet Hours Suppression**: Automatically silences overlay during presentations, full-screen gaming/video, and Windows Focus Assist.

---

## Getting Started

### Prerequisites
- Windows 10 (version 19041+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher

### Build & Run

```powershell
# Restore & build solution
dotnet build DynamicPill/DynamicPill.csproj

# Run application
dotnet run --project DynamicPill/DynamicPill.csproj
```

Alternatively, launch the executable directly via Windows Explorer:
```powershell
Start-Process explorer.exe ".\DynamicPill\bin\Debug\net8.0-windows10.0.19041.0\DynamicPill.exe"
```

---

## Testing Notifications

Send a test notification directly to the overlay using the included PowerShell script:

```powershell
.\test-notification.ps1 -Title "Slack" -Message "Maya: Reviewing the draft now 👍" -AppName "Slack"
```

Or trigger built-in demo animations from the System Tray icon:
1. Click the **`^` (Show hidden icons)** chevron in the Windows taskbar.
2. Right-click the **DynamicPill** icon.
3. Select **Test Notification (Compact)**, **Test Swap Animation**, or **Test Merged Burst**.

---

## Repository Structure

```
├── DynamicPill/
│   ├── Interop/         # Win32 DWM, monitor DPI, and window style interop
│   ├── Models/          # PillItem and PillState models
│   ├── Resources/       # Themes (Dark/Light) and Storyboard animations
│   ├── Services/        # Presenter, AppLauncher, and NotificationListenerService
│   ├── Views/           # PillWindow and SettingsWindow WPF views
│   └── DynamicPill.csproj
├── FrontEndHTMLView/    # Anatomy, motion, and visual design prototypes
├── test-notification.ps1 # IPC and Windows Toast notification test dispatcher
└── README.md
```
