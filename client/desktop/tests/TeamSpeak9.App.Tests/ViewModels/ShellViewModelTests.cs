// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Globalization;
using TeamSpeak9.App.ViewModels;
using TeamSpeak9.Core.Management;
using TeamSpeak9.Core.Model;

namespace TeamSpeak9.App.Tests.ViewModels;

/// <summary>
/// The composer toolbar's Markdown editing, which the official TS6 client uses in place of the
/// BBCode of TeamSpeak 3.
/// </summary>
public class ApplyMarkdownTests
{
    /// <summary>Exactly the tokens <c>ChatPanelView.xaml</c> puts in each button's Tag.</summary>
    public static TheoryData<string> ToolbarTokens =>
    [
        "**", "*", "__", "~~", ShellViewModel.LinkToken, "> ", "- ", "`", "||", "# ",
    ];

    [Theory]
    [MemberData(nameof(ToolbarTokens))]
    public void EveryTokenTheToolbarSendsIsHandled(string token)
    {
        var result = ShellViewModel.ApplyMarkdown("文本", 0, 2, token, out var caret);

        Assert.NotEqual("文本", result);
        Assert.InRange(caret, 0, result.Length);
    }

    [Theory]
    [MemberData(nameof(ToolbarTokens))]
    public void EveryTokenKeepsTheSelectedText(string token)
    {
        // A selected URL is the one case where the text moves into the link target rather than
        // staying the label, and it is covered separately in InsertLinkTests.
        var result = ShellViewModel.ApplyMarkdown("文本", 0, 2, token, out _);

        Assert.Contains("文本", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ADelimiterWraps()
    {
        var result = ShellViewModel.ApplyMarkdown("你好", 0, 2, "**", out var caret);

        Assert.Equal("**你好**", result);
        Assert.Equal(result.Length, caret);
    }

    [Fact]
    public void ATrailingSpaceMakesTheTokenABlockMarker()
    {
        var result = ShellViewModel.ApplyMarkdown("你好", 0, 2, "> ", out var caret);

        Assert.Equal("> 你好", result);
        Assert.Equal(result.Length, caret);
    }

    [Fact]
    public void TheLinkTokenBuildsALink()
    {
        var result = ShellViewModel.ApplyMarkdown("点我", 0, 2, ShellViewModel.LinkToken, out _);

        Assert.Equal("[点我]()", result);
    }

    [Fact]
    public void NoTextIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShellViewModel.ApplyMarkdown(null!, 0, 0, "**", out _));
    }

    [Fact]
    public void NoTokenIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShellViewModel.ApplyMarkdown("abc", 0, 0, null!, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTokenIsRejected(string token)
    {
        Assert.Throws<ArgumentException>(
            () => ShellViewModel.ApplyMarkdown("abc", 0, 0, token, out _));
    }
}

public class WrapTests
{
    [Fact]
    public void AnEmptySelectionInsertsAnEmptyPairAndPutsTheCaretInside()
    {
        var result = ShellViewModel.Wrap("你好", 2, 0, "**", out var caret);

        Assert.Equal("你好****", result);
        Assert.Equal(4, caret);
        Assert.Equal("**", result[caret..]);
    }

    [Fact]
    public void ASelectionIsWrappedAndTheCaretLandsAfterTheClosingDelimiter()
    {
        var result = ShellViewModel.Wrap("你好世界", 1, 2, "*", out var caret);

        Assert.Equal("你*好世*界", result);
        Assert.Equal(5, caret);
        Assert.Equal("界", result[caret..]);
    }

    [Fact]
    public void AnEmptyComposerStillProducesADelimiterPair()
    {
        var result = ShellViewModel.Wrap(string.Empty, 0, 0, "||", out var caret);

        Assert.Equal("||||", result);
        Assert.Equal(2, caret);
    }

    [Fact]
    public void TheWholeTextCanBeWrapped()
    {
        var result = ShellViewModel.Wrap("code", 0, 4, "`", out var caret);

        Assert.Equal("`code`", result);
        Assert.Equal(result.Length, caret);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 0)]
    public void AnOutOfRangeSelectionIsClamped(int selectionStart, int selectionLength)
    {
        // The composer reports its own selection, and a stale value can arrive after the text has
        // already been replaced; clamping keeps that from throwing in a click handler.
        var result = ShellViewModel.Wrap("abc", selectionStart, selectionLength, "**", out var caret);

        Assert.Contains("****", result, StringComparison.Ordinal);
        Assert.Contains("abc", result, StringComparison.Ordinal);
        Assert.InRange(caret, 0, result.Length);
    }

    [Fact]
    public void ASelectionRunningPastTheEndIsTruncated()
    {
        var result = ShellViewModel.Wrap("abc", 1, 99, "**", out var caret);

        Assert.Equal("a**bc**", result);
        Assert.Equal(result.Length, caret);
    }

    [Fact]
    public void ANegativeSelectionLengthCollapsesToAnInsert()
    {
        var result = ShellViewModel.Wrap("abc", 1, -4, "**", out var caret);

        Assert.Equal("a****bc", result);
        Assert.Equal(3, caret);
    }

    [Fact]
    public void WrappingTwiceNests()
    {
        var once = ShellViewModel.Wrap("hi", 0, 2, "**", out _);
        var twice = ShellViewModel.Wrap(once, 0, once.Length, "*", out var caret);

        Assert.Equal("***hi***", twice);
        Assert.Equal(twice.Length, caret);
    }

    [Fact]
    public void NoTextIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShellViewModel.Wrap(null!, 0, 0, "**", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDelimiterIsRejected(string delimiter)
    {
        Assert.Throws<ArgumentException>(
            () => ShellViewModel.Wrap("abc", 0, 0, delimiter, out _));
    }
}

public class PrefixLinesTests
{
    [Fact]
    public void AnEmptySelectionPrefixesTheLineAndKeepsTheCaretInPlace()
    {
        var result = ShellViewModel.PrefixLines("abc", 2, 0, "> ", out var caret);

        Assert.Equal("> abc", result);
        Assert.Equal(4, caret);
    }

    [Fact]
    public void EveryLineTheSelectionTouchesIsPrefixed()
    {
        var result = ShellViewModel.PrefixLines("一\n二\n三", 0, 3, "- ", out var caret);

        Assert.Equal("- 一\n- 二\n三", result);
        Assert.Equal(7, caret);
    }

    [Fact]
    public void OnlyTheLineTheCaretIsOnIsPrefixed()
    {
        var result = ShellViewModel.PrefixLines("一\n二", 2, 0, "# ", out _);

        Assert.Equal("一\n# 二", result);
    }

    [Fact]
    public void ThePrefixGoesAtTheStartOfTheLineNotAtTheCaret()
    {
        var result = ShellViewModel.PrefixLines("abc", 3, 0, "# ", out _);

        Assert.Equal("# abc", result);
    }

    [Fact]
    public void PrefixingTwiceNests()
    {
        var once = ShellViewModel.PrefixLines("hi", 0, 2, "> ", out _);
        var twice = ShellViewModel.PrefixLines(once, 0, once.Length, "> ", out _);

        Assert.Equal("> > hi", twice);
    }

    [Fact]
    public void AnEmptyComposerBecomesTheMarkerAlone()
    {
        var result = ShellViewModel.PrefixLines(string.Empty, 0, 0, "- ", out var caret);

        Assert.Equal("- ", result);
        Assert.Equal(2, caret);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 0)]
    [InlineData(1, 99)]
    [InlineData(1, -4)]
    public void AnOutOfRangeSelectionIsClamped(int selectionStart, int selectionLength)
    {
        var result = ShellViewModel.PrefixLines("abc", selectionStart, selectionLength, "> ", out var caret);

        Assert.Equal("> abc", result);
        Assert.InRange(caret, 0, result.Length);
    }

    [Fact]
    public void NoTextIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShellViewModel.PrefixLines(null!, 0, 0, "> ", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPrefixIsRejected(string prefix)
    {
        Assert.Throws<ArgumentException>(
            () => ShellViewModel.PrefixLines("abc", 0, 0, prefix, out _));
    }
}

public class InsertLinkTests
{
    [Fact]
    public void AnEmptySelectionInsertsAnEmptyLinkWithTheCaretInTheLabel()
    {
        var result = ShellViewModel.InsertLink(string.Empty, 0, 0, out var caret);

        Assert.Equal("[]()", result);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void ASelectionBecomesTheLabelAndTheCaretLandsInTheTarget()
    {
        var result = ShellViewModel.InsertLink("点我", 0, 2, out var caret);

        Assert.Equal("[点我]()", result);
        Assert.Equal(5, caret);
        Assert.Equal(")", result[caret..]);
    }

    [Fact]
    public void ASelectedUrlBecomesTheTargetAndTheCaretLandsInTheLabel()
    {
        var result = ShellViewModel.InsertLink("https://teamspeak.com/", 0, 22, out var caret);

        Assert.Equal("[](https://teamspeak.com/)", result);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void ASelectionThatOnlyLooksLikeAUrlStaysTheLabel()
    {
        // Anything Markdown.IsSafeUrl rejects is not offered as a clickable target either, so it
        // has to be treated as ordinary text here too.
        var result = ShellViewModel.InsertLink("javascript:alert(1)", 0, 19, out _);

        Assert.Equal("[javascript:alert(1)]()", result);
    }

    [Fact]
    public void TheSurroundingTextIsPreserved()
    {
        var result = ShellViewModel.InsertLink("前点我后", 1, 2, out _);

        Assert.Equal("前[点我]()后", result);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 0)]
    [InlineData(1, 99)]
    [InlineData(1, -4)]
    public void AnOutOfRangeSelectionIsClamped(int selectionStart, int selectionLength)
    {
        var result = ShellViewModel.InsertLink("abc", selectionStart, selectionLength, out var caret);

        Assert.Contains("](", result, StringComparison.Ordinal);
        Assert.InRange(caret, 0, result.Length);
    }

    [Fact]
    public void NoTextIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShellViewModel.InsertLink(null!, 0, 0, out _));
    }
}

/// <summary>The info tab's server card.</summary>
public class BuildServerRowsTests
{
    private static ServerSnapshot Sample() => new()
    {
        Name = "测试服务器",
        Nickname = "我",
        Address = "ts.example.com:9987",
        Version = "6.0.0-beta12.1",
        Platform = "Windows",
        ProtocolVersion = 3,
        MaxClients = 32,
        License = ServerLicense.Npl,
        VoiceEncryption = VoiceEncryptionMode.Enabled,
        Created = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
    };

    [Fact]
    public void EveryRowHasBothAValueAndALabel()
    {
        var rows = ShellViewModel.BuildServerRows(Sample());

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.False(string.IsNullOrWhiteSpace(row.Value));
        });
    }

    [Fact]
    public void TheNameAndAddressAreShown()
    {
        var rows = ShellViewModel.BuildServerRows(Sample());

        Assert.Equal("测试服务器", Value(rows, "名称"));
        Assert.Equal("ts.example.com:9987", Value(rows, "地址"));
    }

    [Fact]
    public void TheClientCountIsShownAgainstTheLimit()
    {
        var rows = ShellViewModel.BuildServerRows(Sample());

        Assert.Equal("0 / 32", Value(rows, "在线人数"));
    }

    [Fact]
    public void AServerWithoutAClientLimitShowsJustTheCount()
    {
        // MaxClients is 0 until the server sends it, and "3 / 0" would look like an error.
        var rows = ShellViewModel.BuildServerRows(Sample() with { MaxClients = 0 });

        Assert.Equal("0", Value(rows, "在线人数"));
    }

    [Fact]
    public void FieldsTheServerHasNotSentYetAreSkipped()
    {
        // A fresh connection fills the snapshot in over several updates, and a row with an empty
        // value reads as if the server had no version rather than as if it were still loading.
        var rows = ShellViewModel.BuildServerRows(ServerSnapshot.Empty);

        Assert.DoesNotContain(rows, row => row.Label == "版本");
        Assert.DoesNotContain(rows, row => row.Label == "平台");
        Assert.DoesNotContain(rows, row => row.Label == "语音提示名");
        Assert.DoesNotContain(rows, row => row.Label == "创建时间");
    }

    [Fact]
    public void ThePhoneticNameIsShownWhenTheServerHasOne()
    {
        var rows = ShellViewModel.BuildServerRows(Sample() with { PhoneticName = "测试服" });

        Assert.Equal("测试服", Value(rows, "语音提示名"));
    }

    [Fact]
    public void TheCreationTimeIsShownInLocalTime()
    {
        var snapshot = Sample();
        var rows = ShellViewModel.BuildServerRows(snapshot);

        var expected = snapshot.Created.ToLocalTime();
        Assert.Equal(
            expected.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            Value(rows, "创建时间"));
    }

    [Fact]
    public void ANullSnapshotIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ShellViewModel.BuildServerRows(null!));
    }

    private static string Value(ImmutableArray<InfoRow> rows, string label)
    {
        var row = Assert.Single(rows, r => r.Label == label);
        return row.Value;
    }
}

/// <summary>The info tab's channel card.</summary>
public class BuildChannelRowsTests
{
    private static ChannelNode Sample() => new()
    {
        ChannelId = 7,
        Name = "大厅",
        Topic = "随便聊",
        Kind = ChannelKind.Permanent,
        Codec = AudioCodec.OpusVoice,
        CodecQuality = 6,
        MaxClients = ChannelLimit.Of(16),
        Members =
        [
            new ChannelMember { ClientId = 1, ChannelId = 7, Name = "甲" },
            new ChannelMember { ClientId = 2, ChannelId = 7, Name = "乙" },
        ],
    };

    [Fact]
    public void NotBeingInAChannelYieldsNoRows()
    {
        Assert.Empty(ShellViewModel.BuildChannelRows(null));
    }

    [Fact]
    public void EveryRowHasBothAValueAndALabel()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample());

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.False(string.IsNullOrWhiteSpace(row.Value));
        });
    }

    [Fact]
    public void TheMemberCountIsShownAgainstTheLimit()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample());

        Assert.Equal("2 / 16", Value(rows, "人数"));
    }

    [Fact]
    public void AnUnlimitedChannelSaysSoRatherThanShowingZero()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample() with { MaxClients = ChannelLimit.Unlimited });

        Assert.Equal("2 / 不限", Value(rows, "人数"));
    }

    [Fact]
    public void AChannelWithoutATopicSkipsTheRow()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample() with { Topic = string.Empty });

        Assert.DoesNotContain(rows, row => row.Label == "主题");
    }

    [Fact]
    public void TheDefaultTalkPowerIsNotWorthARow()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample());

        Assert.DoesNotContain(rows, row => row.Label == "所需发言权限");
    }

    [Fact]
    public void ATalkPowerRequirementIsShown()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample() with { NeededTalkPower = 50 });

        Assert.Equal("50", Value(rows, "所需发言权限"));
    }

    [Fact]
    public void AnOrdinaryChannelHasNoStatusRow()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample());

        Assert.DoesNotContain(rows, row => row.Label == "状态");
    }

    [Fact]
    public void TheFlagsAreJoinedIntoOneStatusRow()
    {
        var channel = Sample() with
        {
            IsDefault = true,
            HasPassword = true,
            ForcedSilence = true,
            IsUnencrypted = true,
        };

        var value = Value(ShellViewModel.BuildChannelRows(channel), "状态");

        Assert.Equal("默认频道、需要密码、强制静音、语音未加密", value);
    }

    [Fact]
    public void TheDescriptionIsNotARowBecauseItIsRenderedAsMarkdown()
    {
        var rows = ShellViewModel.BuildChannelRows(Sample() with { Description = "**粗体**" });

        Assert.DoesNotContain(rows, row => row.Value.Contains("粗体", StringComparison.Ordinal));
    }

    private static string Value(ImmutableArray<InfoRow> rows, string label)
    {
        var row = Assert.Single(rows, r => r.Label == label);
        return row.Value;
    }
}

/// <summary>The info tab's enum labels, which have to cover every value the server can send.</summary>
public class InfoLabelTests
{
    [Theory]
    [InlineData(ServerLicense.NoLicense)]
    [InlineData(ServerLicense.Athp)]
    [InlineData(ServerLicense.Lan)]
    [InlineData(ServerLicense.Npl)]
    [InlineData(ServerLicense.Unknown)]
    public void EveryLicenseHasALabel(ServerLicense license)
    {
        Assert.False(string.IsNullOrWhiteSpace(ShellViewModel.DescribeLicense(license)));
    }

    [Theory]
    [InlineData(VoiceEncryptionMode.Individual)]
    [InlineData(VoiceEncryptionMode.Disabled)]
    [InlineData(VoiceEncryptionMode.Enabled)]
    public void EveryEncryptionModeHasALabel(VoiceEncryptionMode mode)
    {
        Assert.False(string.IsNullOrWhiteSpace(ShellViewModel.DescribeEncryption(mode)));
    }

    [Theory]
    [InlineData(ChannelKind.Temporary)]
    [InlineData(ChannelKind.SemiPermanent)]
    [InlineData(ChannelKind.Permanent)]
    public void EveryChannelKindHasALabel(ChannelKind kind)
    {
        Assert.False(string.IsNullOrWhiteSpace(ShellViewModel.DescribeChannelKind(kind)));
    }

    [Theory]
    [InlineData(AudioCodec.SpeexNarrowband)]
    [InlineData(AudioCodec.SpeexWideband)]
    [InlineData(AudioCodec.SpeexUltraWideband)]
    [InlineData(AudioCodec.CeltMono)]
    [InlineData(AudioCodec.OpusVoice)]
    [InlineData(AudioCodec.OpusMusic)]
    [InlineData(AudioCodec.Raw)]
    public void EveryCodecHasALabel(AudioCodec codec)
    {
        Assert.False(string.IsNullOrWhiteSpace(ShellViewModel.DescribeCodec(codec)));
    }

    [Fact]
    public void AnUnknownEnumValueFallsBackRatherThanThrowing()
    {
        // The server is free to add values, and an info tab must not crash on one.
        Assert.Equal("未知", ShellViewModel.DescribeChannelKind((ChannelKind)99));
        Assert.Equal("未知", ShellViewModel.DescribeCodec((AudioCodec)99));
        Assert.Equal("未知", ShellViewModel.DescribeEncryption((VoiceEncryptionMode)99));
    }

    [Fact]
    public void AnAbsentTimestampFormatsAsNothing()
    {
        // TSLib decodes these from a unix timestamp, so "not sent" arrives as MinValue or the epoch.
        Assert.Empty(ShellViewModel.FormatDate(default));
        Assert.Empty(ShellViewModel.FormatDate(DateTime.UnixEpoch));
    }

    [Fact]
    public void AnUnspecifiedTimestampIsNotShiftedTwice()
    {
        // Only UTC values need converting; a local value is already in the user's zone.
        var local = new DateTime(2024, 5, 6, 7, 8, 0, DateTimeKind.Unspecified);

        Assert.Equal("2024-05-06 07:08", ShellViewModel.FormatDate(local));
    }
}

/// <summary>The byte counts shown in the file tab's size column.</summary>
public class FormatFileSizeTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1UL, "1 B")]
    [InlineData(1023UL, "1023 B")]
    public void ByteCountsAreWholeNumbers(ulong bytes, string expected)
    {
        // A fractional byte count would only be noise.
        Assert.Equal(expected, ShellViewModel.FormatFileSize(bytes));
    }

    [Theory]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1048576UL, "1 MB")]
    [InlineData(1073741824UL, "1 GB")]
    [InlineData(1099511627776UL, "1 TB")]
    public void LargerCountsScaleToTheNextUnit(ulong bytes, string expected)
    {
        Assert.Equal(expected, ShellViewModel.FormatFileSize(bytes));
    }

    [Fact]
    public void TheScaledValueKeepsAtMostTwoDecimals()
    {
        Assert.Equal("1.51 KB", ShellViewModel.FormatFileSize(1546));
    }

    [Fact]
    public void TheLargestUnitIsNotExceeded()
    {
        // ulong.MaxValue is ~16 EB; the unit table stops at TB, so it must stay there rather than
        // walking off the end of the array.
        Assert.EndsWith(" TB", ShellViewModel.FormatFileSize(ulong.MaxValue), StringComparison.Ordinal);
    }
}

/// <summary>The file tab's rows, built from what <c>FileService</c> listed.</summary>
public class BuildFileRowsTests
{
    private static ChannelFileEntry Entry(
        string name,
        bool isFile,
        ulong size = 0,
        DateTime modified = default,
        string directory = "/") => new()
        {
            Name = name,
            Directory = directory,
            Size = size,
            Modified = modified,
            IsFile = isFile,
        };

    [Fact]
    public void AnEmptyListingProducesNoRows()
    {
        Assert.Empty(ShellViewModel.BuildFileRows([]));
    }

    [Fact]
    public void ADirectoryShowsTheWordFolderInsteadOfASize()
    {
        // The server reports 0 bytes for a directory, and "0 B" would read as an empty file.
        var row = Assert.Single(ShellViewModel.BuildFileRows([Entry("logs", isFile: false)]));

        Assert.Equal("文件夹", row.SizeText);
        Assert.False(row.IsFile);
    }

    [Fact]
    public void AFileShowsItsFormattedSize()
    {
        var row = Assert.Single(ShellViewModel.BuildFileRows([Entry("a.txt", isFile: true, size: 2048)]));

        Assert.Equal("2 KB", row.SizeText);
        Assert.True(row.IsFile);
    }

    [Fact]
    public void EachRowCarriesTheFullChannelPathSoCommandsCanUseItDirectly()
    {
        var row = Assert.Single(ShellViewModel.BuildFileRows(
            [Entry("a.txt", isFile: true, directory: "/logs")]));

        Assert.Equal("a.txt", row.Name);
        Assert.Equal("/logs/a.txt", row.Path);
    }

    [Fact]
    public void TheModifiedColumnUsesTheSameFormatAsTheRestOfTheUi()
    {
        var modified = new DateTime(2024, 5, 6, 7, 8, 0, DateTimeKind.Unspecified);

        var row = Assert.Single(ShellViewModel.BuildFileRows([Entry("a.txt", isFile: true, modified: modified)]));

        Assert.Equal("2024-05-06 07:08", row.ModifiedText);
    }

    [Fact]
    public void AnAbsentTimestampLeavesTheColumnBlank()
    {
        var row = Assert.Single(ShellViewModel.BuildFileRows([Entry("a.txt", isFile: true)]));

        Assert.Empty(row.ModifiedText);
    }

    [Fact]
    public void TheOrderTheServiceSortedIntoIsPreserved()
    {
        // FileService.Sort already put directories first; re-ordering here would undo that.
        var rows = ShellViewModel.BuildFileRows(
        [
            Entry("logs", isFile: false),
            Entry("b.txt", isFile: true),
            Entry("a.txt", isFile: true),
        ]);

        Assert.Equal(["logs", "b.txt", "a.txt"], rows.Select(r => r.Name));
    }
}
