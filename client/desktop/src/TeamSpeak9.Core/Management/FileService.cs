// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Connection;
using TSLib;
using TSLib.Helper;
using TSLib.Messages;

namespace TeamSpeak9.Core.Management;

/// <summary>One entry in a channel's file area.</summary>
public sealed record ChannelFileEntry
{
    /// <summary>Entry name, without any directory part.</summary>
    public required string Name { get; init; }

    /// <summary>Directory the entry lives in: <c>/</c>, <c>/logs</c>, …</summary>
    public required string Directory { get; init; }

    /// <summary>Size in bytes. The server reports 0 for a directory.</summary>
    public ulong Size { get; init; }

    /// <summary>Last modification time in UTC, or <see cref="DateTime.MinValue"/> when absent.</summary>
    public DateTime Modified { get; init; }

    public required bool IsFile { get; init; }

    /// <summary>Path inside the channel, which is the form every transfer command takes.</summary>
    public string FullPath => FileService.Combine(Directory, Name);
}

/// <summary>
/// Browsing and transferring the files stored in a channel's file area.
/// </summary>
/// <remarks>
/// <para>
/// Every channel has its own namespace on the server, addressed by <c>cid</c> plus a path rooted at
/// <see cref="RootPath"/>. Listing is a normal command, but the transfers themselves run over a
/// separate TCP connection to port 30033 that TSLib opens per transfer; see
/// <c>docs/desktop/tslib-ts6-compat.md</c> §4.7.
/// </para>
/// <para>
/// Paths are normalised before they reach the wire (<see cref="Normalize"/>). Names arriving from
/// the server are treated as untrusted: they are never joined onto a local path without being run
/// through <see cref="SanitizeLocalName"/> first, so a hostile server cannot walk out of the folder
/// the user picked.
/// </para>
/// </remarks>
public sealed class FileService
{
    /// <summary>Root of a channel's file area.</summary>
    public const string RootPath = "/";

    private readonly TsConnection connection;
    private readonly ILogger<FileService> log;

    public FileService(TsConnection connection, ILogger<FileService> log)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(log);

        this.connection = connection;
        this.log = log;
    }

    /// <summary>
    /// Lists one directory of a channel's file area, directories first.
    /// </summary>
    /// <remarks>
    /// An empty directory has no rows to send, and the server answers with an error rather than an
    /// empty list. Two codes were seen for it, so both are folded into an empty result.
    /// </remarks>
    public async Task<CommandOutcome<IReadOnlyList<ChannelFileEntry>>> ListAsync(
        ulong channelId,
        string path = RootPath,
        string channelPassword = "")
    {
        string directory = Normalize(path);

        var result = await connection.ExecuteAsync(
            client => client.FileTransferGetFileList(new ChannelId(channelId), directory, channelPassword),
            R<FileList[], CommandError>.Err(CommandError.ConnectionClosed)).ConfigureAwait(false);

        if (!result.Ok)
        {
            if (IsEmptyDirectory(result.Error))
                return CommandOutcome<IReadOnlyList<ChannelFileEntry>>.Success(ImmutableArray<ChannelFileEntry>.Empty);

            log.LogWarning("列出频道 {Cid} 的 {Path} 失败：{Error}", channelId, directory, result.Error.ErrorFormat());
            return CommandOutcome<IReadOnlyList<ChannelFileEntry>>.Fail(CommandErrorText.Describe(result.Error));
        }

        var entries = new List<ChannelFileEntry>(result.Value.Length);
        foreach (var file in result.Value)
        {
            // The server pads its reply with an empty row when the directory is empty.
            if (string.IsNullOrEmpty(file.Name))
                continue;

            entries.Add(new ChannelFileEntry
            {
                Name = file.Name,
                Directory = directory,
                Size = file.IsFile ? file.Size : 0,
                Modified = file.DateTime,
                IsFile = file.IsFile,
            });
        }

        return CommandOutcome<IReadOnlyList<ChannelFileEntry>>.Success(Sort(entries));
    }

    /// <summary>
    /// Downloads one file into <paramref name="localPath"/>.
    /// </summary>
    /// <remarks>
    /// Written to a sibling temporary file and moved into place, so a failed or aborted transfer
    /// cannot leave a truncated file behind under the name the user chose. The stream is owned here
    /// (<c>closeStream: false</c>) because TSLib only disposes it on a *successful* transfer.
    /// </remarks>
    public async Task<CommandOutcome> DownloadAsync(
        ulong channelId,
        string remotePath,
        string localPath,
        string channelPassword = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        string path = Normalize(remotePath);
        if (path == RootPath)
            return CommandOutcome.Fail("没有选择文件。");

        string temporary = localPath + ".part";

        try
        {
            string? parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent))
                System.IO.Directory.CreateDirectory(parent);

            R<FileTransferToken, CommandError> result;
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                result = await connection.ExecuteAsync(
                    client => client.DownloadFile(
                        stream,
                        new ChannelId(channelId),
                        path,
                        channelPassword,
                        closeStream: false),
                    R<FileTransferToken, CommandError>.Err(CommandError.ConnectionClosed)).ConfigureAwait(false);
            }

            if (!result.Ok)
            {
                log.LogWarning("下载 {Path} 失败：{Error}", path, result.Error.ErrorFormat());
                return CommandOutcome.Fail(CommandErrorText.Describe(result.Error));
            }

            if (result.Value.Status != TransferStatus.Done)
                return CommandOutcome.Fail($"下载未完成（状态：{result.Value.Status}）。");

            File.Move(temporary, localPath, overwrite: true);
            log.LogInformation("已下载 {Path} 到 {Local}", path, localPath);
            return CommandOutcome.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "写入 {Local} 失败。", localPath);
            return CommandOutcome.Fail($"无法写入本地文件：{ex.Message}");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Uploads a local file into <paramref name="directory"/>.</summary>
    /// <param name="overwrite">
    /// When false the server refuses with <c>file_already_exists</c> rather than replacing.
    /// </param>
    /// <remarks>
    /// The stream is owned here rather than handed to TSLib, which only disposes it when the
    /// transfer reaches <see cref="TransferStatus.Done"/>.
    /// </remarks>
    public async Task<CommandOutcome> UploadAsync(
        ulong channelId,
        string directory,
        string localPath,
        bool overwrite = false,
        string channelPassword = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        if (!File.Exists(localPath))
            return CommandOutcome.Fail("本地文件不存在。");

        string name = Path.GetFileName(localPath);
        if (ValidateName(name) is { } invalid)
            return CommandOutcome.Fail(invalid);

        string target = Combine(directory, name);

        try
        {
            R<FileTransferToken, CommandError> result;
            await using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = await connection.ExecuteAsync(
                    client => client.UploadFile(
                        stream,
                        new ChannelId(channelId),
                        target,
                        overwrite,
                        channelPassword,
                        closeStream: false),
                    R<FileTransferToken, CommandError>.Err(CommandError.ConnectionClosed)).ConfigureAwait(false);
            }

            if (!result.Ok)
            {
                log.LogWarning("上传 {Path} 失败：{Error}", target, result.Error.ErrorFormat());
                return CommandOutcome.Fail(CommandErrorText.Describe(result.Error));
            }

            if (result.Value.Status != TransferStatus.Done)
                return CommandOutcome.Fail($"上传未完成（状态：{result.Value.Status}）。");

            log.LogInformation("已上传 {Local} 到 {Path}", localPath, target);
            return CommandOutcome.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "读取 {Local} 失败。", localPath);
            return CommandOutcome.Fail($"无法读取本地文件：{ex.Message}");
        }
    }

    /// <summary>Deletes a file or a whole directory.</summary>
    public async Task<CommandOutcome> DeleteAsync(ulong channelId, string remotePath, string channelPassword = "")
    {
        string path = Normalize(remotePath);
        if (path == RootPath)
            return CommandOutcome.Fail("不能删除频道的根目录。");

        var result = await connection.ExecuteAsync(
            client => client.FileTransferDeleteFile(new ChannelId(channelId), [path], channelPassword))
            .ConfigureAwait(false);

        var outcome = CommandOutcome.From(result);
        if (!outcome.Ok)
            log.LogWarning("删除 {Path} 失败：{Message}", path, outcome.Message);

        return outcome;
    }

    /// <summary>Creates a directory named <paramref name="name"/> inside <paramref name="directory"/>.</summary>
    public async Task<CommandOutcome> CreateDirectoryAsync(
        ulong channelId,
        string directory,
        string name,
        string channelPassword = "")
    {
        if (ValidateName(name) is { } invalid)
            return CommandOutcome.Fail(invalid);

        string path = Combine(directory, name.Trim());

        var result = await connection.ExecuteAsync(
            client => client.FileTransferCreateDirectory(new ChannelId(channelId), path, channelPassword))
            .ConfigureAwait(false);

        var outcome = CommandOutcome.From(result);
        if (!outcome.Ok)
            log.LogWarning("创建目录 {Path} 失败：{Message}", path, outcome.Message);

        return outcome;
    }

    /// <summary>Renames a file or directory in place.</summary>
    public async Task<CommandOutcome> RenameAsync(
        ulong channelId,
        string remotePath,
        string newName,
        string channelPassword = "")
    {
        string path = Normalize(remotePath);
        if (path == RootPath)
            return CommandOutcome.Fail("不能重命名频道的根目录。");

        if (ValidateName(newName) is { } invalid)
            return CommandOutcome.Fail(invalid);

        string target = Combine(Parent(path), newName.Trim());
        if (string.Equals(path, target, StringComparison.Ordinal))
            return CommandOutcome.Success;

        var result = await connection.ExecuteAsync(
            client => client.FileTransferRenameFile(new ChannelId(channelId), path, channelPassword, target))
            .ConfigureAwait(false);

        var outcome = CommandOutcome.From(result);
        if (!outcome.Ok)
            log.LogWarning("重命名 {Path} 失败：{Message}", path, outcome.Message);

        return outcome;
    }

    /// <summary>
    /// Directories first, then by name.
    /// </summary>
    /// <remarks>
    /// The server returns rows in insertion order, which looks arbitrary. Culture-aware comparison
    /// so Chinese names sort the way the rest of the UI does.
    /// </remarks>
    internal static ImmutableArray<ChannelFileEntry> Sort(IEnumerable<ChannelFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            .. entries
                .OrderBy(e => e.IsFile)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// True when a listing error only means "there is nothing in here".
    /// </summary>
    /// <remarks>
    /// <c>database_empty_result</c> is what tsserver 6 was measured to send for the icon directory.
    /// <c>file_no_files_available</c> is the code the error table names for it, so both are accepted
    /// rather than surfacing an alarming message for an empty folder.
    /// </remarks>
    internal static bool IsEmptyDirectory(CommandError? error) => error?.Id is
        TsErrorCode.database_empty_result or
        TsErrorCode.file_no_files_available;

    /// <summary>Checks a name the user typed, before it can reach the wire.</summary>
    /// <returns>A user-facing complaint, or <c>null</c> when the name is usable.</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "名称不能为空。";

        string trimmed = name.Trim();
        if (trimmed is "." or "..")
            return "名称无效。";

        if (trimmed.IndexOfAny(['/', '\\']) >= 0)
            return "名称不能包含斜杠。";

        foreach (char c in trimmed)
        {
            if (char.IsControl(c))
                return "名称不能包含控制字符。";
        }

        return null;
    }

    /// <summary>
    /// Reduces a path to the single leading slash form the server expects.
    /// </summary>
    /// <remarks>
    /// Both separators are accepted, empty segments and <c>.</c> are dropped, and <c>..</c> pops one
    /// segment without ever escaping the root. That last part is what keeps a crafted server reply
    /// from addressing another channel's area.
    /// </remarks>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RootPath;

        var segments = new List<string>(4);
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            string part = segment.Trim();
            if (part.Length == 0 || part == ".")
                continue;

            if (part == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? RootPath : RootPath + string.Join('/', segments);
    }

    /// <summary>Joins a directory and an entry name into a normalised channel path.</summary>
    internal static string Combine(string? directory, string? name)
    {
        string parent = Normalize(directory);
        if (string.IsNullOrWhiteSpace(name))
            return parent;

        return Normalize(parent == RootPath ? RootPath + name : parent + "/" + name);
    }

    /// <summary>The containing directory, or the root when there is none.</summary>
    public static string Parent(string? path)
    {
        string normalized = Normalize(path);
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? RootPath : normalized[..slash];
    }

    /// <summary>
    /// Turns a server-supplied name into something safe to use as a local file name.
    /// </summary>
    /// <remarks>
    /// The name is only ever a *suggestion* for the save dialog, but it arrives from the server, so
    /// separators and traversal have to be stripped rather than trusted.
    /// </remarks>
    public static string SanitizeLocalName(string? name)
    {
        const string fallback = "download";

        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (c == '/' || c == '\\' || char.IsControl(c) || Array.IndexOf(invalid, c) >= 0)
                continue;

            cleaned[length++] = c;
        }

        string result = new string(cleaned, 0, length).Trim().TrimEnd('.');
        return result.Length == 0 || result is "." or ".." ? fallback : result;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "无法删除临时文件 {Path}。", path);
        }
    }
}
