namespace DynamicPill;

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DynamicPill.Models;
using DynamicPill.Services;
using DynamicPill.Views;
using WinForms = System.Windows.Forms;

public partial class App : Application
{
    private const string MutexName = "DynamicPill_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;

    private WinForms.NotifyIcon? _trayIcon;
    private PillWindow? _pillWindow;
    private PillPresenter? _presenter;
    private NotificationListenerService? _listener;
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _ipcTokenSource;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "app_debug.log");

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            File.AppendAllText(LogFilePath, line + "\r\n");
            try { File.AppendAllText("app_debug.log", line + "\r\n"); } catch { }
            Debug.WriteLine(line);
        }
        catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"[CRASH] Unhandled: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            Log($"[CRASH] Dispatcher: {args.Exception}");
        };

        Log("App starting up...");

        // 1. Single-instance check
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
            if (!isNewInstance)
            {
                try
                {
                    if (_singleInstanceMutex.WaitOne(TimeSpan.Zero, true))
                    {
                        isNewInstance = true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    isNewInstance = true;
                }
            }

            if (!isNewInstance)
            {
                Log("Another instance is running. Exiting.");
                // Send trigger to existing instance via pipe if possible
                try
                {
                    using var client = new NamedPipeClientStream(".", "DynamicPill_Pipe", PipeDirection.Out);
                    client.Connect(400);
                    using var writer = new StreamWriter(client, Encoding.UTF8);
                    writer.WriteLine("DynamicPill|Another instance was launched. Pill is active here!|DynamicPill");
                    writer.Flush();
                }
                catch { }

                Environment.Exit(0);
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            Log("Mutex was abandoned by previous instance. Acquired ownership.");
        }
        catch (Exception ex)
        {
            Log($"[Mutex] Warning: {ex.Message}");
        }

        Log("Single instance check passed.");

        // 2. Initialize presenter and host window
        _presenter = new PillPresenter();
        _presenter.Start();
        Log("PillPresenter started.");

        _pillWindow = new PillWindow();
        _pillWindow.SetPresenter(_presenter);
        _pillWindow.Show();
        Log("PillWindow shown.");

        // 3. Setup System Tray NotifyIcon
        SetupTrayIcon();
        Log("SetupTrayIcon completed.");

        // 4. Start Named Pipe IPC Server for CLI / external scripts
        StartIpcServer();

        // 5. Initialize notification listener
        _listener = new NotificationListenerService();
        _listener.NotificationReceived += item =>
        {
            Log($"Notification received from listener: {item.Title}");
            _presenter.Enqueue(item);
        };

        bool started = await _listener.StartAsync();
        Log($"NotificationListenerService started: {started}");

        // Welcome demo notification
        _presenter.Enqueue(new PillItem
        {
            Id = 1,
            AppId = "DynamicPill.Welcome",
            AppName = "DynamicPill",
            Title = "DynamicPill Active",
            Body = "Pill overlay initialized. Click or right-click tray icon.",
            Timestamp = DateTimeOffset.Now
        });
    }

    private static Icon CreatePillTrayIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw pill capsule
                var rect = new Rectangle(2, 9, 28, 14);
                using var path = new GraphicsPath();
                int diameter = 14;
                path.AddArc(rect.X, rect.Y, diameter, diameter, 90, 180);
                path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 180);
                path.CloseFigure();

                using var brush = new SolidBrush(Color.FromArgb(20, 20, 26));
                using var pen = new Pen(Color.FromArgb(56, 189, 248), 2.5f); // Sky blue capsule border
                using var dotBrush = new SolidBrush(Color.FromArgb(52, 211, 153)); // Glowing emerald dot

                g.FillPath(brush, path);
                g.DrawPath(pen, path);

                // Indicator dot on left side
                g.FillEllipse(dotBrush, 8, 13, 6, 6);
            }

            IntPtr hIcon = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return icon;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void StartIpcServer()
    {
        _ipcTokenSource = new CancellationTokenSource();
        var token = _ipcTokenSource.Token;

        Task.Run(async () =>
        {
            Log("Named pipe IPC server started (DynamicPill_Pipe).");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        "DynamicPill_Pipe",
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Log($"IPC received line: {line}");

                        if (line.StartsWith("CMD:SNAPSHOT|"))
                        {
                            var savePath = line.Substring("CMD:SNAPSHOT|".Length);
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    if (_pillWindow != null)
                                    {
                                        int w = (int)Math.Max(1, _pillWindow.ActualWidth > 0 ? _pillWindow.ActualWidth : _pillWindow.Width);
                                        int h = (int)Math.Max(1, _pillWindow.ActualHeight > 0 ? _pillWindow.ActualHeight : _pillWindow.Height);
                                        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                        rtb.Render(_pillWindow);
                                        var encoder = new PngBitmapEncoder();
                                        encoder.Frames.Add(BitmapFrame.Create(rtb));
                                        using var fs = File.Create(savePath);
                                        encoder.Save(fs);
                                        Log($"[Snapshot] Saved visual snapshot to {savePath} ({w}x{h})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"[Snapshot] Error saving snapshot: {ex.Message}");
                                }
                            });
                            continue;
                        }

                        if (line == "CMD:OPEN_SETTINGS")
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_settingsWindow == null || !_settingsWindow.IsLoaded)
                                {
                                    _settingsWindow = new SettingsWindow(_pillWindow!, _presenter!);
                                    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                                    _settingsWindow.Show();
                                    Log("[IPC] Settings window opened.");
                                }
                                else
                                {
                                    _settingsWindow.Activate();
                                }
                            });
                            continue;
                        }

                        if (line.StartsWith("CMD:SNAPSHOT_SETTINGS|"))
                        {
                            var savePath = line.Substring("CMD:SNAPSHOT_SETTINGS|".Length);
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    if (_settingsWindow != null && _settingsWindow.IsLoaded)
                                    {
                                        int w = (int)Math.Max(1, _settingsWindow.ActualWidth > 0 ? _settingsWindow.ActualWidth : _settingsWindow.Width);
                                        int h = (int)Math.Max(1, _settingsWindow.ActualHeight > 0 ? _settingsWindow.ActualHeight : _settingsWindow.Height);
                                        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                        rtb.Render(_settingsWindow);
                                        var encoder = new PngBitmapEncoder();
                                        encoder.Frames.Add(BitmapFrame.Create(rtb));
                                        using var fs = File.Create(savePath);
                                        encoder.Save(fs);
                                        Log($"[Snapshot] Saved settings visual snapshot to {savePath} ({w}x{h})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"[Snapshot] Error saving settings snapshot: {ex.Message}");
                                }
                            });
                            continue;
                        }

                        if (line == "CMD:CLOSE_SETTINGS")
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _settingsWindow?.Close();
                                _settingsWindow = null;
                                Log("[IPC] Settings window closed.");
                            });
                            continue;
                        }

                        if (line == "CMD:TRIGGER_HOVER")
                        {
                            Dispatcher.Invoke(() => _pillWindow?.TriggerHover(true));
                            Log("[IPC] Triggered hover expansion.");
                            continue;
                        }

                        if (line == "CMD:TRIGGER_UNHOVER")
                        {
                            Dispatcher.Invoke(() => _pillWindow?.TriggerHover(false));
                            Log("[IPC] Triggered unhover collapse.");
                            continue;
                        }

                        var parts = line.Split('|');
                        string title = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "DynamicPill";
                        string body = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "Notification";
                        string appName = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "System";

                        Dispatcher.Invoke(() =>
                        {
                            _presenter?.Enqueue(new PillItem
                            {
                                Id = (uint)Random.Shared.Next(),
                                AppId = $"DynamicPill.{appName}",
                                AppName = appName,
                                Title = title,
                                Body = body,
                                Timestamp = DateTimeOffset.Now
                            });
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"IPC Pipe error: {ex.Message}");
                    await Task.Delay(500, token);
                }
            }
        }, token);
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "DynamicPill - Notification Overlay",
            Icon = CreatePillTrayIcon(),
            Visible = true
        };

        // Visually spotlight the tray icon on launch
        try
        {
            _trayIcon.ShowBalloonTip(3000, "DynamicPill Active", "DynamicPill is running in your system tray! Click to test.", WinForms.ToolTipIcon.Info);
        }
        catch { }

        // Left-click directly triggers a test notification
        _trayIcon.MouseClick += (sender, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                _presenter?.Enqueue(new PillItem
                {
                    Id = (uint)Random.Shared.Next(),
                    AppId = "DynamicPill.Tray",
                    AppName = "DynamicPill",
                    Title = "DynamicPill Active",
                    Body = "Click received! Right-click icon for full menu & settings.",
                    Timestamp = DateTimeOffset.Now
                });
            }
        };

        var contextMenu = new WinForms.ContextMenuStrip();

        var testCompact = new WinForms.ToolStripMenuItem("🔔 Test Notification (Compact)", null, (_, _) =>
        {
            _presenter?.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Slack.Desktop",
                AppName = "Maya Chen",
                Title = "Maya Chen",
                Body = "Are we still on for the 3 pm review?",
                Timestamp = DateTimeOffset.Now
            });
        });

        var testSwap = new WinForms.ToolStripMenuItem("🔄 Test Swap Animation", null, (_, _) =>
        {
            _presenter?.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Slack.Desktop",
                AppName = "Maya Chen",
                Title = "Maya Chen",
                Body = "Are we still on for the 3 pm review?",
                Timestamp = DateTimeOffset.Now
            });

            _presenter?.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "VSCode.Build",
                AppName = "Build System",
                Title = "Build finished",
                Body = "Release 2.4 passed all checks",
                Timestamp = DateTimeOffset.Now
            });
        });

        var testBurst = new WinForms.ToolStripMenuItem("📬 Test Merged Burst", null, (_, _) =>
        {
            _presenter?.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Outlook.Mail",
                AppName = "Mail",
                Title = "Mail · 4 new messages",
                Body = "Latest: Invoice 1042 is ready",
                Timestamp = DateTimeOffset.Now,
                MergeCount = 4
            });
        });

        var pauseItem = new WinForms.ToolStripMenuItem("⏸ Pause Listening", null, (sender, eventArgs) =>
        {
            if (sender is WinForms.ToolStripMenuItem mi)
            {
                if (mi.Text?.Contains("Pause") == true)
                {
                    _listener?.Stop();
                    mi.Text = "▶ Resume Listening";
                }
                else
                {
                    if (_listener != null)
                    {
                        Task.Run(async () => await _listener.StartAsync());
                    }
                    mi.Text = "⏸ Pause Listening";
                }
            }
        });

        var settingsItem = new WinForms.ToolStripMenuItem("⚙ Settings...", null, (_, _) =>
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(_pillWindow!, _presenter!);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        });

        var winSettingsItem = new WinForms.ToolStripMenuItem("🔧 Windows Notification Settings", null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:notifications",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Open win settings error: {ex.Message}");
            }
        });

        var quitItem = new WinForms.ToolStripMenuItem("❌ Exit DynamicPill", null, (_, _) =>
        {
            Shutdown();
        });

        contextMenu.Items.Add(testCompact);
        contextMenu.Items.Add(testSwap);
        contextMenu.Items.Add(testBurst);
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add(pauseItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(winSettingsItem);
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add(quitItem);

        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (_, _) =>
        {
            _presenter?.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Slack.Desktop",
                AppName = "Maya Chen",
                Title = "Maya Chen",
                Body = "Are we still on for the 3 pm review?",
                Timestamp = DateTimeOffset.Now
            });
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"OnExit called! ExitCode: {e.ApplicationExitCode}");
        _ipcTokenSource?.Cancel();
        _ipcTokenSource?.Dispose();
        _listener?.Dispose();
        _presenter?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
        Log("OnExit finished.");
    }
}
