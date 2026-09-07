// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Globalization;
using System.Net;
using TeamSpeak9.Core.Settings;

namespace TeamSpeak9.Streaming.Tssp;

/// <summary>信令端点的来源，对应规范 §2.1 的优先级。</summary>
public enum TsspEndpointSource
{
    /// <summary>来自 tsserver 的 <c>virtualserver_sfu_endpoint</c> 属性。</summary>
    ServerAdvertised,

    /// <summary>来自用户在设置里手工填写的地址。</summary>
    ManualSetting,
}

/// <summary>端点解析失败的原因。</summary>
public enum TsspEndpointProblem
{
    /// <summary>解析成功。</summary>
    None,

    /// <summary>服务器没有公告地址，用户也没有手工填写。</summary>
    Missing,

    /// <summary>地址无法解析为绝对 URI。</summary>
    Malformed,

    /// <summary>使用了明文 <c>ws://</c>，但未开启开发模式。</summary>
    InsecureScheme,

    /// <summary>使用了 <c>ws</c> / <c>wss</c> 之外的协议。</summary>
    UnsupportedScheme,
}

/// <summary>端点解析结果。</summary>
public sealed record TsspEndpointResolution
{
    /// <summary>解析是否成功。</summary>
    public bool Success => Problem == TsspEndpointProblem.None;

    /// <summary>补全端口与路径后的信令地址。</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>地址来源。</summary>
    public TsspEndpointSource Source { get; init; }

    /// <summary>
    /// 用于记录在 <see cref="StreamSettings.TrustedEndpoints"/> 中的 <c>host:port</c> 键。
    /// </summary>
    public string TrustKey { get; init; } = string.Empty;

    /// <summary>
    /// 该主机尚未被用户确认过。规范 §2.1 要求首次连接新地址时提示用户并显示完整主机名与端口，
    /// 因此这里只上报信号，弹窗交由界面层处理。
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>失败原因。</summary>
    public TsspEndpointProblem Problem { get; init; }

    /// <summary>可直接展示给用户的失败说明。</summary>
    public string? Message { get; init; }
}

/// <summary>
/// 按规范 §2.1 决定连接哪个信令端点：优先用 tsserver 公告的地址，其次用用户手工填写的地址。
/// </summary>
public static class TsspEndpointResolver
{
    /// <summary>tsserver 上承载信令地址的虚拟服务器属性名。</summary>
    public const string ServerPropertyName = "virtualserver_sfu_endpoint";

    /// <summary>
    /// 解析出应当连接的信令端点。
    /// </summary>
    /// <param name="advertisedEndpoint">
    /// tsserver 公告的 <c>virtualserver_sfu_endpoint</c>；为空表示服务器没有配置。
    /// </param>
    /// <param name="settings">屏幕共享设置，提供手工地址与已确认主机列表。</param>
    /// <param name="allowInsecureScheme">
    /// 是否允许明文 <c>ws://</c>。仅在本地开发（<c>dev_insecure</c>）时置为 <c>true</c>。
    /// </param>
    /// <remarks>
    /// 公告地址存在但不合法时会退回手工地址；若手工地址为空，则直接上报公告地址的问题。
    /// </remarks>
    public static TsspEndpointResolution Resolve(
        string? advertisedEndpoint,
        StreamSettings settings,
        bool allowInsecureScheme = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var advertised = TryNormalize(
            advertisedEndpoint,
            TsspEndpointSource.ServerAdvertised,
            settings,
            allowInsecureScheme);
        if (advertised.Success)
        {
            return advertised;
        }

        var manual = TryNormalize(
            settings.ManualEndpoint,
            TsspEndpointSource.ManualSetting,
            settings,
            allowInsecureScheme);
        if (manual.Success)
        {
            return manual;
        }

        // 手工地址压根没填时，公告地址的具体问题对排查更有价值。
        return manual.Problem == TsspEndpointProblem.Missing
            && advertised.Problem != TsspEndpointProblem.Missing
            ? advertised
            : manual;
    }

    /// <summary>
    /// 计算某个端点在 <see cref="StreamSettings.TrustedEndpoints"/> 中的存储键。
    /// </summary>
    public static string TrustKeyFor(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return NormalizeHost(endpoint.Host)
            + ":"
            + endpoint.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把端点记为已确认；重复调用不会产生重复条目。
    /// </summary>
    /// <returns>本次是否真的新增了条目。</returns>
    public static bool Trust(StreamSettings settings, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);

        var key = TrustKeyFor(endpoint);
        if (IsTrusted(settings.TrustedEndpoints, key))
        {
            return false;
        }

        settings.TrustedEndpoints.Add(key);
        return true;
    }

    private static TsspEndpointResolution TryNormalize(
        string? raw,
        TsspEndpointSource source,
        StreamSettings settings,
        bool allowInsecureScheme)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new TsspEndpointResolution
            {
                Source = source,
                Problem = TsspEndpointProblem.Missing,
                Message = source == TsspEndpointSource.ServerAdvertised
                    ? "服务器没有公告屏幕共享服务地址。"
                    : "请在设置中填写屏幕共享服务地址。",
            };
        }

        // 允许用户只填 host 或 host:port，按规范默认使用 wss。
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = Uri.UriSchemeWss + "://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return Failure(source, TsspEndpointProblem.Malformed, $"地址无法解析：{raw}");
        }

        var isSecure = string.Equals(parsed.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase);
        var isPlain = string.Equals(parsed.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase);

        if (!isSecure && !isPlain)
        {
            return Failure(
                source,
                TsspEndpointProblem.UnsupportedScheme,
                $"仅支持 wss:// 地址，收到的是 {parsed.Scheme}://。");
        }

        if (isPlain && !allowInsecureScheme)
        {
            return Failure(
                source,
                TsspEndpointProblem.InsecureScheme,
                "屏幕共享服务地址必须使用 wss://，明文 ws:// 仅限本地开发模式。");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return Failure(source, TsspEndpointProblem.Malformed, $"地址缺少主机名：{raw}");
        }

        var builder = new UriBuilder(parsed);
        if (!HasExplicitPort(AuthorityOf(text)))
        {
            builder.Port = TsspProtocol.DefaultPort;
        }

        if (string.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/")
        {
            builder.Path = TsspProtocol.DefaultPath;
        }

        builder.Query = string.Empty;
        builder.Fragment = string.Empty;

        var endpoint = builder.Uri;
        var trustKey = TrustKeyFor(endpoint);

        return new TsspEndpointResolution
        {
            Endpoint = endpoint,
            Source = source,
            TrustKey = trustKey,
            RequiresConfirmation = !IsTrusted(settings.TrustedEndpoints, trustKey),
        };
    }

    private static TsspEndpointResolution Failure(
        TsspEndpointSource source,
        TsspEndpointProblem problem,
        string message) =>
        new() { Source = source, Problem = problem, Message = message };

    private static bool IsTrusted(IEnumerable<string> trusted, string trustKey)
    {
        foreach (var entry in trusted)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (string.Equals(NormalizeTrustEntry(entry), trustKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把已保存的条目折算成 <c>host:port</c>，兼容早期存成完整 URL 的写法。</summary>
    private static string NormalizeTrustEntry(string entry)
    {
        var authority = AuthorityOf(entry.Trim());
        var port = TsspProtocol.DefaultPort.ToString(CultureInfo.InvariantCulture);
        if (HasExplicitPort(authority))
        {
            var separator = authority.LastIndexOf(':');
            port = authority[(separator + 1)..];
            authority = authority[..separator];
        }

        return NormalizeHost(authority) + ":" + port;
    }

    /// <summary>
    /// 统一主机名写法：IPv6 字面量折算成 <see cref="IPAddress"/> 的紧凑形式，
    /// 因为 <see cref="Uri.Host"/> 会返回带方括号且零填充展开的写法，
    /// 而用户手工填写或早期保存的条目通常是紧凑写法。
    /// </summary>
    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim();
        var bare = trimmed.StartsWith('[') && trimmed.EndsWith(']')
            ? trimmed[1..^1]
            : trimmed;

        return IPAddress.TryParse(bare, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address.ToString() + "]"
            : trimmed.ToLowerInvariant();
    }

    private static string AuthorityOf(string text)
    {
        var start = text.IndexOf("://", StringComparison.Ordinal);
        var body = start >= 0 ? text[(start + 3)..] : text;
        var end = body.IndexOfAny(['/', '?', '#']);
        return end >= 0 ? body[..end] : body;
    }

    private static bool HasExplicitPort(string authority)
    {
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            return close >= 0 && close + 1 < authority.Length && authority[close + 1] == ':';
        }

        return authority.Contains(':', StringComparison.Ordinal);
    }
}
