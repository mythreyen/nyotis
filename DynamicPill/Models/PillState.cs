namespace DynamicPill.Models;

/// <summary>
/// Lifecycle states of the pill overlay window.
/// </summary>
public enum PillState
{
    /// <summary>Pill is hidden/off-screen; window passes all clicks through.</summary>
    Idle,

    /// <summary>Pill is dropping down from Y = -80 to Y = 16 with overshoot.</summary>
    Entering,

    /// <summary>Pill is visible and interactive; 4.5s countdown running.</summary>
    Showing,

    /// <summary>Cursor is hovering; countdown paused, pill scaled 1.04x with close button.</summary>
    Hovered,

    /// <summary>Transitioning to next queued notification (collapse -> crossfade -> expand).</summary>
    Swapping,

    /// <summary>Retracting smoothly upward to Y = -80.</summary>
    Exiting,

    /// <summary>Retract complete; transition back to Idle.</summary>
    Dismissed
}
