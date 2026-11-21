namespace DynamicPill.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicPill.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

/// <summary>
/// Service listening for incoming Windows Action Center / Toast notifications
/// across all applications with zero drops, robust fallback extraction,
/// and support for in-place updated toasts.
/// </summary>
public sealed class NotificationListenerService : IDisposable
{
    private const int FastPollIntervalMs = 60;
    private const int InitialPollDelayMs = 50;

    private UserNotificationListener? _listener;
    private bool _isListening;

    // Track notification ID -> content signature so that:
    // 1. Identical toasts are not duplicated.
    // 2. In-place updated toasts with the same ID (e.g. messaging apps, download progress) are caught immediately.
    private readonly Dictionary<uint, string> _seenNotifications = new();
    private readonly object _lock = new();
    private Timer? _pollTimer;
    private int _isPolling;

    // In-memory icon cache by AppId for 0ms retrieval latency
    private static readonly ConcurrentDictionary<string, ImageSource> _iconCache = new();

    public event Action<PillItem>? NotificationReceived;

    public async Task<bool> StartAsync()
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                App.Log("[NotificationListener] UserNotificationListener is not supported on this Windows build.");
                return false;
            }

            _listener = UserNotificationListener.Current;
            var accessStatus = await _listener.RequestAccessAsync();

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                App.Log($"[NotificationListener] Access status: {accessStatus}");
                return false;
            }

            _isListening = true;

            // Populate existing notification IDs so we don't flood on initial startup,
            // but keep anything received within the last 5 minutes.
            try
            {
                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var cutoff = DateTimeOffset.Now.AddMinutes(-5);
                lock (_lock)
                {
                    foreach (var n in existing)
                    {
                        DateTimeOffset time = DateTimeOffset.MinValue;
                        try { time = n.CreationTime; } catch { }

                        if (time < cutoff)
                        {
                            _seenNotifications[n.Id] = string.Empty;
                        }

                        // Pre-warm the icon cache in background
                        try
                        {
                            if (n.AppInfo?.AppUserModelId != null && !_iconCache.ContainsKey(n.AppInfo.AppUserModelId))
                            {
                                _ = PrewarmIconAsync(n);
                            }
                        }
                        catch { }
                    }
                }
                App.Log($"[NotificationListener] Initialized cache with {existing.Count} total existing toasts.");
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] Initial cache load error: {ex.Message}");
            }

            // Start high-frequency polling timer (60ms interval, ~16 queries/sec)
            _pollTimer = new Timer(
                callback: _ => PollNotifications(),
                state: null,
                dueTime: TimeSpan.FromMilliseconds(InitialPollDelayMs),
                period: TimeSpan.FromMilliseconds(FastPollIntervalMs));

            App.Log($"[NotificationListener] High-performance polling started ({FastPollIntervalMs}ms interval).");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[NotificationListener] StartAsync exception: {ex}");
            return false;
        }
    }

    public void Stop()
    {
        _isListening = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        App.Log("[NotificationListener] Stopped.");
    }

    private static async Task PrewarmIconAsync(UserNotification notification)
    {
        try
        {
            string appId = string.Empty;
            try { appId = notification.AppInfo?.AppUserModelId ?? string.Empty; } catch { }
            if (string.IsNullOrEmpty(appId) || _iconCache.ContainsKey(appId)) return;

            var logoRef = notification.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(36, 36));
            if (logoRef != null)
            {
                using var cts = new CancellationTokenSource(1000);
                var stream = await logoRef.OpenReadAsync().AsTask(cts.Token);
                if (stream != null)
                {
                    using var memStream = new MemoryStream();
                    await stream.AsStreamForRead().CopyToAsync(memStream, cts.Token);
                    memStream.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = memStream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 72;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _iconCache[appId] = bitmap;
                }
            }
        }
        catch { }
    }

    private async Task ProcessNotificationAsync(UserNotification notification)
    {
        var pillItem = await ExtractPillItemAsync(notification);

        string signature = $"{pillItem.Title}|{pillItem.Body}";
        lock (_lock)
        {
            if (_seenNotifications.TryGetValue(notification.Id, out string? prevSig) && prevSig == signature)
            {
                // Exact same content already processed
                return;
            }

            _seenNotifications[notification.Id] = signature;
        }

        App.Log($"[NotificationListener] Forwarding toast: [{pillItem.AppName}] '{pillItem.Title}' - '{pillItem.Body}' (ID: {pillItem.Id})");
        NotificationReceived?.Invoke(pillItem);
    }

    private async void PollNotifications()
    {
        if (!_isListening || _listener == null)
            return;

        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0)
            return;

        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            foreach (var notification in notifications)
            {
                await ProcessNotificationAsync(notification);
            }

            // Clean up dismissed IDs
            lock (_lock)
            {
                var currentIds = new HashSet<uint>(notifications.Select(n => n.Id));
                var toRemove = _seenNotifications.Keys.Where(k => !currentIds.Contains(k)).ToList();
                foreach (var id in toRemove)
                {
                    _seenNotifications.Remove(id);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[NotificationListener] Poll error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private static async Task<PillItem> ExtractPillItemAsync(UserNotification notification)
    {
        string appId = "Unknown";
        string appName = "Notification";
        ImageSource? icon = null;

        // 1. Safely extract AppId and AppName (handles NotImplementedException on unpackaged apps)
        try
        {
            if (notification.AppInfo != null)
            {
                appId = notification.AppInfo.AppUserModelId ?? "Unknown";
                appName = notification.AppInfo.DisplayInfo?.DisplayName ?? appId;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[NotificationListener] AppInfo fallback for {notification.Id}: {ex.Message}");
        }

        // 2. Safely extract Visual Binding and Text Elements
        var textList = new List<string>();
        try
        {
            var visual = notification.Notification?.Visual;
            var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric)
                          ?? visual?.Bindings?.FirstOrDefault();

            if (binding != null)
            {
                foreach (var t in binding.GetTextElements())
                {
                    if (!string.IsNullOrWhiteSpace(t?.Text))
                    {
                        textList.Add(t.Text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[NotificationListener] Visual binding read note: {ex.Message}");
        }

        // 3. Guarantee non-empty title and body so NO notification is ever dropped
        string title;
        string body;

        if (textList.Count > 0)
        {
            title = textList[0];
            body = textList.Count > 1
                ? string.Join(" · ", textList.Skip(1))
                : string.Empty;
        }
        else
        {
            title = !string.IsNullOrWhiteSpace(appName) && appName != "Unknown" ? appName : "Notification";
            body = "New notification";
        }

        // 4. Safely extract Timestamp
        DateTimeOffset timestamp = DateTimeOffset.Now;
        try
        {
            timestamp = notification.CreationTime;
        }
        catch
        {
            timestamp = DateTimeOffset.Now;
        }

        // 5. Fast-path icon extraction with in-memory cache
        if (!string.IsNullOrEmpty(appId) && appId != "Unknown")
        {
            if (!_iconCache.TryGetValue(appId, out icon))
            {
                try
                {
                    var logoRef = notification.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(36, 36));
                    if (logoRef != null)
                    {
                        using var cts = new CancellationTokenSource(500);
                        var stream = await logoRef.OpenReadAsync().AsTask(cts.Token);
                        if (stream != null)
                        {
                            using var memStream = new MemoryStream();
                            await stream.AsStreamForRead().CopyToAsync(memStream, cts.Token);
                            memStream.Position = 0;

                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = memStream;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 72;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            icon = bitmap;
                            _iconCache[appId] = bitmap;
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[NotificationListener] Icon extract note for {appName}: {ex.Message}");
                }
            }
        }

        return new PillItem
        {
            Id = notification.Id,
            AppId = appId,
            AppName = appName,
            AppIcon = icon,
            Title = title,
            Body = body,
            Timestamp = timestamp
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
