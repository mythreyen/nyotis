namespace DynamicPill.Views;

using System;
using System.Diagnostics;
using System.Windows;
using DynamicPill.Models;
using DynamicPill.Services;

public partial class SettingsWindow : Window
{
    private readonly PillWindow _pillWindow;
    private readonly PillPresenter _presenter;

    public SettingsWindow(PillWindow pillWindow, PillPresenter presenter)
    {
        InitializeComponent();
        _pillWindow = pillWindow;
        _presenter = presenter;
    }

    private void OpenWindowsSettings_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show($"Unable to open Windows Settings: {ex.Message}", "DynamicPill", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        if (DarkThemeRadio == null || LightThemeRadio == null) return;
        bool isDark = DarkThemeRadio.IsChecked == true;
        _pillWindow.ApplyTheme(isDark);
    }

    private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DurationLabel != null)
        {
            DurationLabel.Text = $"{e.NewValue:0.0} s";
        }
    }

    private void TestCompact_Click(object sender, RoutedEventArgs e)
    {
        _presenter.Enqueue(new PillItem
        {
            Id = (uint)Random.Shared.Next(),
            AppId = "Slack.Desktop",
            AppName = "Maya Chen",
            Title = "Maya Chen",
            Body = "Are we still on for the 3 pm review?",
            Timestamp = DateTimeOffset.Now
        });
    }

    private void TestSwap_Click(object sender, RoutedEventArgs e)
    {
        _presenter.Enqueue(new PillItem
        {
            Id = (uint)Random.Shared.Next(),
            AppId = "Slack.Desktop",
            AppName = "Maya Chen",
            Title = "Maya Chen",
            Body = "Are we still on for the 3 pm review?",
            Timestamp = DateTimeOffset.Now
        });

        // Trigger immediate swap
        _presenter.Enqueue(new PillItem
        {
            Id = (uint)Random.Shared.Next(),
            AppId = "VisualStudio.Code",
            AppName = "Build System",
            Title = "Build finished",
            Body = "Release 2.4 passed all checks",
            Timestamp = DateTimeOffset.Now
        });
    }

    private void TestBurst_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 1; i <= 4; i++)
        {
            _presenter.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Microsoft.Outlook",
                AppName = "Mail",
                Title = "Mail · 4 new messages",
                Body = "Latest: Invoice 1042 is ready",
                Timestamp = DateTimeOffset.Now,
                MergeCount = 4
            });
        }
    }
}
