// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

namespace TeamSpeak9.Core.Audio;

/// <summary>
/// Which direction a device moves audio in.
/// </summary>
public enum AudioDeviceKind
{
    /// <summary>A capture endpoint: microphone, line in, loopback.</summary>
    Input,

    /// <summary>A render endpoint: speakers, headphones.</summary>
    Output,
}

/// <summary>
/// One audio endpoint as offered to the user in the top bar device menus.
/// </summary>
/// <param name="Id">
/// Stable platform device id, persisted into
/// <see cref="Settings.AudioSettings.InputDeviceId"/> / <see cref="Settings.AudioSettings.OutputDeviceId"/>.
/// Empty means "whatever the system default is", which is also the settings default.
/// </param>
/// <param name="Name">Friendly name to show.</param>
/// <param name="Kind">Capture or render.</param>
/// <param name="IsDefault">True for the endpoint the OS currently treats as the default for multimedia.</param>
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault)
{
    /// <summary>Id meaning "follow the system default endpoint".</summary>
    public const string SystemDefaultId = "";

    /// <summary>Row that represents the system default, shown at the top of both menus.</summary>
    public static AudioDeviceInfo SystemDefault(AudioDeviceKind kind) => new(
        SystemDefaultId,
        kind == AudioDeviceKind.Input ? "默认输入设备" : "默认输出设备",
        kind,
        IsDefault: true);

    /// <summary>True when this row is the "follow the system default" placeholder.</summary>
    public bool IsSystemDefault => Id.Length == 0;
}
