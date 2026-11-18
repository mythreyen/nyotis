namespace DynamicPill.Views;

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicPill.Interop;
using DynamicPill.Models;
using DynamicPill.Services;

public partial class PillWindow : Window
{
    private NativeWindow _nativeWindow = null!;
    private PillPresenter _presenter = null!;
    private PillState _state = PillState.Idle;

    private Point _mouseDownPos;
    private bool _isDragging;
    private bool _useReducedMotion;

    public PillWindow()
    {
        InitializeComponent();

        _useReducedMotion = !SystemParameters.ClientAreaAnimation;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public void SetPresenter(PillPresenter presenter)
    {
        _presenter = presenter;
        _presenter.ShowRequested += OnShowRequested;
        _presenter.DismissRequested += OnDismissRequested;
        _presenter.SwapRequested += OnSwapRequested;
    }

    public void ApplyTheme(bool isDark)
    {
        var themeUri = new Uri(isDark ? "/Resources/Theme.Dark.xaml" : "/Resources/Theme.Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = themeUri };

        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Resources/Storyboards.xaml", UriKind.Relative)
        });

        if (TryFindResource("PillBackgroundBrush") is Brush bg)
            PillBorder.Background = bg;
        if (TryFindResource("PillBorderBrush") is Brush border)
            PillBorder.BorderBrush = border;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _nativeWindow = new NativeWindow(this);
        _nativeWindow.Initialize();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _nativeWindow.PositionTopCenter();
        _nativeWindow.EnableAcrylic();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            Dispatcher.Invoke(() => _nativeWindow.PositionTopCenter());
        };
    }

    private void OnShowRequested(PillItem item)
    {
        if (NativeWindow.ShouldSuppressNotifications())
        {
            Debug.WriteLine("[PillWindow] Notifications suppressed (Fullscreen or Focus Assist active).");
            return;
        }

        _nativeWindow.PositionTopCenter();
        UpdateContent(item);

        // Allow user to interact with the pill
        _nativeWindow.SetClickThrough(false);

        _state = PillState.Entering;
        var sb = FindStoryboard(_useReducedMotion ? "ReducedMotionEntranceStoryboard" : "EntranceStoryboard");
        sb.Completed += (_, _) =>
        {
            _state = PillState.Showing;
        };
        sb.Begin(this, true);
    }

    private void OnDismissRequested()
    {
        if (_state == PillState.Idle || _state == PillState.Dismissed)
            return;

        _state = PillState.Exiting;
        var sb = FindStoryboard(_useReducedMotion ? "ReducedMotionRetractStoryboard" : "RetractStoryboard");
        sb.Completed += (_, _) =>
        {
            _state = PillState.Idle;
            // Restore click-through so idle window never intercepts desktop clicks
            _nativeWindow.SetClickThrough(true);
        };
        sb.Begin(this, true);
    }

    private void OnSwapRequested(PillItem newItem)
    {
        _state = PillState.Swapping;

        var collapse = FindStoryboard("SwapCollapseStoryboard");
        collapse.Completed += (_, _) =>
        {
            UpdateContent(newItem);

            var expand = FindStoryboard("SwapExpandStoryboard");
            expand.Completed += (_, _) =>
            {
                _state = PillState.Showing;
            };
            expand.Begin(this, true);
        };
        collapse.Begin(this, true);
    }

    private void UpdateContent(PillItem item)
    {
        TitleText.Text = item.Title;
        BodyText.Text = item.Body;
        TimestampText.Text = item.TimestampDisplay;

        if (item.AppIcon != null)
        {
            AppIconImage.Source = item.AppIcon;
            AppIconHost.Visibility = Visibility.Visible;
            FallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppIconHost.Visibility = Visibility.Collapsed;
            FallbackIcon.Visibility = Visibility.Visible;
            FallbackInitialText.Text = item.FallbackInitial;
            FallbackIconBrush.Color = item.FallbackColor;
        }

        if (item.IsBurstMerged)
        {
            BurstCountText.Text = item.MergeCount.ToString();
            BurstBadge.Visibility = Visibility.Visible;
        }
        else
        {
            BurstBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ── Mouse & Touch Gesture Interactions ──────────────────────────

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_state != PillState.Showing && _state != PillState.Hovered)
            return;

        _state = PillState.Hovered;
        _presenter?.NotifyHovered();

        // Swap timestamp for close button
        CloseButton.Opacity = 1.0;
        TimestampText.Visibility = Visibility.Collapsed;

        // Visual hover enhancement
        if (TryFindResource("PillHoverBorderBrush") is Brush hoverBrush)
            PillBorder.BorderBrush = hoverBrush;
        if (TryFindResource("PillHoverBackgroundBrush") is Brush hoverBg)
            PillBorder.Background = hoverBg;

        if (!_useReducedMotion)
        {
            var hoverIn = FindStoryboard("HoverInStoryboard");
            hoverIn.Begin(this, true);
        }
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_state != PillState.Hovered)
            return;

        _state = PillState.Showing;
        _presenter?.NotifyUnhovered();
        _isDragging = false;

        // Restore timestamp
        CloseButton.Opacity = 0.0;
        TimestampText.Visibility = Visibility.Visible;

        if (TryFindResource("PillBorderBrush") is Brush normalBrush)
            PillBorder.BorderBrush = normalBrush;
        if (TryFindResource("PillBackgroundBrush") is Brush normalBg)
            PillBorder.Background = normalBg;

        if (!_useReducedMotion)
        {
            var hoverOut = FindStoryboard("HoverOutStoryboard");
            hoverOut.Begin(this, true);
        }
    }

    private void Pill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPos = e.GetPosition(this);
        _isDragging = true;
        PillBorder.CaptureMouse();
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point currentPos = e.GetPosition(this);
        double deltaY = currentPos.Y - _mouseDownPos.Y;

        if (deltaY < 0)
        {
            // Direct drag follow for upward swipe
            PillTranslate.Y = 16 + deltaY;

            // Swipe threshold exceeded (24px) -> dismiss
            if (deltaY < -24)
            {
                _isDragging = false;
                PillBorder.ReleaseMouseCapture();
                SwipeDismiss();
            }
        }
        else
        {
            // Rubber-band resistance (0.6x factor) on downward drag
            PillTranslate.Y = 16 + (deltaY * 0.6);
        }
    }

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            PillBorder.ReleaseMouseCapture();

            if (_state != PillState.Exiting)
            {
                PillTranslate.Y = 16;
            }
            return;
        }

        // Click pill body -> activate app
        var currentItem = _presenter?.CurrentItem;
        if (currentItem != null)
        {
            AppLauncher.ActivateApp(currentItem);
        }

        _presenter?.DismissImmediate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SwipeDismiss();
    }

    private void SwipeDismiss()
    {
        _state = PillState.Exiting;
        var sb = FindStoryboard("SwipeDismissStoryboard");
        sb.Completed += (_, _) =>
        {
            _state = PillState.Idle;
            _nativeWindow.SetClickThrough(true);
        };
        sb.Begin(this, true);

        _presenter?.DismissImmediate();
    }

    private Storyboard FindStoryboard(string key)
    {
        return (Storyboard)FindResource(key);
    }

    public void TriggerDemoNotification(string title, string body, string appId = "DynamicPill.Demo")
    {
        var item = new PillItem
        {
            Id = (uint)Random.Shared.Next(),
            AppId = appId,
            AppName = "Maya Chen",
            Title = title,
            Body = body,
            Timestamp = DateTimeOffset.Now
        };
        _presenter?.Enqueue(item);
    }
}
