// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Audio;

namespace TeamSpeak9.App.ViewModels;

/// <summary>
/// One row of the top bar's input or output device menu.
/// </summary>
/// <remarks>
/// Immutable, and the whole list is rebuilt when the selection changes. A device menu holds a
/// handful of entries, so rebuilding is cheaper than tracking per-row change notifications, and it
/// keeps the check mark and the settings in sync by construction.
/// </remarks>
public sealed class AudioDeviceViewModel
{
    internal AudioDeviceViewModel(AudioDeviceInfo device, bool isSelected)
    {
        Device = device;
        IsSelected = isSelected;
    }

    internal AudioDeviceInfo Device { get; }

    /// <summary>Endpoint id, or the empty string for the "follow Windows" entry.</summary>
    public string Id => Device.Id;

    /// <summary>Menu label. The synthetic entry is already named "默认输入/输出设备".</summary>
    public string Name => Device.Name;

    /// <summary>True when this is what Windows currently uses by default.</summary>
    public bool IsSystemDefault => Device.IsDefault;

    /// <summary>Drives the menu item's check mark.</summary>
    public bool IsSelected { get; }
}
