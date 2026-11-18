namespace DynamicPill.Models;

using System;
using System.Windows.Media;

/// <summary>
/// Encapsulates notification item metadata matching the design specification.
/// </summary>
public sealed class PillItem
{
    public uint Id { get; init; }

    public string AppId { get; init; } = string.Empty;

    public string AppName { get; init; } = string.Empty;

    public ImageSource? AppIcon { get; init; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string TimestampDisplay
    {
        get
        {
            var diff = DateTimeOffset.Now - Timestamp;
            if (diff.TotalMinutes < 2)
                return "now";
            return Timestamp.ToLocalTime().ToString("h:mm tt");
        }
    }

    public string FallbackInitial =>
        string.IsNullOrWhiteSpace(AppName) ? "?" : AppName.Trim()[..1].ToUpperInvariant();

    /// <summary>
    /// Color palette derived from design artboards:
    /// Blue #2F66E8, Orange #E8603C, Teal #1FA97A, Purple #8B5CF6, Amber #F0B04A
    /// </summary>
    public Color FallbackColor
    {
        get
        {
            Color[] palette =
            [
                Color.FromRgb(0x2F, 0x66, 0xE8), // Brand Blue
                Color.FromRgb(0x1F, 0xA9, 0x7A), // Emerald Green
                Color.FromRgb(0xF0, 0xB0, 0x4A), // Amber Mail
                Color.FromRgb(0xE8, 0x60, 0x3C), // Warm Coral
                Color.FromRgb(0x8B, 0x5C, 0xF6)  // Violet
            ];

            uint hash = (uint)Math.Abs(AppId.GetHashCode());
            return palette[hash % palette.Length];
        }
    }

    public int MergeCount { get; set; } = 1;

    public bool IsBurstMerged => MergeCount > 1;
}
