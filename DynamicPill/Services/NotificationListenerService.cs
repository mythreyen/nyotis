namespace DynamicPill.Services;

using System;
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
/// using the WinRT UserNotificationListener API in unpackaged desktop mode.
/// </summary>
public sealed class NotificationListenerService : IDisposable
{
    private UserNotificationListener? _listener;
    private bool _isListening;
    private readonly HashSet<uint> _seenNotificationIds = new();
    private readonly object _lock = new();
    private Timer? _pollTimer;
    private int _isPolling;

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

            // Populate existing notification IDs so we don't spam ancient historical toasts,
            // but keep anything received within the last 15 minutes so recent toasts (e.g. during testing) come through.
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
                    }
                }
                App.Log($"[NotificationListener] Initialized cache with {existing.Count} total existing toasts (filtered older than {cutoff:HH:mm:ss}).");
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] Initial cache load error: {ex.Message}");
            }

            // Hook live NotificationChanged event if supported
            try
            {
                _listener.NotificationChanged += OnNotificationChanged;
                App.Log("[NotificationListener] Subscribed to live NotificationChanged event.");
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] NotificationChanged event registration note: {ex.Message}");
            }

            // Start polling timer every 500ms as reliable fallback across all Windows editions
            _pollTimer = new Timer(
                callback: _ => PollNotifications(),
                state: null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromMilliseconds(500));

            App.Log("[NotificationListener] Successfully started listening.");
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
        if (_listener != null)
        {
            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
            }
            catch { }
        }
        App.Log("[NotificationListener] Stopped.");
    }

    private async void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (!_isListening) return;

        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            try
            {
                var notification = sender.GetNotification(args.UserNotificationId);
                if (notification != null)
                {
                    await ProcessNotificationAsync(notification);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] NotificationChanged handling error: {ex.Message}");
            }
        }
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
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                NotificationReceived?.Invoke(pillItem);
            });
        }
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

            // Clean up old cached IDs
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

            ImageSource? icon = null;
            try
            {
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
                        bitmap.DecodePixelWidth = 72; // 2x for high DPI
                        bitmap.EndInit();
                        bitmap.Freeze();
                        icon = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[NotificationListener] Icon extract note for {appName}: {ex.Message}");
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
