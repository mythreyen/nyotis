namespace DynamicPill.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DynamicPill.Models;

public sealed class PillPresenter : IDisposable
{
    private const int MaxQueueSize = 20;
    private const int BurstThreshold = 5;
    private const double ShortenedDisplaySeconds = 2.5;
    private const int MinIntervalMs = 600;
    private const int DedupeWindowMs = 2000;
    private const int BurstMergeThreshold = 3;

    private readonly Channel<PillItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;

    private PillItem? _currentItem;
    private DispatcherTimer? _dismissTimer;
    private DateTime _lastShowTime = DateTime.MinValue;
    private bool _isHovered;

    private readonly object _recentItemsLock = new();
    private readonly List<(PillItem Item, DateTime Time)> _recentItems = new();

    public double NormalDisplaySeconds { get; set; } = 4.5;

    public event Action<PillItem>? ShowRequested;
    public event Action? DismissRequested;
    public event Action<PillItem>? SwapRequested;

    public PillItem? CurrentItem => _currentItem;

    public PillPresenter()
    {
        _channel = Channel.CreateBounded<PillItem>(new BoundedChannelOptions(MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        Task.Run(() => ConsumeLoopAsync(_cts.Token));
    }

    public void Enqueue(PillItem item)
    {
        var now = DateTime.UtcNow;
        lock (_recentItemsLock)
        {
            CleanupRecent(now);

            // Deduplication
            bool isDuplicate = _recentItems.Any(r =>
                r.Item.AppId == item.AppId &&
                r.Item.Title == item.Title &&
                r.Item.Body == item.Body &&
                (now - r.Time).TotalMilliseconds < DedupeWindowMs);

            if (isDuplicate)
            {
                Debug.WriteLine($"[PillPresenter] Deduplicated toast: {item.Title}");
                return;
            }

            _recentItems.Add((item, now));

            // Burst merge check for same app
            int sameAppCount = _recentItems.Count(r => r.Item.AppId == item.AppId);
            if (sameAppCount >= BurstMergeThreshold)
            {
                item.MergeCount = sameAppCount;
                item.Title = $"{item.AppName} · {sameAppCount} new messages";
                item.Body = $"Latest: {item.Body}";
            }
        }

        _channel.Writer.TryWrite(item);
    }

    public void NotifyHovered()
    {
        _isHovered = true;
        _dismissTimer?.Stop();
    }

    public void NotifyUnhovered()
    {
        _isHovered = false;
        if (_currentItem != null)
        {
            // Resume with 2.0s remaining
            StartDismissTimer(TimeSpan.FromSeconds(2.0));
        }
    }

    public void DismissImmediate()
    {
        _dismissTimer?.Stop();
        _currentItem = null;
        _dispatcher.Invoke(() => DismissRequested?.Invoke());
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Rate limit
                var elapsed = DateTime.UtcNow - _lastShowTime;
                if (elapsed.TotalMilliseconds < MinIntervalMs)
                {
                    await Task.Delay(MinIntervalMs - (int)elapsed.TotalMilliseconds, ct);
                }

                if (_currentItem != null)
                {
                    // Item currently showing -> trigger Swap transition
                    _dispatcher.Invoke(() => _dismissTimer?.Stop());
                    await Task.Delay(140, ct); // Wait collapse duration

                    _currentItem = item;
                    _lastShowTime = DateTime.UtcNow;

                    _dispatcher.Invoke(() =>
                    {
                        SwapRequested?.Invoke(item);
                        StartDismissTimer(GetDisplayDuration());
                    });
                }
                else
                {
                    // Full entrance
                    _currentItem = item;
                    _lastShowTime = DateTime.UtcNow;

                    _dispatcher.Invoke(() =>
                    {
                        ShowRequested?.Invoke(item);
                        StartDismissTimer(GetDisplayDuration());
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PillPresenter] ConsumeLoop error: {ex.Message}");
            }
        }
    }

    private void StartDismissTimer(TimeSpan duration)
    {
        if (_dismissTimer == null)
        {
            _dismissTimer = new DispatcherTimer();
            _dismissTimer.Tick += OnDismissTimerTick;
        }
        _dismissTimer.Stop();
        _dismissTimer.Interval = duration;
        _dismissTimer.Start();
    }

    private void OnDismissTimerTick(object? sender, EventArgs e)
    {
        _dismissTimer?.Stop();
        if (_isHovered) return;

        _currentItem = null;
        DismissRequested?.Invoke();
    }

    private TimeSpan GetDisplayDuration()
    {
        int queueCount = _channel.Reader.Count;
        double seconds = queueCount >= BurstThreshold ? ShortenedDisplaySeconds : NormalDisplaySeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private void CleanupRecent(DateTime now)
    {
        _recentItems.RemoveAll(r => (now - r.Time).TotalMilliseconds > DedupeWindowMs * 2);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _dismissTimer?.Stop();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
