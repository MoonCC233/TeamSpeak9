// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TeamSpeak9.App.ViewModels;
using TeamSpeak9.Core.Management;

namespace TeamSpeak9.App.Views;

/// <summary>
/// Right column: message list plus the composer.
/// </summary>
public partial class ChatPanelView : UserControl
{
    private INotifyCollectionChanged? subscribed;
    private bool scrollPending;

    public ChatPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (subscribed is not null)
        {
            subscribed.CollectionChanged -= OnMessagesChanged;
            subscribed = null;
        }

        if (e.NewValue is ShellViewModel vm)
        {
            subscribed = vm.Messages;
            subscribed.CollectionChanged += OnMessagesChanged;
        }
    }

    /// <summary>
    /// Keeps the newest message in view.
    /// </summary>
    /// <remarks>
    /// Only auto-scrolls when the list is already at the bottom, so reading back through history is
    /// not interrupted by incoming messages. The decision is made here, while the extent still
    /// describes the list without the new row, but the scroll itself is deferred: see
    /// <see cref="ScrollToNewest"/>.
    /// </remarks>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        var scroller = FindScrollViewer(MessageList);
        if (scroller is not null)
        {
            // ExtentHeight - ViewportHeight is the maximum offset; a couple of pixels of slack
            // covers the partially visible last row.
            double bottom = scroller.ExtentHeight - scroller.ViewportHeight;
            if (bottom - scroller.VerticalOffset > 24)
                return;
        }

        // Bursts of messages otherwise queue one scroll each; only the last one would matter.
        if (scrollPending)
            return;

        scrollPending = true;
        Dispatcher.BeginInvoke(ScrollToNewest, DispatcherPriority.Background);
    }

    /// <remarks>
    /// <see cref="ItemsControl.ScrollIntoView"/> forces a measure pass on the virtualizing panel.
    /// Calling it straight out of the collection event can beat the ListBox's own handler to it, and
    /// the generator then verifies itself against a count that has not caught up yet, which throws
    /// "ItemsControl is inconsistent with its items source". Running at
    /// <see cref="DispatcherPriority.Background"/> puts the scroll after the generator and layout
    /// have settled.
    /// </remarks>
    private void ScrollToNewest()
    {
        scrollPending = false;

        if (MessageList.Items.Count > 0)
            MessageList.ScrollIntoView(MessageList.Items[^1]);
    }

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        // Shift+Enter is a newline, which AcceptsReturn already handles.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            return;

        // An active IME composition sends Key.ImeProcessed instead, so this cannot swallow the
        // Enter that commits a candidate.
        if (DataContext is not ShellViewModel vm)
            return;

        e.Handled = true;

        if (vm.SendMessageCommand.CanExecute(null))
            vm.SendMessageCommand.Execute(null);
    }

    private void OnFormatClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || tag.Length == 0)
            return;

        string updated = ShellViewModel.ApplyMarkdown(
            Composer.Text,
            Composer.SelectionStart,
            Composer.SelectionLength,
            tag,
            out int caret);

        Composer.Text = updated;
        Composer.CaretIndex = Math.Min(caret, updated.Length);
        Composer.Focus();
    }

    /// <summary>
    /// Opens a clicked link. Routed through the ViewModel so the http(s) check and the logging stay
    /// in one place.
    /// </summary>
    private void OnLinkClicked(object? sender, string url)
    {
        if (DataContext is ShellViewModel vm)
            vm.OpenUrl(url);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    // ===== Files tab =====
    //
    // Click handlers rather than commands: every one of these needs a dialog owned by this
    // window, which a view model must not reach for. The view model keeps the server side.

    private async void OnFilesRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            await vm.RefreshFilesAsync();
    }

    private async void OnFilesUpClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            await vm.NavigateUpAsync();
    }

    private async void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The ListBox raises this for clicks on its background too, where SelectedItem is whatever
        // was selected before, so the hit has to be traced back to a row.
        if (DataContext is not ShellViewModel vm)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        // ContainerFromElement rather than a hand-rolled walk: it climbs the logical tree as well,
        // which a Run inside the name TextBlock needs.
        if (ItemsControl.ContainerFromElement(FileList, source) is not ListBoxItem { DataContext: FileRow row })
            return;

        await vm.OpenFolderAsync(row);
    }

    private async void OnFilesUploadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "上传到频道",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        await vm.UploadFileAsync(dialog.FileName);
    }

    private async void OnFilesDownloadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedFile is not { IsFile: true } row)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "保存文件",
            // The name comes from the server, so it is sanitised before it can reach the dialog.
            FileName = FileService.SanitizeLocalName(row.Name),
            Filter = "所有文件|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        await vm.DownloadFileAsync(row, dialog.FileName);
    }

    private async void OnFilesNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm)
            return;

        var prompt = new TextPromptWindow(
            "新建文件夹",
            "文件夹名称",
            "创建",
            validate: FileService.ValidateName)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() != true)
            return;

        await vm.CreateFolderAsync(prompt.Value);
    }

    private async void OnFilesRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedFile is not { } row)
            return;

        var prompt = new TextPromptWindow(
            "重命名",
            $"将“{row.Name}”重命名为",
            "重命名",
            row.Name,
            FileService.ValidateName)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() != true)
            return;

        if (prompt.Value == row.Name)
            return;

        await vm.RenameFileAsync(row, prompt.Value);
    }

    private async void OnFilesDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedFile is not { } row)
            return;

        // Deleting a directory takes everything under it with it, so the two cases get different
        // warnings.
        string warning = row.IsFile
            ? $"确定删除文件“{row.Name}”？"
            : $"确定删除文件夹“{row.Name}”及其中的全部内容？";

        if (MessageBox.Show(
                Window.GetWindow(this),
                warning,
                "删除",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel)
            != MessageBoxResult.OK)
        {
            return;
        }

        await vm.DeleteFileAsync(row);
    }
}
