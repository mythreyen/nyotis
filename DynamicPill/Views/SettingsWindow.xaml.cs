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

        DurationSlider.Value = _presenter.NormalDisplaySeconds;
        if (DurationLabel != null)
        {
            DurationLabel.Text = $"{_presenter.NormalDisplaySeconds:0.0} s";
        }
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
        if (_presenter != null)
        {
            _presenter.NormalDisplaySeconds = e.NewValue;
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
        string[] messages =
        [
            "Invoice 1039 approved",
            "Meeting moved to 4 PM",
            "Project update from Maya",
            "Invoice 1042 is ready"
        ];

        for (int i = 0; i < messages.Length; i++)
        {
            _presenter.Enqueue(new PillItem
            {
                Id = (uint)Random.Shared.Next(),
                AppId = "Microsoft.Outlook",
                AppName = "Mail",
                Title = "Mail",
                Body = messages[i],
                Timestamp = DateTimeOffset.Now
            });
        }
    }
}
