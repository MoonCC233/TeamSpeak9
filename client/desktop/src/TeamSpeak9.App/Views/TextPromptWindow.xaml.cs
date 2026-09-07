// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Windows;
using System.Windows.Controls;
using TeamSpeak9.App.Controls;

namespace TeamSpeak9.App.Views;

/// <summary>
/// One-line text input dialog, for "new folder" and "rename".
/// </summary>
/// <remarks>
/// Deliberately without a view model: there is no server round trip and no state worth keeping, so
/// the two labels and the validator are passed straight to the constructor. <see cref="Validate"/>
/// runs on every keystroke so the accept button reflects the same rule the caller will apply.
/// </remarks>
public partial class TextPromptWindow : ShellWindow
{
    private readonly Func<string, string?>? validate;

    internal TextPromptWindow(
        string title,
        string prompt,
        string acceptLabel,
        string initialValue = "",
        Func<string, string?>? validate = null)
    {
        this.validate = validate;

        InitializeComponent();

        Title = title;
        TitleLabel.Text = title;
        PromptLabel.Text = prompt;
        AcceptButton.Content = acceptLabel;
        ValueBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            // Selecting the stem rather than everything: renaming usually keeps the extension.
            int dot = initialValue.LastIndexOf('.');
            if (dot > 0)
                ValueBox.Select(0, dot);
            else
                ValueBox.SelectAll();
        };

        Revalidate();
    }

    /// <summary>The trimmed value the user accepted.</summary>
    public string Value => ValueBox.Text.Trim();

    private void OnValueChanged(object sender, TextChangedEventArgs e) => Revalidate();

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (Revalidate())
            DialogResult = true;
    }

    private bool Revalidate()
    {
        string? complaint = validate?.Invoke(ValueBox.Text.Trim());

        // Only complain once the user has typed something: an empty box on open is expected, and a
        // red "cannot be empty" the moment the dialog appears reads like a failure.
        ErrorLabel.Text = ValueBox.Text.Length == 0 ? string.Empty : complaint ?? string.Empty;
        AcceptButton.IsEnabled = complaint is null;
        return complaint is null;
    }
}
