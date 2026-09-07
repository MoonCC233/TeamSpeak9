// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.App.ViewModels;
using TeamSpeak9.Core.Model;

namespace TeamSpeak9.App.Tests.ViewModels;

public class MessageViewModelTests
{
    private static readonly DateTimeOffset Noon =
        new(new DateTime(2024, 5, 17, 12, 0, 0, DateTimeKind.Local));

    private static ChatMessage Message(
        ushort senderId = 7,
        string senderName = "阿花",
        ChatTarget target = ChatTarget.Channel,
        string text = "在吗",
        DateTimeOffset? received = null) => new()
        {
            Target = target,
            SenderId = senderId,
            SenderName = senderName,
            SenderUid = "0KAWtL7XmPtvBAoIcgVSZ2/8/wE=",
            Text = text,
            Received = received ?? Noon,
        };

    [Fact]
    public void AMessageIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new MessageViewModel(null!, null));
    }

    [Fact]
    public void TheFirstLineAlwaysKeepsItsHeader()
    {
        var first = new MessageViewModel(Message(), null);

        Assert.False(first.IsMerged);
        Assert.True(first.ShowHeader);
    }

    [Fact]
    public void TheSameClientMergesWithinTheWindow()
    {
        var first = new MessageViewModel(Message(), null);
        var second = new MessageViewModel(
            Message(received: Noon + TimeSpan.FromMinutes(4), text: "?"), first);

        Assert.True(second.IsMerged);
        Assert.False(second.ShowHeader);
    }

    [Fact]
    public void TheWindowBoundaryIsInclusive()
    {
        var first = new MessageViewModel(Message(), null);
        var onTheEdge = new MessageViewModel(Message(received: Noon + MessageViewModel.MergeWindow), first);
        var justPast = new MessageViewModel(
            Message(received: Noon + MessageViewModel.MergeWindow + TimeSpan.FromSeconds(1)), first);

        Assert.True(onTheEdge.IsMerged);
        Assert.False(justPast.IsMerged);
    }

    [Fact]
    public void AMessageThatArrivedBeforeTheOneAboveNeverMerges()
    {
        // Clock changes and reordered notifications can produce a negative gap; treating that as
        // "within five minutes" would hide the header on a line that is not really a continuation.
        var first = new MessageViewModel(Message(), null);
        var earlier = new MessageViewModel(Message(received: Noon - TimeSpan.FromSeconds(1)), first);

        Assert.False(earlier.IsMerged);
    }

    [Fact]
    public void ADifferentClientStartsANewBlock()
    {
        var first = new MessageViewModel(Message(senderId: 7), null);
        var second = new MessageViewModel(Message(senderId: 8, senderName: "阿花"), first);

        Assert.False(second.IsMerged);
    }

    [Theory]
    [InlineData(ChatTarget.Channel, ChatTarget.Server)]
    [InlineData(ChatTarget.Private, ChatTarget.Channel)]
    [InlineData(ChatTarget.Channel, ChatTarget.Poke)]
    public void DifferentKindsOfLineNeverMerge(ChatTarget first, ChatTarget second)
    {
        var above = new MessageViewModel(Message(target: first), null);
        var below = new MessageViewModel(Message(target: second), above);

        Assert.False(below.IsMerged);
    }

    [Fact]
    public void ServerNoticesMergeByName()
    {
        // The server has no client id, so SenderId is 0 for every notice and the name is all there
        // is to go on.
        var first = new MessageViewModel(Message(senderId: 0, senderName: "服务器"), null);
        var same = new MessageViewModel(
            Message(senderId: 0, senderName: "服务器", received: Noon + TimeSpan.FromSeconds(1)), first);
        var other = new MessageViewModel(Message(senderId: 0, senderName: "Server"), first);

        Assert.True(same.IsMerged);
        Assert.False(other.IsMerged);
    }

    [Fact]
    public void AServerNoticeAndAClientLineNeverMerge()
    {
        // Both directions: a client whose nickname happens to match a server notice must not be
        // folded into it, which would make the notice look like it came from that person.
        var notice = new MessageViewModel(Message(senderId: 0, senderName: "阿花"), null);
        var fromClient = new MessageViewModel(Message(senderId: 7, senderName: "阿花"), notice);

        var client = new MessageViewModel(Message(senderId: 7, senderName: "阿花"), null);
        var fromServer = new MessageViewModel(Message(senderId: 0, senderName: "阿花"), client);

        Assert.False(fromClient.IsMerged);
        Assert.False(fromServer.IsMerged);
    }

    [Fact]
    public void TheMergeWindowIsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), MessageViewModel.MergeWindow);
    }

    [Fact]
    public void TheTimestampsAreShownInLocalTime()
    {
        var message = Message(received: new DateTimeOffset(new DateTime(2024, 5, 17, 14, 3, 9, DateTimeKind.Local)));
        var line = new MessageViewModel(message, null);

        Assert.Equal("14:03", line.TimeText);
        Assert.Equal("2024-05-17 14:03:09", line.TimeTooltip);
    }

    [Fact]
    public void ATimestampFromAnotherOffsetIsConvertedFirst()
    {
        var utc = new DateTimeOffset(2024, 5, 17, 6, 3, 0, TimeSpan.Zero);
        var line = new MessageViewModel(Message(received: utc), null);

        Assert.Equal(utc.ToLocalTime().ToString("HH:mm"), line.TimeText);
    }

    [Fact]
    public void PokesAndPrivateMessagesAreFlagged()
    {
        var poke = new MessageViewModel(Message(target: ChatTarget.Poke), null);
        var direct = new MessageViewModel(Message(target: ChatTarget.Private), null);
        var channel = new MessageViewModel(Message(target: ChatTarget.Channel), null);

        Assert.True(poke.IsPoke);
        Assert.False(poke.IsPrivate);
        Assert.True(direct.IsPrivate);
        Assert.False(direct.IsPoke);
        Assert.False(channel.IsPoke);
        Assert.False(channel.IsPrivate);
    }

    [Fact]
    public void TheUnderlyingMessageIsExposedForBinding()
    {
        var message = Message(text: "**粗体**");
        var line = new MessageViewModel(message, null);

        Assert.Same(message, line.Message);
        Assert.Equal("阿花", line.SenderName);
        Assert.Equal("0KAWtL7XmPtvBAoIcgVSZ2/8/wE=", line.SenderUid);
        Assert.Equal("**粗体**", line.Text);
        Assert.Equal(Noon, line.Received);
        Assert.False(line.IsFromServer);
    }

    [Fact]
    public void AMessageWithoutASenderIdCountsAsComingFromTheServer()
    {
        Assert.True(new MessageViewModel(Message(senderId: 0), null).IsFromServer);
    }

    [Fact]
    public void TheTextIsParsedAsMarkdown()
    {
        var line = new MessageViewModel(Message(text: "**粗体**"), null);

        var paragraph = Assert.Single(line.Blocks);
        Assert.Equal(MarkdownNodeKind.Paragraph, paragraph.Kind);

        var bold = Assert.Single(paragraph.Children);
        Assert.Equal(MarkdownNodeKind.Bold, bold.Kind);
        Assert.Equal("粗体", Assert.Single(bold.Children).Text);
    }

    [Fact]
    public void AnEmptyMessageParsesToNoBlocks()
    {
        Assert.Empty(new MessageViewModel(Message(text: string.Empty), null).Blocks);
    }

    [Fact]
    public void TheParsedBlocksAreTheSameInstanceOnEveryRead()
    {
        // The message list virtualises with recycling, so re-parsing on every property read would
        // put the parser on the scroll path.
        var line = new MessageViewModel(Message(text: "# 标题"), null);

        Assert.True(line.Blocks == line.Blocks);
    }
}
