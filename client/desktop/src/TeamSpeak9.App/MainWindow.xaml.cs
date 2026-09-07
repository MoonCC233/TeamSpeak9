// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TeamSpeak9.App.Controls;
using TeamSpeak9.App.ViewModels;
using TeamSpeak9.App.Views;

namespace TeamSpeak9.App;

public partial class MainWindow : ShellWindow
{
    /// <summary>Opens the theme gallery, bound to <c>Ctrl+Shift+T</c>.</summary>
    public static readonly RoutedUICommand OpenThemeGalleryCommand =
        new("主题预览", nameof(OpenThemeGalleryCommand), typeof(MainWindow));

    /// <summary>
    /// Creates a channel. The parameter is the parent <see cref="ChannelViewModel"/>, or null for a
    /// root channel.
    /// </summary>
    public static readonly RoutedUICommand CreateChannelCommand =
        new("创建频道", nameof(CreateChannelCommand), typeof(MainWindow));

    /// <summary>Edits the channel passed as the parameter.</summary>
    public static readonly RoutedUICommand EditChannelCommand =
        new("编辑频道", nameof(EditChannelCommand), typeof(MainWindow));

    /// <summary>Deletes the channel passed as the parameter.</summary>
    public static readonly RoutedUICommand DeleteChannelCommand =
        new("删除频道", nameof(DeleteChannelCommand), typeof(MainWindow));

    /// <summary>Makes the channel passed as the parameter the server's default channel.</summary>
    public static readonly RoutedUICommand SetDefaultChannelCommand =
        new("设为默认频道", nameof(SetDefaultChannelCommand), typeof(MainWindow));

    /// <summary>Opens the icon browser for the channel passed as the parameter.</summary>
    public static readonly RoutedUICommand ChannelIconCommand =
        new("频道图标", nameof(ChannelIconCommand), typeof(MainWindow));

    /// <summary>Opens the virtual server editor.</summary>
    public static readonly RoutedUICommand EditServerCommand =
        new("服务器设置", nameof(EditServerCommand), typeof(MainWindow));

        /// <summary>Starts screen sharing.</summary>
        public static readonly RoutedUICommand ShareScreenCommand =
            new("开始直播", nameof(ShareScreenCommand), typeof(MainWindow));

        private readonly ShellViewModel? shell;
    private ThemeGalleryWindow? gallery;

    /// <summary>Design-time and XAML-loader constructor.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AddCommandBindings();
    }

    internal MainWindow(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        this.shell = shell;

        InitializeComponent();
        AddCommandBindings();

        DataContext = shell;

        // Column widths, the window rect and the chat panel are driven imperatively rather than by
        // binding: GridSplitter assigns ColumnDefinition.Width as a local value, which would blow
        // away any binding set on it, and the window rect has to survive a maximize round trip.
        SidebarColumn.Width = new GridLength(shell.SidebarWidth);
        ChannelColumn.Width = new GridLength(shell.ChannelPanelWidth);
        RestoreWindowState();
        ApplyChatPanelVisibility();

        shell.PropertyChanged += OnShellPropertyChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>
    /// Applies the persisted window size, clamped to the virtual screen.
    /// </summary>
    /// <remarks>
    /// A saved size from a larger or differently arranged monitor set would otherwise open the
    /// window bigger than anything currently attached.
    /// </remarks>
    private void RestoreWindowState()
    {
        if (shell is null)
            return;

        double maxWidth = SystemParameters.VirtualScreenWidth;
        double maxHeight = SystemParameters.VirtualScreenHeight;

        if (shell.WindowWidth >= MinWidth && maxWidth > 0)
            Width = Math.Min(shell.WindowWidth, maxWidth);

        if (shell.WindowHeight >= MinHeight && maxHeight > 0)
            Height = Math.Min(shell.WindowHeight, maxHeight);

        if (shell.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void AddCommandBindings()
    {
        CommandBindings.Add(new CommandBinding(OpenThemeGalleryCommand, (_, _) => OpenThemeGallery()));
        CommandBindings.Add(new CommandBinding(
            CreateChannelCommand,
            (_, e) => OpenChannelEditor(e.Parameter as ChannelViewModel, create: true),
            (_, e) => e.CanExecute = shell?.IsConnected == true));
        CommandBindings.Add(new CommandBinding(
            EditChannelCommand,
            (_, e) => OpenChannelEditor(e.Parameter as ChannelViewModel, create: false),
            (_, e) => e.CanExecute = e.Parameter is ChannelViewModel));
        CommandBindings.Add(new CommandBinding(
            DeleteChannelCommand,
            (_, e) => DeleteChannel(e.Parameter as ChannelViewModel),
            (_, e) => e.CanExecute = e.Parameter is ChannelViewModel));
        CommandBindings.Add(new CommandBinding(
            SetDefaultChannelCommand,
            (_, e) => SetDefaultChannel(e.Parameter as ChannelViewModel),
            (_, e) => e.CanExecute = e.Parameter is ChannelViewModel));
        CommandBindings.Add(new CommandBinding(
            ChannelIconCommand,
            (_, e) => OpenIconBrowser(e.Parameter as ChannelViewModel),
            (_, e) => e.CanExecute = e.Parameter is ChannelViewModel));
        CommandBindings.Add(new CommandBinding(
            EditServerCommand,
            (_, _) => OpenServerEditor(),
            (_, e) => e.CanExecute = shell?.IsConnected == true));
                CommandBindings.Add(new CommandBinding(
                    ShareScreenCommand,
                    async (_, _) => await shell?.StartScreenShareAsync()!,
                    (_, e) => e.CanExecute = shell?.IsConnected == true && shell?.ScreenShareService?.IsSupported == true));
            }

    private void OpenThemeGallery()
    {
        if (gallery is not null)
        {
            gallery.Activate();
            return;
        }

        gallery = new ThemeGalleryWindow { Owner = this };
        gallery.Closed += (_, _) => gallery = null;
        gallery.Show();
    }

    /// <remarks>
    /// Dialogs are resolved from the container rather than constructed directly, so their view
    /// models get the same service instances the shell uses. <c>Owner</c> matters beyond z-order:
    /// <c>ShutdownMode="OnMainWindowClose"</c> means an unowned dialog would keep running after the
    /// main window closed.
    /// </remarks>
    private T CreateDialog<T>() where T : Window
    {
        var dialog = ((App)Application.Current).Services.GetRequiredService<T>();
        dialog.Owner = this;
        return dialog;
    }

    private async void OpenChannelEditor(ChannelViewModel? channel, bool create)
    {
        var dialog = CreateDialog<ChannelEditorWindow>();
        var vm = (ChannelEditorViewModel)dialog.DataContext;

        if (create)
            await vm.LoadForCreateAsync(channel?.Node);
        else if (channel is not null)
            await vm.LoadForEditAsync(channel.Node);
        else
            return;

        dialog.ShowDialog();
    }

    private async void OpenIconBrowser(ChannelViewModel? channel)
    {
        if (channel is null)
            return;

        var dialog = CreateDialog<IconBrowserWindow>();
        var vm = (IconBrowserViewModel)dialog.DataContext;

        await vm.LoadForChannelAsync(channel.Node);
        dialog.ShowDialog();
    }

    private void OpenServerEditor()
    {
        // Loads its own data in Loaded, because the editor needs two round trips and the dialog
        // should already be visible while they run.
        CreateDialog<ServerEditorWindow>().ShowDialog();
    }

    private async void DeleteChannel(ChannelViewModel? channel)
    {
        if (channel is null || shell is null)
            return;

        // The server refuses a non-empty channel unless force is set, so the prompt has to say what
        // will happen to the people standing in it.
        string warning = channel.MemberCount > 0
            ? $"频道“{channel.Name}”中还有 {channel.MemberCount} 人，删除后他们会被移到默认频道。确定删除？"
            : $"确定删除频道“{channel.Name}”？";

        if (MessageBox.Show(this, warning, "删除频道", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            != MessageBoxResult.OK)
        {
            return;
        }

        await shell.DeleteChannelAsync(channel.ChannelId, force: channel.MemberCount > 0);
    }

    private async void SetDefaultChannel(ChannelViewModel? channel)
    {
        if (channel is null || shell is null)
            return;

        await shell.SetDefaultChannelAsync(channel.ChannelId);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsChatPanelVisible))
            ApplyChatPanelVisibility();
    }

    /// <summary>
    /// Hides the chat column together with its splitter, letting the channel column take the space.
    /// </summary>
    /// <remarks>
    /// The column is zeroed as well as the content collapsed, because a star-sized column keeps its
    /// share of the width even when the child inside it is <c>Collapsed</c>. The channel column then
    /// has to become the star column, otherwise nothing claims the freed width and the window ends
    /// in a blank gap. Its <c>MaxWidth</c> is lifted for the same reason.
    /// </remarks>
    private void ApplyChatPanelVisibility()
    {
        bool visible = shell?.IsChatPanelVisible ?? true;

        ChatPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ChannelSplitter.Visibility = ChatPanel.Visibility;
        ChatColumn.MinWidth = visible ? 360 : 0;
        ChatColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        if (visible)
        {
            ChannelColumn.MaxWidth = 520;
            ChannelColumn.Width = new GridLength(shell?.ChannelPanelWidth ?? 320);
        }
        else
        {
            ChannelColumn.MaxWidth = double.PositiveInfinity;
            ChannelColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void OnSidebarSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (shell is not null)
            shell.SidebarWidth = SidebarColumn.ActualWidth;
    }

    private void OnChannelSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (shell is not null)
            shell.ChannelPanelWidth = ChannelColumn.ActualWidth;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shell is null)
            return;

        // Also captures column widths changed with the splitter's keyboard resizing, which does not
        // raise DragCompleted.
        shell.SidebarWidth = SidebarColumn.ActualWidth;
        if (shell.IsChatPanelVisible)
            shell.ChannelPanelWidth = ChannelColumn.ActualWidth;

        // RestoreBounds is only meaningful while maximized or minimized; otherwise it is empty and
        // the live Width/Height are the restore size.
        var bounds = RestoreBounds;
        bool useRestore = WindowState != WindowState.Normal && !bounds.IsEmpty;

        shell.SaveWindowState(
            useRestore ? bounds.Width : Width,
            useRestore ? bounds.Height : Height,
            WindowState == WindowState.Maximized);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            Closing -= OnClosing;
        }
    }
}
