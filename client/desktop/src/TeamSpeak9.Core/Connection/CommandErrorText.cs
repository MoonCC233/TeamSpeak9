// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TSLib;
using TSLib.Messages;

namespace TeamSpeak9.Core.Connection;

/// <summary>
/// Turns a TSLib <see cref="CommandError"/> into something worth showing a user.
/// </summary>
/// <remarks>
/// <para>
/// TSLib's own <c>ErrorFormat()</c> produces English developer text like
/// <c>"1538: the command failed to execute: invalid parameter"</c>. This maps the error ids the
/// management commands actually hit onto Chinese messages that say what to do about it, and falls
/// back to the server's message for everything else.
/// </para>
/// <para>
/// Lives in <c>Core</c> rather than in a view model because both <see cref="TsConnection"/> and
/// every service in <c>Management</c> needs it.
/// </para>
/// </remarks>
public static class CommandErrorText
{
    /// <summary>Formats an error for display. Never returns null or empty.</summary>
    public static string Describe(CommandError? error)
    {
        if (error is null)
            return "未知错误。";

        string? known = KnownMessage(error.Id);
        string message = known
            ?? (string.IsNullOrWhiteSpace(error.Message) ? error.Id.ToString() : error.Message);

        if (error.MissingPermissionId is { } permission
            && permission != TsPermission.unknown
            && permission != TsPermission.undefined)
        {
            return $"{message}（缺少权限：{permission}）";
        }

        // The server's ExtraMessage names the offending parameter, which is the single most useful
        // detail for a rejected channeledit. Keep it even when we have a friendlier message.
        return string.IsNullOrWhiteSpace(error.ExtraMessage) ? message : $"{message}：{error.ExtraMessage}";
    }

    /// <summary>
    /// True when the error means "the change was already in effect", which callers should treat as
    /// success rather than surfacing.
    /// </summary>
    /// <remarks>
    /// Moving a channel to where it already is answers <c>channel_already_in</c>; re-applying an
    /// identical set of properties answers <c>database_no_modifications</c>. Neither is a failure
    /// from the user's point of view.
    /// </remarks>
    public static bool IsNoOp(CommandError? error) => error?.Id is
        TsErrorCode.channel_already_in or
        TsErrorCode.database_no_modifications;

    /// <summary>
    /// Chinese text for the error ids the channel, icon and server editors can realistically hit.
    /// </summary>
    /// <remarks>
    /// <c>permission_invalid_group_id</c> is the notable one: the server reports it for an
    /// out-of-range <c>i_icon_id</c> value, not for a bad group id at all. See
    /// docs/desktop/tslib-ts6-compat.md §4.4.
    /// </remarks>
    private static string? KnownMessage(TsErrorCode id) => id switch
    {
        TsErrorCode.ok => null,

        TsErrorCode.parameter_quote => "参数格式错误（引号未闭合）。",
        TsErrorCode.parameter_invalid_count => "参数数量不正确。",
        TsErrorCode.parameter_invalid => "服务端拒绝了这个参数值。",
        TsErrorCode.parameter_not_found => "缺少必需的参数。",
        TsErrorCode.parameter_convert => "参数无法转换为服务端要求的类型。",
        TsErrorCode.parameter_invalid_size => "参数长度超出允许范围。",
        TsErrorCode.parameter_missing => "缺少必需的参数。",

        TsErrorCode.permissions_client_insufficient => "权限不足。",
        TsErrorCode.permissions => "权限不足。",

        TsErrorCode.channel_invalid_id => "频道不存在。",
        TsErrorCode.channel_protocol_limit_reached => "已达到服务端的频道数量上限。",
        TsErrorCode.channel_name_inuse => "频道名称已被占用。",
        TsErrorCode.channel_not_empty => "频道内还有客户端或子频道，无法删除（可勾选强制删除）。",
        TsErrorCode.channel_can_not_delete_default => "默认频道无法删除。",
        TsErrorCode.channel_default_require_permanent => "默认频道必须是永久频道。",
        TsErrorCode.channel_invalid_flags => "频道类型与其他设置冲突。",
        TsErrorCode.channel_parent_not_permanent => "永久频道不能放在非永久频道之下。",
        TsErrorCode.channel_invalid_order => "频道排序位置无效。",
        TsErrorCode.channel_no_filetransfer_supported => "该频道不支持文件传输。",
        TsErrorCode.channel_invalid_password => "频道密码错误。",

        TsErrorCode.permission_invalid_group_id => "图标编号超出该组允许的范围（模板组与 ServerQuery 组只能用小于 1000 的内置图标）。",

        TsErrorCode.file_invalid_name => "文件名无效。",
        TsErrorCode.file_invalid_permissions => "没有操作该文件的权限。",
        TsErrorCode.file_already_exists => "文件已存在。",
        TsErrorCode.file_not_found => "文件不存在。",
        TsErrorCode.file_io_error => "服务端文件读写失败。",
        TsErrorCode.file_invalid_path => "文件路径无效。",
        TsErrorCode.file_no_files_available => "该目录下没有文件。",
        TsErrorCode.file_invalid_size => "文件大小超出服务端允许的上限。",
        TsErrorCode.file_already_in_use => "文件正在被其他传输占用。",
        TsErrorCode.file_could_not_open_connection => "无法建立文件传输连接（请检查服务端 30033 端口）。",
        TsErrorCode.file_exceeds_file_system_maximum_size => "文件超出服务端文件系统的大小限制。",
        TsErrorCode.file_no_space_left_on_device => "服务端磁盘空间不足。",
        TsErrorCode.file_transfer_connection_timeout => "文件传输连接超时。",
        TsErrorCode.file_connection_lost => "文件传输连接已断开。",
        TsErrorCode.file_transfer_server_quota_exceeded => "超出服务端的存储配额。",

        TsErrorCode.database_empty_result => "服务端没有返回数据。",
        TsErrorCode.database_no_modifications => "没有任何改动。",

        _ => null,
    };
}
