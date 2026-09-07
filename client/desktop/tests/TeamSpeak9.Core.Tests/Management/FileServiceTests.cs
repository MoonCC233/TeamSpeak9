// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Management;
using TSLib;
using TSLib.Messages;

namespace TeamSpeak9.Core.Tests.Management;

public class FileServiceNormalizeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData(".")]
    [InlineData("/./")]
    public void NothingMeaningfulBecomesTheRoot(string? path)
    {
        Assert.Equal("/", FileService.Normalize(path));
    }

    [Theory]
    [InlineData("logs", "/logs")]
    [InlineData("/logs", "/logs")]
    [InlineData("/logs/", "/logs")]
    [InlineData("//logs//old//", "/logs/old")]
    [InlineData(@"\logs\old", "/logs/old")]
    [InlineData("/logs/./old", "/logs/old")]
    public void SeparatorsAndEmptySegmentsAreCollapsed(string input, string expected)
    {
        Assert.Equal(expected, FileService.Normalize(input));
    }

    [Fact]
    public void DotDotPopsOneSegment()
    {
        Assert.Equal("/logs", FileService.Normalize("/logs/old/.."));
    }

    [Theory]
    [InlineData("/..")]
    [InlineData("/../..")]
    [InlineData("/logs/../..")]
    [InlineData(@"..\..\windows")]
    public void DotDotCanNeverClimbAboveTheRoot(string input)
    {
        // A crafted server reply must not be able to address another channel's area.
        string result = FileService.Normalize(input);

        Assert.DoesNotContain("..", result, StringComparison.Ordinal);
        Assert.StartsWith("/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ClimbingPastTheRootStillLeavesTheRemainingSegments()
    {
        Assert.Equal("/windows", FileService.Normalize("/../../windows"));
    }
}

public class FileServiceParentTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("/", "/")]
    [InlineData("/report.txt", "/")]
    [InlineData("/logs", "/")]
    [InlineData("/logs/old", "/logs")]
    [InlineData("/logs/old/a.txt", "/logs/old")]
    public void TheParentOfTheRootIsTheRoot(string? path, string expected)
    {
        Assert.Equal(expected, FileService.Parent(path));
    }

    [Fact]
    public void ATrailingSlashDoesNotShiftTheResult()
    {
        Assert.Equal("/logs", FileService.Parent("/logs/old/"));
    }
}

public class FileServiceValidateNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefused(string? name)
    {
        Assert.NotNull(FileService.ValidateName(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("  ..  ")]
    public void TraversalNamesAreRefused(string name)
    {
        Assert.NotNull(FileService.ValidateName(name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("/")]
    public void SeparatorsAreRefusedSoOneNameCannotBecomeAPath(string name)
    {
        Assert.NotNull(FileService.ValidateName(name));
    }

    [Fact]
    public void ControlCharactersAreRefused()
    {
        Assert.NotNull(FileService.ValidateName("a\nb"));
        Assert.NotNull(FileService.ValidateName("a\tb"));
    }

    [Theory]
    [InlineData("report.txt")]
    [InlineData("会议记录.txt")]
    [InlineData("a b.txt")]
    [InlineData("...leading-dots")]
    public void AnOrdinaryNameIsAccepted(string name)
    {
        Assert.Null(FileService.ValidateName(name));
    }
}

public class FileServiceSanitizeLocalNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("..")]
    [InlineData("...")]
    public void AnUnusableNameFallsBackToADefault(string? name)
    {
        Assert.Equal("download", FileService.SanitizeLocalName(name));
    }

    [Fact]
    public void SeparatorsAreStrippedRatherThanEscaped()
    {
        // The server supplies this name, so traversal must not survive into a local path. Leading
        // dots are harmless once the separators are gone, so they are left alone.
        Assert.Equal("....etcpasswd", FileService.SanitizeLocalName("../../etc/passwd"));
    }

    [Fact]
    public void ControlCharactersAreStripped()
    {
        Assert.Equal("ab.txt", FileService.SanitizeLocalName("a\u0000b\n.txt"));
    }

    [Fact]
    public void TrailingDotsAreStrippedBecauseWindowsDropsThemSilently()
    {
        Assert.Equal("report", FileService.SanitizeLocalName("report..."));
    }

    [Fact]
    public void AnOrdinaryNameSurvivesUnchanged()
    {
        Assert.Equal("会议记录.txt", FileService.SanitizeLocalName("会议记录.txt"));
    }
}

public class FileServiceCombineTests
{
    [Theory]
    [InlineData("/", "a.txt", "/a.txt")]
    [InlineData("/logs", "a.txt", "/logs/a.txt")]
    [InlineData("/logs/", "a.txt", "/logs/a.txt")]
    [InlineData(null, "a.txt", "/a.txt")]
    public void ANameIsAppendedWithExactlyOneSeparator(string? directory, string name, string expected)
    {
        Assert.Equal(expected, FileService.Combine(directory, name));
    }

    [Theory]
    [InlineData("/logs", null)]
    [InlineData("/logs", "")]
    public void AnEmptyNameLeavesTheDirectory(string directory, string? name)
    {
        Assert.Equal("/logs", FileService.Combine(directory, name));
    }

    [Fact]
    public void TheEntryPathIsBuiltFromItsDirectoryAndName()
    {
        var entry = new ChannelFileEntry
        {
            Name = "a.txt",
            Directory = "/logs",
            IsFile = true,
        };

        Assert.Equal("/logs/a.txt", entry.FullPath);
    }
}

public class FileServiceSortTests
{
    private static ChannelFileEntry Entry(string name, bool isFile) => new()
    {
        Name = name,
        Directory = "/",
        IsFile = isFile,
    };

    [Fact]
    public void DirectoriesComeBeforeFiles()
    {
        var sorted = FileService.Sort(
        [
            Entry("b.txt", isFile: true),
            Entry("a-dir", isFile: false),
        ]);

        Assert.Equal(["a-dir", "b.txt"], sorted.Select(e => e.Name));
    }

    [Fact]
    public void ADirectoryStillSortsFirstEvenWhenItsNameComesLast()
    {
        var sorted = FileService.Sort(
        [
            Entry("a.txt", isFile: true),
            Entry("zzz", isFile: false),
        ]);

        Assert.Equal(["zzz", "a.txt"], sorted.Select(e => e.Name));
    }

    [Fact]
    public void NamesAreOrderedCaseInsensitively()
    {
        var sorted = FileService.Sort(
        [
            Entry("beta", isFile: true),
            Entry("Alpha", isFile: true),
        ]);

        Assert.Equal(["Alpha", "beta"], sorted.Select(e => e.Name));
    }

    [Fact]
    public void NamesDifferingOnlyByCaseKeepADeterministicOrder()
    {
        // The culture-aware pass ties, so the ordinal pass has to break it or the list would
        // reshuffle between refreshes.
        var first = FileService.Sort([Entry("README", isFile: true), Entry("readme", isFile: true)]);
        var second = FileService.Sort([Entry("readme", isFile: true), Entry("README", isFile: true)]);

        Assert.Equal(first.Select(e => e.Name), second.Select(e => e.Name));
    }

    [Fact]
    public void AnEmptyListSortsToAnEmptyList()
    {
        Assert.Empty(FileService.Sort([]));
    }
}

public class FileServiceIsEmptyDirectoryTests
{
    private static CommandError Error(TsErrorCode id) => new() { Id = id, Message = "x" };

    [Fact]
    public void BothCodesSeenForAnEmptyFolderAreTreatedAsEmpty()
    {
        // tsserver 6 was measured to answer with database_empty_result; the error table names
        // file_no_files_available for the same situation.
        Assert.True(FileService.IsEmptyDirectory(Error(TsErrorCode.database_empty_result)));
        Assert.True(FileService.IsEmptyDirectory(Error(TsErrorCode.file_no_files_available)));
    }

    [Fact]
    public void ARealFailureIsNotTreatedAsEmpty()
    {
        Assert.False(FileService.IsEmptyDirectory(Error(TsErrorCode.file_not_found)));
        Assert.False(FileService.IsEmptyDirectory(Error(TsErrorCode.permissions_client_insufficient)));
    }

    [Fact]
    public void NoErrorIsNotAnEmptyDirectory()
    {
        Assert.False(FileService.IsEmptyDirectory(null));
    }
}
