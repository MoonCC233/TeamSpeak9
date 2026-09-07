// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

namespace TeamSpeak9.Core.Model;

/// <summary>Who a text message was addressed to.</summary>
public enum ChatTarget
{
    /// <summary>A direct message between two clients.</summary>
    Private = 1,

    /// <summary>Sent to everyone in the sender's channel.</summary>
    Channel = 2,

    /// <summary>Sent to everyone on the virtual server.</summary>
    Server = 3,

    /// <summary>A poke. Rendered separately from the chat tabs.</summary>
    Poke = 100,
}

/// <summary>
/// One received chat message or poke.
/// </summary>
/// <remarks>
/// <para>
/// The server does not timestamp text messages, so <see cref="Received"/> is the local arrival
/// time. That is what the official client displays too.
/// </para>
/// <para>
/// <see cref="Text"/> is the raw message and may contain Markdown markup (<c>**bold**</c>,
/// <c>[label](url)</c>, …) as well as TeamSpeak escape sequences already undone by TSLib. Parsing it
/// with <see cref="Markdown.Parse"/> and rendering it is the UI's job.
/// </para>
/// </remarks>
public sealed record ChatMessage
{
    public required ChatTarget Target { get; init; }

    /// <summary>Sender's runtime client id. 0 when the server itself is the sender.</summary>
    public ushort SenderId { get; init; }

    public required string SenderName { get; init; }

    /// <summary>Sender's permanent identity. Empty when the server is the sender.</summary>
    public string SenderUid { get; init; } = string.Empty;

    public required string Text { get; init; }

    /// <summary>
    /// For <see cref="ChatTarget.Private"/>, the client the message was addressed to.
    /// </summary>
    /// <remarks>
    /// Needed to tell an outgoing echo from an incoming message: for a message we sent, this is the
    /// peer, and <see cref="SenderId"/> is us.
    /// </remarks>
    public ushort TargetClientId { get; init; }

    /// <summary>Local arrival time; the protocol carries no timestamp.</summary>
    public DateTimeOffset Received { get; init; } = DateTimeOffset.Now;

    /// <summary>True when this is a server-generated message rather than one from a client.</summary>
    public bool IsFromServer => SenderId == 0;

    public static ChatMessage FromNotification(TSLib.Messages.TextMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ChatMessage
        {
            Target = (ChatTarget)(int)message.Target,
            SenderId = message.InvokerId.Value,
            SenderName = message.InvokerName ?? string.Empty,
            SenderUid = message.InvokerUid?.Value ?? string.Empty,
            Text = message.Message ?? string.Empty,
            TargetClientId = message.TargetClientId?.Value ?? 0,
        };
    }

    public static ChatMessage FromPoke(TSLib.Messages.ClientPoke poke)
    {
        ArgumentNullException.ThrowIfNull(poke);

        return new ChatMessage
        {
            Target = ChatTarget.Poke,
            SenderId = poke.InvokerId.Value,
            SenderName = poke.InvokerName ?? string.Empty,
            SenderUid = poke.InvokerUid?.Value ?? string.Empty,
            Text = poke.Message ?? string.Empty,
        };
    }

    public override string ToString() => $"[{Target}] {SenderName}: {Text}";
}
