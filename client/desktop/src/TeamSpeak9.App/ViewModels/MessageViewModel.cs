// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using TeamSpeak9.Core.Model;

namespace TeamSpeak9.App.ViewModels;

/// <summary>
/// One rendered chat line.
/// </summary>
/// <remarks>
/// Consecutive messages from the same sender within <see cref="MergeWindow"/> are drawn without
/// repeating the avatar and nickname, matching the official client. Whether a message is merged
/// depends on the one before it, so it is decided when the message is appended and stored here
/// rather than computed in the view.
/// </remarks>
public sealed class MessageViewModel
{
    /// <summary>How long a sender keeps their header block.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(5);

    public MessageViewModel(ChatMessage message, MessageViewModel? previous)
    {
        ArgumentNullException.ThrowIfNull(message);

        Message = message;
        IsMerged = CanMerge(previous, message);
        Blocks = Markdown.Parse(message.Text);
    }

    public ChatMessage Message { get; }

    /// <summary>
    /// The message parsed into Markdown blocks, as the official TS6 client renders chat.
    /// </summary>
    /// <remarks>
    /// Parsed once on construction rather than lazily: the list virtualises with recycling, so a
    /// lazy property would re-parse every time a row scrolls back into view.
    /// </remarks>
    public ImmutableArray<MarkdownNode> Blocks { get; }

    public string SenderName => Message.SenderName;

    /// <summary>Colour key source. Stable across reconnects, unlike the client id.</summary>
    public string SenderUid => Message.SenderUid;

    public string Text => Message.Text;

    public DateTimeOffset Received => Message.Received;

    public bool IsFromServer => Message.IsFromServer;

    public bool IsPoke => Message.Target == ChatTarget.Poke;

    public bool IsPrivate => Message.Target == ChatTarget.Private;

    /// <summary>True when the header block is suppressed because the sender is repeating.</summary>
    public bool IsMerged { get; }

    public bool ShowHeader => !IsMerged;

    /// <summary><c>14:03</c>. Shown in the header, or on hover for merged lines.</summary>
    public string TimeText => Message.Received.ToLocalTime().ToString("HH:mm");

    /// <summary>Full timestamp for the tooltip, where the short form is ambiguous.</summary>
    public string TimeTooltip => Message.Received.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static bool CanMerge(MessageViewModel? previous, ChatMessage message)
    {
        if (previous is null)
            return false;

        // Different kinds of line never merge: a poke and a channel message look nothing alike even
        // when they come from the same person.
        if (previous.Message.Target != message.Target)
            return false;

        // Server notices have no sender id, so fall back to the name.
        bool sameSender = message.IsFromServer || previous.Message.IsFromServer
            ? previous.Message.IsFromServer == message.IsFromServer
                && string.Equals(previous.SenderName, message.SenderName, StringComparison.Ordinal)
            : previous.Message.SenderId == message.SenderId;

        if (!sameSender)
            return false;

        var gap = message.Received - previous.Message.Received;
        return gap >= TimeSpan.Zero && gap <= MergeWindow;
    }
}
