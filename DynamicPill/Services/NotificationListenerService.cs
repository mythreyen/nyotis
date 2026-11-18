namespace DynamicPill.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// using the WinRT UserNotificationListener API.
/// </summary>
public sealed class NotificationListenerService : IDisposable
{
    private UserNotificationListener? _listener;
    private bool _isListening;
    private readonly HashSet<uint> _seenNotificationIds = new();
    private Timer? _pollTimer;

    public event Action<PillItem>? NotificationReceived;

    public async Task<bool> StartAsync()
    {
        try
        {
            // Windows UserNotificationListener requires package identity (MSIX).
            // Calling it from unpackaged Win32 causes RPC/COM exceptions or access violations.
            if (!HasPackageIdentity())
            {
                Debug.WriteLine("[NotificationListener] Running in unpackaged desktop mode. Using IPC and tray hooks for notifications.");
                return false;
            }

            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                Debug.WriteLine("[NotificationListener] UserNotificationListener is not supported on this Windows build.");
                return false;
            }

            _listener = UserNotificationListener.Current;
            var accessStatus = await _listener.RequestAccessAsync();

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                Debug.WriteLine($"[NotificationListener] Access status: {accessStatus}");
                return false;
            }

            _isListening = true;

            // Optional: Populate current notification IDs so we only alert on NEW incoming toasts
            try
            {
                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var n in existing)
                {
                    _seenNotificationIds.Add(n.Id);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationListener] Initial cache load error: {ex.Message}");
            }

            // Start polling timer every 500ms
            _pollTimer = new Timer(
                callback: _ => PollNotifications(),
                state: null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromMilliseconds(500));

            Debug.WriteLine("[NotificationListener] Successfully started listening.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationListener] StartAsync exception: {ex}");
            return false;
        }
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current?.Id != null;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        _isListening = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        Debug.WriteLine("[NotificationListener] Stopped.");
    }

    private async void PollNotifications()
    {
        if (!_isListening || _listener == null)
            return;

        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            foreach (var notification in notifications)
            {
                if (_seenNotificationIds.Contains(notification.Id))
                    continue;

                _seenNotificationIds.Add(notification.Id);

                var pillItem = ExtractPillItem(notification);
                if (pillItem != null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        NotificationReceived?.Invoke(pillItem);
                    });
                }
            }

            // Clean up old cached IDs
            var currentIds = new HashSet<uint>(notifications.Select(n => n.Id));
            _seenNotificationIds.IntersectWith(currentIds);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationListener] Poll error: {ex.Message}");
        }
    }

    private static PillItem? ExtractPillItem(UserNotification notification)
    {
        try
        {
            var toast = notification.Notification;
            var visual = toast.Visual;
            var binding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);

            if (binding == null)
                return null;

            var texts = binding.GetTextElements().ToList();
            string title = texts.Count > 0 ? (texts[0].Text ?? string.Empty) : string.Empty;
            string body = texts.Count > 1 ? (texts[1].Text ?? string.Empty) : string.Empty;

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
                    var stream = logoRef.OpenReadAsync().GetAwaiter().GetResult();
                    if (stream != null)
                    {
                        using var memStream = new MemoryStream();
                        stream.AsStreamForRead().CopyTo(memStream);
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
            catch
            {
                // Fallback glyph/color handled by PillItem
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
            Debug.WriteLine($"[NotificationListener] ExtractPillItem error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
