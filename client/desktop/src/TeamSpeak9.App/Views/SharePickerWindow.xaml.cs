// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Windows;
using TeamSpeak9.App.Controls;
using TeamSpeak9.App.ViewModels;

namespace TeamSpeak9.App.Views;

/// <summary>Screen share target picker dialog.</summary>
public partial class SharePickerWindow : ShellWindow
{
    private readonly SharePickerViewModel viewModel;

    internal SharePickerWindow(SharePickerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        viewModel.Confirmed += OnConfirmed;
        Closed += (_, _) => viewModel.Confirmed -= OnConfirmed;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        DialogResult = true;
    }
}