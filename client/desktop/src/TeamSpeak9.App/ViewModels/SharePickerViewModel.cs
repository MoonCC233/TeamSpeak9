// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Streaming;

namespace TeamSpeak9.App.ViewModels;

/// <summary>
/// One surface the user can pick in the share picker.
/// </summary>
public sealed partial class ShareTargetViewModel : ObservableObject
{
    public ShareTargetViewModel(ScreenCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
    }

    /// <summary>The underlying capture target.</summary>
    public ScreenCaptureTarget Target { get; }

    /// <summary>Kind label: 显示器 or 窗口.</summary>
    public string KindLabel => Target.Kind == ScreenCaptureKind.Display ? "显示器" : "窗口";

    /// <summary>Friendly name: monitor description or window title.</summary>
    public string Name => Target.Name;

    /// <summary>Size text, e.g. "1920 × 1080".</summary>
    public string SizeText => string.Create(CultureInfo.CurrentCulture, $"{Target.Width} × {Target.Height}");

    /// <summary>Whether this row is selected in the picker list.</summary>
    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// Backs the share picker: lists the surfaces the user can share and carries the capture options.
/// </summary>
/// <remarks>
/// The picker is a modal dialog, so it is transient: each open gets a fresh instance and the
/// chosen target is read back through <see cref="SelectedTarget"/> after <c>ShowDialog</c> returns.
/// </remarks>
public sealed partial class SharePickerViewModel : ObservableObject
{
    private readonly IScreenTargetEnumerator targets;
    private readonly StreamSettings settings;

    public SharePickerViewModel(IScreenTargetEnumerator targets, StreamSettings settings)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);

        this.targets = targets;
        this.settings = settings;

        foreach (var display in targets.ListDisplays())
        {
            Displays.Add(new ShareTargetViewModel(display));
        }

        foreach (var window in targets.ListWindows())
        {
            Windows.Add(new ShareTargetViewModel(window));
        }

        ShowCaptureBorder = settings.ShowCaptureBorder;
        CaptureCursor = settings.CaptureCursor;
    }

    /// <summary>Monitors, primary first.</summary>
    public ObservableCollection<ShareTargetViewModel> Displays { get; } = [];

    /// <summary>Top-level windows worth offering.</summary>
    public ObservableCollection<ShareTargetViewModel> Windows { get; } = [];

    /// <summary>Draw the system highlight around the captured surface.</summary>
    [ObservableProperty]
    private bool showCaptureBorder;

    /// <summary>Include the mouse cursor in the captured frames.</summary>
    [ObservableProperty]
    private bool captureCursor;

    /// <summary>The target the user picked, or <see langword="null"/> if they cancelled.</summary>
    public ScreenCaptureTarget? SelectedTarget { get; private set; }

    /// <summary>Whether the picker has at least one surface to offer.</summary>
    public bool HasTargets => Displays.Count > 0 || Windows.Count > 0;

    /// <summary>Whether the picker is empty, shown as a hint row.</summary>
    public bool IsEmpty => !HasTargets;

    /// <summary>Raised when the user confirms their selection.</summary>
    public event EventHandler? Confirmed;

    /// <summary>Confirms the selection and closes the dialog.</summary>
    [RelayCommand]
    private void Confirm()
    {
        SelectedTarget = Displays.FirstOrDefault(d => d.IsSelected)?.Target
            ?? Windows.FirstOrDefault(w => w.IsSelected)?.Target;

        if (SelectedTarget is not null)
        {
            settings.ShowCaptureBorder = ShowCaptureBorder;
            settings.CaptureCursor = CaptureCursor;
            Confirmed?.Invoke(this, EventArgs.Empty);
        }
    }
}