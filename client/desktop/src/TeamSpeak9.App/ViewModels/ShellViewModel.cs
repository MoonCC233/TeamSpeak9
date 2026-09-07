// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TeamSpeak9.App.Audio;
using TeamSpeak9.App.Converters;
using TeamSpeak9.Core.Audio;
using TeamSpeak9.Core.Connection;
using TeamSpeak9.Core.Identity;
using TeamSpeak9.Core.Management;
using TeamSpeak9.Core.Model;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.App.Streaming;
using TSLib.Commands;

namespace TeamSpeak9.App.ViewModels;

/// <summary>
/// Which tab the right hand panel shows.
/// </summary>
public enum ChatPanelTab
{
    Info,
    Chat,
    Files,
}

/// <summary>
/// State behind the main window: top bar, server list, channel tree and chat panel.
/// </summary>
/// <remarks>
/// <para>
/// Single instance for the whole app, matching the one <see cref="TsConnection"/>. Every
/// <c>TsConnection</c> event already arrives on the UI thread, so nothing here marshals.
/// </para>
/// <para>
/// Settings that the user can change from the shell (mute flags, panel widths, chat visibility) are
/// written back through <see cref="SettingsStore"/> as they change. The store serialises its writes
/// internally, so it is safe to call from a property setter.
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly TsConnection connection;
    private readonly ChannelService channels;
    private readonly FileService files;
    private readonly IconService icons;
    private readonly AudioService audio;
    private readonly AppSettings settings;
    private readonly SettingsStore settingsStore;
    private readonly IdentityStore identityStore;
    private readonly ILogger<ShellViewModel> log;
        private readonly ScreenShareService screenShareService;

    /// <summary>Exposes the screen share service for command bindings.</summary>
    public ScreenShareService ScreenShareService => screenShareService;
        private readonly ChannelTreeState treeState = new();

        private bool disposed;
        private bool isAway;
        private bool prefetchRunning;

    private ImmutableArray<AudioDeviceViewModel> inputDeviceRows = ImmutableArray<AudioDeviceViewModel>.Empty;
    private ImmutableArray<AudioDeviceViewModel> outputDeviceRows = ImmutableArray<AudioDeviceViewModel>.Empty;

    /// <summary>Channel whose description is already in <see cref="ChannelDescriptionBlocks"/>.</summary>
    private ulong describedChannelId;
    private bool descriptionLoading;

    /// <summary>Channel whose file area is already in <see cref="FileRows"/>.</summary>
    private ulong listedChannelId;
    private bool filesLoading;

    /// <summary>Text <see cref="WelcomeBlocks"/> was parsed from, so it is only parsed once.</summary>
    private string welcomeSource = string.Empty;

    [ObservableProperty]
    private string statusText = "未连接";

    [ObservableProperty]
    private bool isBusy;

    /// <summary>Text of the left column's connect box. Enter connects to it.</summary>
    [ObservableProperty]
    private string quickConnectAddress = string.Empty;

    /// <summary>Bookmark filter text.</summary>
    [ObservableProperty]
    private string bookmarkFilter = string.Empty;

    /// <summary>Draft message in the chat composer.</summary>
    [ObservableProperty]
    private string messageDraft = string.Empty;

    [ObservableProperty]
    private ChatPanelTab activeTab = ChatPanelTab.Chat;

    /// <summary>Server facts for the info tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerInfo))]
    private ImmutableArray<InfoRow> serverInfoRows = ImmutableArray<InfoRow>.Empty;

    /// <summary>Facts about the channel we are in, for the info tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannelInfo))]
    private ImmutableArray<InfoRow> channelInfoRows = ImmutableArray<InfoRow>.Empty;

    /// <summary>The welcome message, rendered as Markdown like every other server text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWelcomeMessage))]
    private ImmutableArray<MarkdownNode> welcomeBlocks = ImmutableArray<MarkdownNode>.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannelDescription))]
    private ImmutableArray<MarkdownNode> channelDescriptionBlocks = ImmutableArray<MarkdownNode>.Empty;

    /// <summary>Entries of the current channel's file area, for the files tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    private ImmutableArray<FileRow> fileRows = ImmutableArray<FileRow>.Empty;

    /// <summary>Directory <see cref="FileRows"/> was listed from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtFilesRoot))]
    private string filesPath = FileService.RootPath;

    /// <summary>Why the file list is empty: loading, nothing here, or an error.</summary>
    [ObservableProperty]
    private string filesStatus = string.Empty;

    /// <summary>Selected file row, driving the download and delete buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFile))]
    [NotifyPropertyChangedFor(nameof(HasFileSelection))]
    private FileRow? selectedFile;

    public ShellViewModel(
        TsConnection connection,
        ChannelService channels,
        FileService files,
        IconService icons,
        AudioService audio,
        AppSettings settings,
        SettingsStore settingsStore,
        IdentityStore identityStore,
            ScreenShareService screenShareService,
            ILogger<ShellViewModel> log)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(channels);
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(icons);
            ArgumentNullException.ThrowIfNull(audio);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(settingsStore);
            ArgumentNullException.ThrowIfNull(identityStore);
            ArgumentNullException.ThrowIfNull(screenShareService);
            ArgumentNullException.ThrowIfNull(log);

            this.connection = connection;
            this.channels = channels;
            this.files = files;
            this.icons = icons;
            this.audio = audio;
            this.settings = settings;
            this.settingsStore = settingsStore;
            this.identityStore = identityStore;
            this.screenShareService = screenShareService;
            this.log = log;

            Bookmarks = new ObservableCollection<BookmarkViewModel>();
            Channels = new ObservableCollection<ChannelTreeItem>();
            Messages = new ObservableCollection<MessageViewModel>();

            connection.StateChanged += OnStateChanged;
            connection.SnapshotChanged += OnSnapshotChanged;
            connection.MessageReceived += OnMessageReceived;
        connection.Poked += OnPoked;
        connection.ServerError += OnServerError;
        icons.IconCached += OnIconCached;
        audio.DevicesChanged += OnAudioDevicesChanged;
        audio.TransmittingChanged += OnAudioTransmittingChanged;

        RebuildBookmarks();
        RebuildDeviceMenus();
    }

    /// <summary>Bookmark rows for the left column, filtered by <see cref="BookmarkFilter"/>.</summary>
    public ObservableCollection<BookmarkViewModel> Bookmarks { get; }

    /// <summary>Root rows of the channel tree.</summary>
    public ObservableCollection<ChannelTreeItem> Channels { get; }

    public ObservableCollection<MessageViewModel> Messages { get; }

    public ConnectionState State => connection.State;

    public bool IsConnected => connection.IsConnected;

    public bool IsDisconnected => connection.State == ConnectionState.Disconnected
        || connection.State == ConnectionState.Failed;

    /// <summary>True while connecting or reconnecting; drives the progress affordance.</summary>
    public bool IsConnecting => connection.State is ConnectionState.Connecting or ConnectionState.Reconnecting;

    public string ServerName => connection.Snapshot.DisplayName;

    public IconId ServerIconId => connection.Snapshot.IconId;

    public string ServerAddress => connection.Snapshot.Address;

    public int ClientCount => connection.Snapshot.ClientCount;

    public int MaxClients => connection.Snapshot.MaxClients;

    /// <summary><c>12/64</c> for the server banner.</summary>
    public string ServerOccupancyText => MaxClients > 0 ? $"{ClientCount}/{MaxClients}" : ClientCount.ToString();

    public bool HasServerBanner => connection.Snapshot.Banner.HasBanner;

    public string ServerBannerUrl => connection.Snapshot.Banner.GfxUrl;

    /// <summary>Own nickname, from the server when connected and from settings otherwise.</summary>
    public string OwnNickname => connection.Snapshot.OwnClient?.Name is { Length: > 0 } name
        ? name
        : settings.Nickname;

    /// <summary>Channel we are in, for the footer's location line.</summary>
    public string OwnChannelName => connection.Snapshot.OwnChannel?.Name ?? string.Empty;

    public string OwnUid => connection.Snapshot.OwnClient?.Uid ?? string.Empty;

    /// <summary>Title of the chat panel: the current channel, or a placeholder.</summary>
    public string ChatTitle => OwnChannelName is { Length: > 0 } channel ? channel : "聊天";

    public bool HasMessages => Messages.Count > 0;

    public bool HasServerInfo => !ServerInfoRows.IsDefaultOrEmpty;

    public bool HasChannelInfo => !ChannelInfoRows.IsDefaultOrEmpty;

    public bool HasWelcomeMessage => !WelcomeBlocks.IsDefaultOrEmpty;

    public bool HasChannelDescription => !ChannelDescriptionBlocks.IsDefaultOrEmpty;

    public bool HasFiles => !FileRows.IsDefaultOrEmpty;

    /// <summary>False once the user has navigated into a subdirectory, enabling the up button.</summary>
    public bool IsAtFilesRoot => FilesPath == FileService.RootPath;

    public bool HasFileSelection => SelectedFile is not null;

    /// <summary>Only files can be downloaded; a directory selection leaves the button disabled.</summary>
    public bool CanDownloadFile => SelectedFile?.IsFile == true;

    // The pill toggles bind IsChecked to these, so "checked" means muted, which is what the
    // Toggle.Pill danger styling expects.
    public bool IsInputMuted
    {
        get => settings.Audio.InputMuted;
        set
        {
            if (settings.Audio.InputMuted == value)
                return;

            settings.Audio.InputMuted = value;
            OnPropertyChanged();
            PersistSettings();
            audio.ApplySettings();
            _ = PushMuteStateAsync("client_input_muted", value);
        }
    }

    public bool IsOutputMuted
    {
        get => settings.Audio.OutputMuted;
        set
        {
            if (settings.Audio.OutputMuted == value)
                return;

            settings.Audio.OutputMuted = value;
            OnPropertyChanged();
            PersistSettings();
            audio.ApplySettings();
            _ = PushMuteStateAsync("client_output_muted", value);
        }
    }

    /// <summary>Capture endpoints for the top bar's microphone menu.</summary>
    public ImmutableArray<AudioDeviceViewModel> InputDevices => inputDeviceRows;

    /// <summary>Render endpoints for the top bar's speaker menu.</summary>
    public ImmutableArray<AudioDeviceViewModel> OutputDevices => outputDeviceRows;

    /// <summary>Selected capture device id; empty means follow the Windows default.</summary>
    public string SelectedInputDeviceId => audio.SelectedInputDeviceId;

    /// <summary>Selected render device id; empty means follow the Windows default.</summary>
    public string SelectedOutputDeviceId => audio.SelectedOutputDeviceId;

    /// <summary>Whether our own microphone is currently passing audio, for the top bar indicator.</summary>
    public bool IsTransmitting => audio.IsTransmitting;

    /// <summary>AFK pill. Maps onto the server's away flag.</summary>
    public bool IsAway
    {
        get => isAway;
        set
        {
            if (isAway == value)
                return;

            isAway = value;
            OnPropertyChanged();
            _ = PushAwayStateAsync(value);
        }
    }

    public bool IsChatPanelVisible
    {
        get => settings.Appearance.ShowChatPanel;
        set
        {
            if (settings.Appearance.ShowChatPanel == value)
                return;

            settings.Appearance.ShowChatPanel = value;
            OnPropertyChanged();
            PersistSettings();
        }
    }

    public double SidebarWidth
    {
        get => settings.Appearance.SidebarWidth;
        set
        {
            // GridSplitter drags fire continuously, so ignore sub-pixel noise to avoid a save storm.
            if (Math.Abs(settings.Appearance.SidebarWidth - value) < 0.5)
                return;

            settings.Appearance.SidebarWidth = value;
            OnPropertyChanged();
            PersistSettings();
        }
    }

    public double ChannelPanelWidth
    {
        get => settings.Appearance.ChannelPanelWidth;
        set
        {
            if (Math.Abs(settings.Appearance.ChannelPanelWidth - value) < 0.5)
                return;

            settings.Appearance.ChannelPanelWidth = value;
            OnPropertyChanged();
            PersistSettings();
        }
    }

    public double WindowWidth => settings.Appearance.WindowWidth;

    public double WindowHeight => settings.Appearance.WindowHeight;

    public bool WindowMaximized => settings.Appearance.WindowMaximized;

    /// <summary>
    /// Stores the window size to restore on the next start.
    /// </summary>
    /// <remarks>
    /// Only the restore bounds are kept, never the maximized bounds, so unmaximizing after a
    /// restart lands on the size the user last chose. Called once while closing rather than on every
    /// resize, so no change notification is raised.
    /// </remarks>
    public void SaveWindowState(double restoreWidth, double restoreHeight, bool maximized)
    {
        if (restoreWidth > 0 && restoreHeight > 0)
        {
            settings.Appearance.WindowWidth = restoreWidth;
            settings.Appearance.WindowHeight = restoreHeight;
        }

        settings.Appearance.WindowMaximized = maximized;
        PersistSettings();
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        string address = QuickConnectAddress.Trim();
        if (address.Length == 0)
            return;

        await ConnectToAsync(address, nickname: string.Empty, serverPassword: string.Empty, bookmarkId: string.Empty);
    }

    [RelayCommand]
    private async Task ConnectBookmarkAsync(BookmarkViewModel? bookmark)
    {
        if (bookmark is null)
            return;

        var entry = bookmark.Entry;
        await ConnectToAsync(entry.Address, entry.Nickname, entry.ServerPassword, entry.Id, entry);
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await connection.DisconnectAsync();
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        string text = MessageDraft.Trim();
        if (text.Length == 0 || !connection.IsConnected)
            return;

        MessageDraft = string.Empty;

        var result = await connection.ExecuteAsync(c => c.SendChannelMessage(text));
        if (!result.Ok)
        {
            log.LogWarning("Sending a channel message failed: {Error}", result.Error?.Message);
            AppendSystemMessage($"消息发送失败：{result.Error?.Message}");
            return;
        }

        // The server does not echo our own channel messages back, so show it locally.
        Append(new ChatMessage
        {
            Target = ChatTarget.Channel,
            SenderId = connection.Snapshot.OwnClientId,
            SenderName = OwnNickname,
            SenderUid = OwnUid,
            Text = text,
        });
    }

    [RelayCommand]
    private void SelectTab(ChatPanelTab tab) => ActiveTab = tab;

    /// <remarks>
    /// The channel description is not in <c>channellist</c>, so it is fetched the first time the
    /// info tab is opened for a channel rather than on every snapshot. The file list is fetched the
    /// same way, for the same reason: neither is worth a round trip until it is on screen.
    /// </remarks>
    partial void OnActiveTabChanged(ChatPanelTab value)
    {
        if (value == ChatPanelTab.Info)
            LoadChannelDescription();
        else if (value == ChatPanelTab.Files)
            LoadChannelFiles();
    }

    [RelayCommand]
    private void OpenCommunitySite() => OpenUrl("https://www.teamspeak.com/en/more/community/");

    /// <summary>
    /// Opens a URL in the default browser.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute</c> is required: without it .NET tries to execute the URL as a file. Only
    /// http/https are allowed, so a hostile server-supplied banner link cannot launch a program.
    /// </remarks>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            log.LogWarning("Refusing to open a non-http(s) URL.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogError(ex, "Could not open the default browser.");
        }
    }

    [RelayCommand]
    private void ToggleChatPanel() => IsChatPanelVisible = !IsChatPanelVisible;

    /// <summary>
    /// Picks a capture or render device. The menu binds the row itself, so a single command serves
    /// both lists and the kind comes from the device rather than from which menu was clicked.
    /// </summary>
    [RelayCommand]
    private void SelectDevice(AudioDeviceViewModel? row)
    {
        if (row is null)
            return;

        if (row.Device.Kind == AudioDeviceKind.Input)
            audio.SelectInputDevice(row.Id);
        else
            audio.SelectOutputDevice(row.Id);

        RebuildDeviceMenus();
    }

    /// <summary>Re-enumerates the endpoints, for when the user plugs a headset in.</summary>
    [RelayCommand]
    private void RefreshAudioDevices() => audio.RefreshDevices();

    /// <remarks>Already on the UI thread: <see cref="AudioService"/> marshals before raising.</remarks>
    private void OnAudioDevicesChanged() => RebuildDeviceMenus();

    /// <remarks>Already on the UI thread: <see cref="AudioService"/> marshals before raising.</remarks>
    private void OnAudioTransmittingChanged(bool value) => OnPropertyChanged(nameof(IsTransmitting));

    /// <summary>
    /// Rebuilds both device menus from the current endpoint lists and selection.
    /// </summary>
    /// <remarks>
    /// A selection that no longer exists (device unplugged) leaves no row checked, which is
    /// honest: the pipeline silently falls back to the system default in that case, and the
    /// setting still points at the missing device in case it comes back.
    /// </remarks>
    private void RebuildDeviceMenus()
    {
        inputDeviceRows = Rows(audio.InputDevices, audio.SelectedInputDeviceId);
        outputDeviceRows = Rows(audio.OutputDevices, audio.SelectedOutputDeviceId);

        OnPropertyChanged(nameof(InputDevices));
        OnPropertyChanged(nameof(OutputDevices));
        OnPropertyChanged(nameof(SelectedInputDeviceId));
        OnPropertyChanged(nameof(SelectedOutputDeviceId));

        static ImmutableArray<AudioDeviceViewModel> Rows(
            IReadOnlyList<AudioDeviceInfo> devices,
            string selectedId)
        {
            var builder = ImmutableArray.CreateBuilder<AudioDeviceViewModel>(devices.Count);
            foreach (var device in devices)
            {
                builder.Add(new AudioDeviceViewModel(
                    device,
                    string.Equals(device.Id, selectedId, StringComparison.Ordinal)));
            }

            return builder.MoveToImmutable();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await connection.RefreshAsync();

    /// <summary>
    /// The token the toolbar sends for the link button; it needs its own shape, not a delimiter.
    /// </summary>
    public const string LinkToken = "link";

    /// <summary>
    /// Applies one of the composer toolbar's Markdown tokens to <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composer is a plain <c>TextBox</c>, so selection handling lives in the view; this only
    /// gets the resulting text. Splitting it this way keeps the ViewModel free of control state.
    /// </para>
    /// <para>
    /// A token ending in a space (<c>"&gt; "</c>, <c>"- "</c>, <c>"# "</c>) is a block marker and
    /// prefixes whole lines; <see cref="LinkToken"/> builds an inline link; anything else is an
    /// inline delimiter that wraps the selection.
    /// </para>
    /// </remarks>
    public static string ApplyMarkdown(string text, int selectionStart, int selectionLength, string token, out int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (token == LinkToken)
            return InsertLink(text, selectionStart, selectionLength, out caret);

        return IsBlockMarker(token)
            ? PrefixLines(text, selectionStart, selectionLength, token, out caret)
            : Wrap(text, selectionStart, selectionLength, token, out caret);
    }

    /// <summary>
    /// Wraps the selection in an inline delimiter, or inserts an empty pair at the caret.
    /// </summary>
    public static string Wrap(string text, int selectionStart, int selectionLength, string delimiter, out int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(delimiter);

        ClampSelection(text, ref selectionStart, ref selectionLength);

        string selected = text.Substring(selectionStart, selectionLength);

        caret = selectionLength == 0
            ? selectionStart + delimiter.Length
            : selectionStart + (delimiter.Length * 2) + selectionLength;

        return string.Concat(
            text[..selectionStart],
            delimiter,
            selected,
            delimiter,
            text[(selectionStart + selectionLength)..]);
    }

    /// <summary>
    /// Prefixes every line the selection touches with a block marker such as <c>"&gt; "</c>.
    /// </summary>
    /// <remarks>
    /// The marker is added unconditionally rather than toggled, so clicking quote twice nests the
    /// quote — which is what Markdown means by <c>"&gt; &gt; "</c>.
    /// </remarks>
    public static string PrefixLines(string text, int selectionStart, int selectionLength, string prefix, out int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        ClampSelection(text, ref selectionStart, ref selectionLength);

        int selectionEnd = selectionStart + selectionLength;
        int blockStart = selectionStart == 0 ? 0 : text.LastIndexOf('\n', selectionStart - 1) + 1;
        int newline = text.IndexOf('\n', selectionEnd);
        int blockEnd = newline < 0 ? text.Length : newline;

        string[] lines = text[blockStart..blockEnd].Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = prefix + lines[i];

        string replaced = string.Join('\n', lines);

        // An empty selection only ever shifts one line, so keep the caret where the user left it.
        caret = selectionLength == 0
            ? selectionStart + prefix.Length
            : blockStart + replaced.Length;

        return string.Concat(text[..blockStart], replaced, text[blockEnd..]);
    }

    /// <summary>
    /// Inserts an inline link. A selected URL becomes the target, anything else becomes the label.
    /// </summary>
    public static string InsertLink(string text, int selectionStart, int selectionLength, out int caret)
    {
        ArgumentNullException.ThrowIfNull(text);

        ClampSelection(text, ref selectionStart, ref selectionLength);

        string selected = text.Substring(selectionStart, selectionLength);
        bool selectionIsUrl = Markdown.IsSafeUrl(selected);
        string label = selectionIsUrl ? string.Empty : selected;
        string target = selectionIsUrl ? selected : string.Empty;

        // Caret goes wherever the user still has to type: the label for a URL or an empty
        // selection, the target once there is a label to keep.
        caret = label.Length == 0
            ? selectionStart + 1
            : selectionStart + label.Length + 3;

        return string.Concat(
            text[..selectionStart],
            "[",
            label,
            "](",
            target,
            ")",
            text[(selectionStart + selectionLength)..]);
    }

    private static bool IsBlockMarker(string token) => token.Length > 1 && token[^1] == ' ';

    /// <summary>Clears every info tab field, for a disconnect.</summary>
    private void ClearInfo()
    {
        welcomeSource = string.Empty;
        describedChannelId = 0;
        ServerInfoRows = ImmutableArray<InfoRow>.Empty;
        ChannelInfoRows = ImmutableArray<InfoRow>.Empty;
        WelcomeBlocks = ImmutableArray<MarkdownNode>.Empty;
        ChannelDescriptionBlocks = ImmutableArray<MarkdownNode>.Empty;
        ClearFiles();
    }

    /// <summary>Drops the file listing, for a disconnect or a channel change.</summary>
    private void ClearFiles()
    {
        listedChannelId = 0;
        FilesPath = FileService.RootPath;
        FileRows = ImmutableArray<FileRow>.Empty;
        SelectedFile = null;
        FilesStatus = string.Empty;
    }

    /// <summary>
    /// Builds the info tab's server rows.
    /// </summary>
    /// <remarks>
    /// A pure function over the snapshot, and <c>internal</c> so the tests can cover the formatting
    /// without a server. Fields the snapshot leaves empty are skipped rather than shown blank,
    /// because a fresh connection fills them in over several snapshots.
    /// </remarks>
    internal static ImmutableArray<InfoRow> BuildServerRows(ServerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = ImmutableArray.CreateBuilder<InfoRow>(10);

        void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new InfoRow(label, value));
        }

        Add("名称", snapshot.DisplayName);
        Add("语音提示名", snapshot.PhoneticName);
        Add("地址", snapshot.Address);
        Add("在线人数", snapshot.MaxClients > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{snapshot.ClientCount} / {snapshot.MaxClients}")
            : snapshot.ClientCount.ToString(CultureInfo.CurrentCulture));
        Add("版本", snapshot.Version);
        Add("平台", snapshot.Platform);
        Add("协议版本", snapshot.ProtocolVersion > 0
            ? snapshot.ProtocolVersion.ToString(CultureInfo.CurrentCulture)
            : string.Empty);
        Add("许可类型", DescribeLicense(snapshot.License));
        Add("语音加密", DescribeEncryption(snapshot.VoiceEncryption));
        Add("创建时间", FormatDate(snapshot.Created));

        return rows.ToImmutable();
    }

    /// <summary>
    /// Builds the info tab's rows for the channel we are in, or none when we are not in one.
    /// </summary>
    /// <remarks>
    /// The description is not here: <c>channellist</c> does not carry it, so it needs its own
    /// request and is rendered as Markdown rather than as a row.
    /// </remarks>
    internal static ImmutableArray<InfoRow> BuildChannelRows(ChannelNode? channel)
    {
        if (channel is null)
            return ImmutableArray<InfoRow>.Empty;

        var rows = ImmutableArray.CreateBuilder<InfoRow>(8);

        void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new InfoRow(label, value));
        }

        Add("名称", channel.Name);
        Add("主题", channel.Topic);
        Add("语音提示名", channel.PhoneticName);
        Add("类型", DescribeChannelKind(channel.Kind));
        Add("人数", string.Create(
            CultureInfo.CurrentCulture,
            $"{channel.MemberCount} / {channel.MaxClients}"));
        Add("编解码器", DescribeCodec(channel.Codec));
        Add("编码质量", channel.CodecQuality.ToString(CultureInfo.CurrentCulture));

        // 0 is the default and says nothing, so only a real requirement is worth a row.
        if (channel.NeededTalkPower > 0)
            Add("所需发言权限", channel.NeededTalkPower.ToString(CultureInfo.CurrentCulture));

        var flags = ChannelFlags(channel);
        Add("状态", flags);

        return rows.ToImmutable();
    }

    /// <remarks>
    /// The password, silence and encryption flags matter to a user in the channel but each would be
    /// a near-empty row of its own, so they are joined into one.
    /// </remarks>
    private static string ChannelFlags(ChannelNode channel)
    {
        var flags = new List<string>(4);
        if (channel.IsDefault)
            flags.Add("默认频道");
        if (channel.HasPassword)
            flags.Add("需要密码");
        if (channel.ForcedSilence)
            flags.Add("强制静音");
        if (channel.IsUnencrypted)
            flags.Add("语音未加密");

        return string.Join("、", flags);
    }

    internal static string DescribeLicense(ServerLicense license) => license switch
    {
        ServerLicense.NoLicense => "无授权",
        ServerLicense.Athp => "ATHP 授权主机",
        ServerLicense.Lan => "局域网授权",
        ServerLicense.Npl => "非营利授权",
        _ => "未知",
    };

    internal static string DescribeEncryption(VoiceEncryptionMode mode) => mode switch
    {
        VoiceEncryptionMode.Individual => "由各频道决定",
        VoiceEncryptionMode.Disabled => "全服关闭",
        VoiceEncryptionMode.Enabled => "全服强制",
        _ => "未知",
    };

    internal static string DescribeChannelKind(ChannelKind kind) => kind switch
    {
        ChannelKind.Permanent => "永久",
        ChannelKind.SemiPermanent => "半永久",
        ChannelKind.Temporary => "临时",
        _ => "未知",
    };

    /// <remarks>
    /// <see cref="AudioCodec.Raw"/> is a TSLib extension that official clients cannot decode, but a
    /// channel could still be set to it, so it gets a label rather than falling through to "未知".
    /// </remarks>
    internal static string DescribeCodec(AudioCodec codec) => codec switch
    {
        AudioCodec.OpusVoice => "Opus 语音",
        AudioCodec.OpusMusic => "Opus 音乐",
        AudioCodec.CeltMono => "CELT 单声道",
        AudioCodec.SpeexNarrowband => "Speex 窄带",
        AudioCodec.SpeexWideband => "Speex 宽带",
        AudioCodec.SpeexUltraWideband => "Speex 超宽带",
        AudioCodec.Raw => "未压缩",
        _ => "未知",
    };

    /// <summary>
    /// Formats a server timestamp, or returns an empty string when the server did not send one.
    /// </summary>
    /// <remarks>
    /// TSLib decodes these from a unix timestamp, so an absent value arrives as
    /// <see cref="DateTime.MinValue"/> or as the epoch rather than as null.
    /// </remarks>
    internal static string FormatDate(DateTime value)
    {
        if (value == default || value.Year <= 1970)
            return string.Empty;

        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Turns the service's file entries into display rows.
    /// </summary>
    /// <remarks>
    /// Pure, and <c>internal</c> so the tests can cover the formatting without a server. The order
    /// is whatever <see cref="FileService.Sort"/> produced — directories first — and is kept as is.
    /// </remarks>
    internal static ImmutableArray<FileRow> BuildFileRows(IReadOnlyList<ChannelFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return ImmutableArray<FileRow>.Empty;

        var rows = ImmutableArray.CreateBuilder<FileRow>(entries.Count);
        foreach (var entry in entries)
        {
            rows.Add(new FileRow(
                entry.Name,
                entry.FullPath,
                entry.IsFile ? FormatFileSize(entry.Size) : "文件夹",
                FormatDate(entry.Modified),
                entry.IsFile));
        }

        return rows.ToImmutable();
    }

    /// <summary>Formats a byte count the way the file list shows it.</summary>
    internal static string FormatFileSize(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Bytes are whole by definition, so a fractional part would only be noise.
        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} {units[0]}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {units[unit]}");
    }

    private static void ClampSelection(string text, ref int start, ref int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
    }

        /// <summary>
        /// Opens the share picker and starts screen sharing with the selected target.
        /// </summary>
        [RelayCommand]
        public async Task StartScreenShareAsync()
        {
            if (!connection.IsConnected)
            {
                log.LogWarning("Cannot start screen share: not connected to a server.");
                return;
            }

            if (!screenShareService.IsSupported)
            {
                log.LogWarning("Screen sharing is not supported on this system.");
                AppendSystemMessage("当前系统不支持屏幕共享功能。");
                return;
            }

            try
            {
                await screenShareService.StartAsync();
            }
            catch (OperationCanceledException)
            {
                // User cancelled the share picker - not an error
                log.LogInformation("Screen share cancelled by user.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to start screen share.");
                AppendSystemMessage($"开始屏幕共享失败：{ex.Message}");
            }
        }

        public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        connection.StateChanged -= OnStateChanged;
        connection.SnapshotChanged -= OnSnapshotChanged;
        connection.MessageReceived -= OnMessageReceived;
        connection.Poked -= OnPoked;
        connection.ServerError -= OnServerError;
        icons.IconCached -= OnIconCached;
        audio.DevicesChanged -= OnAudioDevicesChanged;
        audio.TransmittingChanged -= OnAudioTransmittingChanged;
    }

    /// <summary>Deletes a channel and surfaces any server-side refusal in the chat log.</summary>
    public async Task DeleteChannelAsync(ulong channelId, bool force)
    {
        var outcome = await channels.DeleteAsync(channelId, force);
        if (!outcome.Ok)
            AppendSystemMessage(outcome.Message);
    }

    /// <summary>Makes a channel the server's default channel.</summary>
    public async Task SetDefaultChannelAsync(ulong channelId)
    {
        var outcome = await channels.SetDefaultAsync(channelId);
        if (!outcome.Ok)
            AppendSystemMessage(outcome.Message);
    }

    /// <summary>Re-lists the directory the files tab is showing.</summary>
    public async Task RefreshFilesAsync() => await ListFilesAsync(FilesPath, force: true);

    /// <summary>Opens a directory row, or does nothing for a file.</summary>
    public async Task OpenFolderAsync(FileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsFile)
            await ListFilesAsync(row.Path, force: true);
    }

    /// <summary>Navigates to the parent directory.</summary>
    public async Task NavigateUpAsync()
    {
        if (IsAtFilesRoot)
            return;

        await ListFilesAsync(FileService.Parent(FilesPath), force: true);
    }

    /// <summary>
    /// Downloads <paramref name="row"/> to <paramref name="localPath"/>.
    /// </summary>
    /// <remarks>
    /// The caller has already asked the user where to put it; failures land in the chat log because
    /// the files tab has no place of its own to show them without hiding the list.
    /// </remarks>
    public async Task DownloadFileAsync(FileRow row, string localPath)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        if (!row.IsFile)
            return;

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
            return;

        FilesStatus = $"正在下载 {row.Name}…";
        try
        {
            var outcome = await files.DownloadAsync(channelId, row.Path, localPath);
            FilesStatus = outcome.Ok ? $"已保存 {row.Name}。" : string.Empty;
            if (!outcome.Ok)
                AppendSystemMessage(outcome.Message);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Downloading {Path} failed.", row.Path);
            FilesStatus = string.Empty;
            AppendSystemMessage($"下载失败：{ex.Message}");
        }
    }

    /// <summary>Uploads a local file into the directory the files tab is showing.</summary>
    public async Task UploadFileAsync(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
            return;

        string directory = FilesPath;
        FilesStatus = "正在上传…";
        try
        {
            var outcome = await files.UploadAsync(channelId, directory, localPath);
            if (!outcome.Ok)
            {
                FilesStatus = string.Empty;
                AppendSystemMessage(outcome.Message);
                return;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Uploading {Local} failed.", localPath);
            FilesStatus = string.Empty;
            AppendSystemMessage($"上传失败：{ex.Message}");
            return;
        }

        await ListFilesAsync(directory, force: true);
    }

    /// <summary>Deletes a file or directory from the channel's file area.</summary>
    public async Task DeleteFileAsync(FileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
            return;

        var outcome = await files.DeleteAsync(channelId, row.Path);
        if (!outcome.Ok)
        {
            AppendSystemMessage(outcome.Message);
            return;
        }

        await ListFilesAsync(FilesPath, force: true);
    }

    /// <summary>Creates a directory inside the one the files tab is showing.</summary>
    public async Task CreateFolderAsync(string name)
    {
        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
            return;

        string directory = FilesPath;
        var outcome = await files.CreateDirectoryAsync(channelId, directory, name);
        if (!outcome.Ok)
        {
            AppendSystemMessage(outcome.Message);
            return;
        }

        await ListFilesAsync(directory, force: true);
    }

    /// <summary>Renames a file or directory in place.</summary>
    public async Task RenameFileAsync(FileRow row, string newName)
    {
        ArgumentNullException.ThrowIfNull(row);

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
            return;

        var outcome = await files.RenameAsync(channelId, row.Path, newName);
        if (!outcome.Ok)
        {
            AppendSystemMessage(outcome.Message);
            return;
        }

        await ListFilesAsync(FilesPath, force: true);
    }

    /// <summary>
    /// Lists the current channel's file area when the files tab needs it.
    /// </summary>
    /// <remarks>
    /// Fire and forget, mirroring <see cref="LoadChannelDescription"/>: opening the tab and moving
    /// to another channel can both ask for it, so the request is guarded and a channel change during
    /// the request is picked up afterwards.
    /// </remarks>
    private void LoadChannelFiles()
    {
        if (!connection.IsConnected)
            return;

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0 || channelId == listedChannelId)
            return;

        _ = ListFilesAsync(FileService.RootPath, force: false);
    }

    /// <summary>
    /// Lists one directory into <see cref="FileRows"/>.
    /// </summary>
    /// <param name="force">
    /// True for a user-initiated navigation or refresh, which must run even if the channel is
    /// already listed. False for the lazy load, which must not repeat itself.
    /// </param>
    private async Task ListFilesAsync(string path, bool force)
    {
        if (filesLoading)
            return;

        if (!connection.IsConnected)
        {
            ClearFiles();
            FilesStatus = "未连接。";
            return;
        }

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0)
        {
            ClearFiles();
            return;
        }

        if (!force && channelId == listedChannelId)
            return;

        filesLoading = true;
        // Recorded up front so the snapshots arriving during the request do not treat what is on
        // screen as belonging to another channel and clear it mid-load.
        listedChannelId = channelId;
        FilesStatus = "正在加载…";
        try
        {
            var outcome = await files.ListAsync(channelId, path);

            // Drop the answer if the user moved on while it was in flight; the retry below fetches
            // the channel they are actually in now.
            if (connection.Snapshot.OwnChannelId != channelId)
                return;

            if (!outcome.Ok || outcome.Value is null)
            {
                // Counted as listed anyway: a refused ftgetfilelist will keep being refused, and the
                // tab would otherwise retry on every snapshot.
                FileRows = ImmutableArray<FileRow>.Empty;
                SelectedFile = null;
                FilesPath = FileService.Normalize(path);
                FilesStatus = outcome.Message is { Length: > 0 } message ? message : "无法读取文件列表。";
                return;
            }

            FilesPath = FileService.Normalize(path);
            FileRows = BuildFileRows(outcome.Value);
            SelectedFile = null;
            FilesStatus = FileRows.IsDefaultOrEmpty ? "这个文件夹是空的。" : string.Empty;
        }
        catch (Exception ex)
        {
            // The file area is a side panel; never let it take the shell down.
            log.LogWarning(ex, "Listing {Path} of channel {Cid} failed.", path, channelId);
            FilesStatus = "无法读取文件列表。";
        }
        finally
        {
            filesLoading = false;
        }

        // A channel change during the request could not start its own listing because of the
        // re-entry guard, so pick it up here rather than waiting for the next snapshot.
        if (ActiveTab == ChatPanelTab.Files)
            LoadChannelFiles();
    }

    private async Task ConnectToAsync(
        string address,
        string nickname,
        string serverPassword,
        string bookmarkId,
        BookmarkEntry? bookmark = null)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var (_, stored) = await identityStore.GetOrCreateDefaultAsync();
            var identity = identityStore.Unprotect(stored);

            string effectiveNickname = string.IsNullOrWhiteSpace(nickname) ? settings.Nickname : nickname;

            var request = bookmark is not null
                ? ConnectionRequest.FromBookmark(bookmark, identity, settings.Nickname)
                : new ConnectionRequest
                {
                    Address = address,
                    Identity = identity,
                    Nickname = effectiveNickname,
                    ServerPassword = serverPassword,
                    BookmarkId = bookmarkId,
                };

            Messages.Clear();
            OnPropertyChanged(nameof(HasMessages));
            treeState.Reset();

            string? error = await connection.ConnectAsync(request);
            if (error is not null)
            {
                log.LogWarning("Connecting to {Address} failed: {Error}", request.Address, error);
                AppendSystemMessage(error);
            }
        }
        catch (Exception ex) when (ex is IdentityStoreException or ArgumentException)
        {
            log.LogError(ex, "Could not start a connection to {Address}.", address);
            StatusText = ex.Message;
            AppendSystemMessage(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PushMuteStateAsync(string field, bool muted)
    {
        if (!connection.IsConnected)
            return;

        var result = await connection.ExecuteAsync(c => c.SendVoid(new TsCommand("clientupdate")
        {
            { field, muted },
        }));

        if (!result.Ok)
            log.LogWarning("Updating {Field} failed: {Error}", field, result.Error?.Message);
    }

    private async Task PushAwayStateAsync(bool away)
    {
        if (!connection.IsConnected)
            return;

        var command = new TsCommand("clientupdate") { { "client_away", away } };
        if (away)
            command.Add("client_away_message", "AFK");

        var result = await connection.ExecuteAsync(c => c.SendVoid(command));
        if (!result.Ok)
            log.LogWarning("Updating the away flag failed: {Error}", result.Error?.Message);
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        StatusText = e.Current switch
        {
            ConnectionState.Connecting => $"正在连接 {e.Detail}…",
            ConnectionState.Connected => $"已连接 {ServerName}",
            ConnectionState.Disconnecting => "正在断开…",
            ConnectionState.Reconnecting => e.HasDetail ? $"正在重连（{e.Detail}）" : "正在重连…",
            ConnectionState.Failed => e.HasDetail ? e.Detail : "连接失败",
            _ => "未连接",
        };

        if (e.Current is ConnectionState.Disconnected or ConnectionState.Failed)
        {
            Channels.Clear();
            treeState.Reset();
            ClearInfo();
        }

        if (e.Current == ConnectionState.Failed && e.HasDetail)
            AppendSystemMessage(e.Detail);

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsConnecting));
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void OnSnapshotChanged(object? sender, ServerSnapshot snapshot)
    {
        RebuildTree(snapshot);
        PrefetchIcons(snapshot);
        RebuildInfo(snapshot);

        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(ServerIconId));
        OnPropertyChanged(nameof(ServerAddress));
        OnPropertyChanged(nameof(ClientCount));
        OnPropertyChanged(nameof(MaxClients));
        OnPropertyChanged(nameof(ServerOccupancyText));
        OnPropertyChanged(nameof(HasServerBanner));
        OnPropertyChanged(nameof(ServerBannerUrl));
        OnPropertyChanged(nameof(OwnNickname));
        OnPropertyChanged(nameof(OwnChannelName));
        OnPropertyChanged(nameof(OwnUid));
        OnPropertyChanged(nameof(ChatTitle));
    }

    /// <summary>
    /// Refreshes the info tab from a snapshot.
    /// </summary>
    /// <remarks>
    /// Snapshots arrive whenever anything on the server changes, so the welcome message is only
    /// re-parsed when its text actually differs, and the channel description — which needs a
    /// separate <c>channelinfo</c> round trip — is dropped and re-fetched only when we move to a
    /// different channel.
    /// </remarks>
    private void RebuildInfo(ServerSnapshot snapshot)
    {
        ServerInfoRows = BuildServerRows(snapshot);

        var channel = snapshot.OwnChannel;
        ChannelInfoRows = BuildChannelRows(channel);

        if (!string.Equals(welcomeSource, snapshot.WelcomeMessage, StringComparison.Ordinal))
        {
            welcomeSource = snapshot.WelcomeMessage;
            WelcomeBlocks = Markdown.Parse(snapshot.WelcomeMessage);
        }

        ulong channelId = channel?.ChannelId ?? 0;
        if (channelId == describedChannelId && channelId == listedChannelId)
            return;

        if (channelId != listedChannelId)
        {
            // A different channel means a different file area, so what is on screen is stale.
            ClearFiles();

            if (ActiveTab == ChatPanelTab.Files)
                LoadChannelFiles();
        }

        if (channelId == describedChannelId)
            return;

        describedChannelId = 0;
        ChannelDescriptionBlocks = ImmutableArray<MarkdownNode>.Empty;

        if (ActiveTab == ChatPanelTab.Info)
            LoadChannelDescription();
    }

    /// <summary>
    /// Fetches the current channel's description, which <c>channellist</c> does not carry.
    /// </summary>
    /// <remarks>
    /// Fire and forget, guarded against re-entry: opening the info tab and a snapshot arriving can
    /// both ask for it. A failure leaves the description empty, which the view simply collapses.
    /// </remarks>
    private void LoadChannelDescription()
    {
        if (descriptionLoading || !connection.IsConnected)
            return;

        ulong channelId = connection.Snapshot.OwnChannelId;
        if (channelId == 0 || channelId == describedChannelId)
            return;

        descriptionLoading = true;
        _ = RunAsync(channelId);

        async Task RunAsync(ulong id)
        {
            try
            {
                await FetchAsync(id);
            }
            catch (Exception ex)
            {
                // A missing description is cosmetic; never let it take the shell down.
                log.LogWarning(ex, "Reading the description of channel {Cid} failed.", id);
            }
            finally
            {
                descriptionLoading = false;
            }

            // A channel change during the request could not start its own fetch because of the
            // re-entry guard, so pick it up here rather than waiting for the next snapshot.
            if (ActiveTab == ChatPanelTab.Info)
                LoadChannelDescription();
        }

        async Task FetchAsync(ulong id)
        {
            var outcome = await channels.GetDetailsAsync(id);

            // Record nothing if the user moved on while the request was in flight, so the retry
            // above fetches the channel they are actually in now.
            if (connection.Snapshot.OwnChannelId != id)
                return;

            if (!outcome.Ok || outcome.Value is null)
            {
                // Counted as answered anyway: a refused channelinfo will keep being refused, and
                // snapshots arrive often enough to turn a retry into a request storm.
                log.LogDebug(
                    "Could not read the description of channel {Cid}: {Message}",
                    id,
                    outcome.Message);
                describedChannelId = id;
                return;
            }

            describedChannelId = id;
            ChannelDescriptionBlocks = Markdown.Parse(outcome.Value.Description);
        }
    }

    /// <summary>
    /// Rebuilds the whole tree from the snapshot.
    /// </summary>
    /// <remarks>
    /// A diff would avoid re-creating rows, but the snapshot is coalesced to at most one every
    /// 80 ms and the tree is virtualised, so a rebuild is cheap enough and far easier to get right.
    /// User state survives in <see cref="treeState"/>.
    /// </remarks>
    private void RebuildTree(ServerSnapshot snapshot)
    {
        Channels.Clear();
        foreach (var root in snapshot.Channels)
            Channels.Add(new ChannelViewModel(root, treeState, snapshot));
    }

    /// <summary>
    /// Downloads any channel and server icons that are not in the on-disk cache yet.
    /// </summary>
    /// <remarks>
    /// Fire and forget, and guarded by a flag: snapshots arrive far more often than the prefetch
    /// takes to run, and the download is deliberately serialised inside <see cref="IconService"/>
    /// because the server hands out only a few file transfer slots.
    /// </remarks>
    private void PrefetchIcons(ServerSnapshot snapshot)
    {
        if (prefetchRunning)
            return;

        prefetchRunning = true;
        _ = RunPrefetchAsync(snapshot);

        async Task RunPrefetchAsync(ServerSnapshot current)
        {
            try
            {
                await icons.PrefetchAsync(current);
            }
            catch (Exception ex)
            {
                // Missing icons are cosmetic; never let them take the shell down.
                log.LogWarning(ex, "Icon prefetch failed.");
            }
            finally
            {
                prefetchRunning = false;
            }
        }
    }

    /// <remarks>
    /// The converter caches negative lookups too, so a freshly downloaded icon stays invisible
    /// until the cache entry is dropped and the bindings are asked again. Raising
    /// <see cref="ChannelViewModel.IconId"/> on every row is cheaper than rebuilding the tree and
    /// keeps expansion and selection untouched.
    /// <para>
    /// Touches WPF-bound state, so it relies on <see cref="IconService.IconCached"/> being raised
    /// on the dispatcher rather than on the thread that finished the file transfer.
    /// </para>
    /// </remarks>
    private void OnIconCached(object? sender, IconId e)
    {
        IconIdToImageConverter.Invalidate(e);

        foreach (var item in Channels)
            NotifyIconChanged(item);

        OnPropertyChanged(nameof(ServerIconId));

        static void NotifyIconChanged(ChannelTreeItem item)
        {
            if (item is ChannelViewModel channel)
                channel.NotifyIconChanged();

            foreach (var child in item.Children)
                NotifyIconChanged(child);
        }
    }

    private void RebuildBookmarks()
    {
        Bookmarks.Clear();

        string filter = BookmarkFilter.Trim();
        foreach (var entry in settings.Bookmarks)
        {
            if (filter.Length > 0
                && !entry.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                && !entry.Address.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Bookmarks.Add(new BookmarkViewModel(entry));
        }
    }

    private void OnMessageReceived(object? sender, ChatMessage e) => Append(e);

    private void OnPoked(object? sender, ChatMessage e) => Append(e);

    private void OnServerError(object? sender, string e) => AppendSystemMessage(e);

    private void AppendSystemMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        Append(new ChatMessage
        {
            Target = ChatTarget.Server,
            SenderId = 0,
            SenderName = "服务器",
            Text = text,
        });
    }

    private void Append(ChatMessage message)
    {
        Messages.Add(new MessageViewModel(message, Messages.Count > 0 ? Messages[^1] : null));

        // Keeps memory bounded on a busy server; the official client also does not keep everything.
        const int limit = 500;
        while (Messages.Count > limit)
            Messages.RemoveAt(0);

        OnPropertyChanged(nameof(HasMessages));
    }

    private void PersistSettings()
    {
        // Fire and forget: SettingsStore serialises its own writes, and a failed settings write
        // must not interrupt whatever the user was doing.
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not save settings.");
        }
    }

    partial void OnBookmarkFilterChanged(string value) => RebuildBookmarks();
}

/// <summary>One label/value pair on the chat panel's info tab.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>One entry on the chat panel's files tab.</summary>
/// <param name="Name">Entry name, without any directory part.</param>
/// <param name="Path">Full path inside the channel, which every transfer command takes.</param>
/// <param name="SizeText">Formatted size, or a label for a directory.</param>
/// <param name="ModifiedText">Formatted local modification time, or empty when absent.</param>
public sealed record FileRow(string Name, string Path, string SizeText, string ModifiedText, bool IsFile);
