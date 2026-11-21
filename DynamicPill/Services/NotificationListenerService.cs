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
/// Ultra-low latency service listening for incoming Windows Action Center / Toast notifications
/// using high-frequency polling (60ms) and in-memory icon caching for near-instantaneous response.
/// </summary>
public sealed class NotificationListenerService : IDisposable
{
    private const int FastPollIntervalMs = 60;
    private const int InitialPollDelayMs = 50;

    private UserNotificationListener? _listener;
    private bool _isListening;
    private readonly HashSet<uint> _seenNotificationIds = new();
    private readonly object _lock = new();
    private Timer? _pollTimer;
    private int _isPolling;

    // In-memory icon cache by AppId for 0ms lookup latency
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

            // Populate existing notification IDs and warm icon cache
            try
            {
                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var cutoff = DateTimeOffset.Now.AddMinutes(-15);
                lock (_lock)
                {
                    foreach (var n in existing)
                    {
                        if (n.CreationTime < cutoff)
                        {
                            _seenNotificationIds.Add(n.Id);
                        }

                        // Pre-warm the icon cache in background for instant lookups later
                        if (n.AppInfo?.AppUserModelId != null && !_iconCache.ContainsKey(n.AppInfo.AppUserModelId))
                        {
                            _ = PrewarmIconAsync(n);
                        }
                    }
                }
                App.Log($"[NotificationListener] Initialized cache with {existing.Count} total existing toasts.");
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] Initial cache load error: {ex.Message}");
            }

            // Start high-frequency polling timer (60ms interval, ~16fps query)
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
            string appId = notification.AppInfo?.AppUserModelId ?? string.Empty;
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
        lock (_lock)
        {
            if (_seenNotificationIds.Contains(notification.Id))
                return;

            _seenNotificationIds.Add(notification.Id);
        }

        var pillItem = await ExtractPillItemAsync(notification);
        if (pillItem != null)
        {
            App.Log($"[NotificationListener] Forwarding toast: [{pillItem.AppName}] '{pillItem.Title}' - '{pillItem.Body}'");
            // Invoke directly on threadpool without Dispatcher queue hopping to minimize dispatch latency
            NotificationReceived?.Invoke(pillItem);
        }
    }

    private async void PollNotifications()
    {
        if (!_isListening || _listener == null)
            return;

        // Prevent overlapping polls if a system call stalls
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
                _seenNotificationIds.IntersectWith(currentIds);
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

    private static async Task<PillItem?> ExtractPillItemAsync(UserNotification notification)
    {
        try
        {
            var toast = notification.Notification;
            var visual = toast.Visual;
            var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric);

            if (binding == null)
                return null;

            var texts = binding.GetTextElements().ToList();
            string title = texts.Count > 0 ? (texts[0].Text ?? string.Empty) : string.Empty;
            string body = texts.Count > 1
                ? string.Join(" · ", texts.Skip(1).Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)))
                : string.Empty;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                return null;

            string appId = notification.AppInfo?.AppUserModelId ?? "Unknown";
            string appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? appId;

            // Fast-path: Check in-memory icon cache for 0ms retrieval
            if (!_iconCache.TryGetValue(appId, out ImageSource? icon))
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
                            bitmap.DecodePixelWidth = 72; // 2x for high DPI
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

            return new PillItem
            {
                Id = notification.Id,
                AppId = appId,
                AppName = appName,
                AppIcon = icon,
                Title = title,
                Body = body,
                Timestamp = notification.CreationTime
            };
        }
        catch (Exception ex)
        {
            App.Log($"[NotificationListener] ExtractPillItem error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
