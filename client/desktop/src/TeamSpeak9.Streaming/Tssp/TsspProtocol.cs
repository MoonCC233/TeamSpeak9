// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

namespace TeamSpeak9.Streaming.Tssp;

/// <summary>
/// TSSP v1 的传输层常量，对应 <c>docs/protocol/tssp-v1.md</c> §2。
/// </summary>
public static class TsspProtocol
{
    /// <summary>协议版本号，随 <c>hello</c> 上报。</summary>
    public const int Version = 1;

    /// <summary>WebSocket 子协议名，客户端必须在 <c>Sec-WebSocket-Protocol</c> 中声明。</summary>
    public const string SubProtocol = "tssp.v1";

    /// <summary>默认信令端口，与 tsserver 的 9987 语音端口无关。</summary>
    public const int DefaultPort = 10099;

    /// <summary>信令端点路径。</summary>
    public const string DefaultPath = "/tssp/v1";

    /// <summary>单帧最大字节数（256 KiB），超限一律视为协议错误。</summary>
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>无任何流量的空闲上限，超过则重连。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>请求默认超时。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>重连退避起始间隔。</summary>
    public static readonly TimeSpan ReconnectBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>重连退避上限。</summary>
    public static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 按第 <paramref name="attempt"/> 次失败计算退避间隔，序列为 1s/2s/4s/8s/16s/30s/30s…（§8.3）。
    /// </summary>
    /// <param name="attempt">已失败次数，从 1 开始。</param>
    public static TimeSpan ReconnectDelayFor(int attempt)
    {
        if (attempt <= 1)
        {
            return ReconnectBaseDelay;
        }

        // 2^(attempt-1) 在 attempt 较大时会溢出，先按上限所需的指数截断。
        var shift = Math.Min(attempt - 1, 16);
        var seconds = ReconnectBaseDelay.TotalSeconds * Math.Pow(2, shift);
        return seconds >= ReconnectMaxDelay.TotalSeconds
            ? ReconnectMaxDelay
            : TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>请求与响应消息类型，对应 <c>internal/tssp/messages.go</c> 的 <c>Type*</c> 常量。</summary>
public static class TsspTypes
{
    /// <summary>鉴权握手。</summary>
    public const string Hello = "hello";

    /// <summary>开始共享。</summary>
    public const string Setup = "setup";

    /// <summary>更新共享参数。</summary>
    public const string Update = "update";

    /// <summary>停止共享。</summary>
    public const string Stop = "stop";

    /// <summary>列出可见流。</summary>
    public const string List = "list";

    /// <summary>请求观看。</summary>
    public const string Subscribe = "subscribe";

    /// <summary>取消观看。</summary>
    public const string Unsubscribe = "unsubscribe";

    /// <summary>发布者审批观看请求。</summary>
    public const string RespondJoin = "respond_join";

    /// <summary>SDP/ICE 中转。</summary>
    public const string Signaling = "signaling";

    /// <summary>续签会话或上报频道变更。</summary>
    public const string Renew = "renew";

    /// <summary>上报质量数据。</summary>
    public const string Stats = "stats";

    /// <summary>成功响应。</summary>
    public const string Ok = "ok";

    /// <summary>失败响应。</summary>
    public const string Error = "error";
}

/// <summary>服务端事件类型，对应规范 §5.11。</summary>
public static class TsspEvents
{
    /// <summary>同频道出现新流。</summary>
    public const string StreamAdded = "stream_added";

    /// <summary>流参数变化。</summary>
    public const string StreamUpdated = "stream_updated";

    /// <summary>流已移除。</summary>
    public const string StreamRemoved = "stream_removed";

    /// <summary>invite_only 观看请求获批。</summary>
    public const string SubscribeReady = "subscribe_ready";

    /// <summary>有人请求观看（发给发布者）。</summary>
    public const string JoinRequest = "join_request";

    /// <summary>观看请求被拒绝。</summary>
    public const string JoinRejected = "join_rejected";

    /// <summary>P2P 订阅者就绪，发布者应发起 offer。</summary>
    public const string PeerJoined = "peer_joined";

    /// <summary>P2P 对端离开。</summary>
    public const string PeerLeft = "peer_left";

    /// <summary>本客户端被移出某路流。</summary>
    public const string RemovedFromStream = "removed_from_stream";

    /// <summary>会话即将过期，应发起 <c>renew</c>。</summary>
    public const string TokenExpiring = "token_expiring";

    /// <summary>服务端索取质量数据。</summary>
    public const string StatsRequest = "stats_request";

    /// <summary>服务端主动断开说明。</summary>
    public const string Bye = "bye";
}

/// <summary>错误码，对应规范 §7。</summary>
public static class TsspErrors
{
    /// <summary>报文结构或字段非法。</summary>
    public const string BadRequest = "BAD_REQUEST";

    /// <summary>协议版本不受支持。</summary>
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";

    /// <summary><c>server_addr</c> 未匹配到已配置的虚拟服务器。</summary>
    public const string UnknownServer = "UNKNOWN_SERVER";

    /// <summary>ServerQuery 暂时不可用。</summary>
    public const string QueryUnavailable = "QUERY_UNAVAILABLE";

    /// <summary>声明的 clid 在 tsserver 上不存在。</summary>
    public const string ClientNotFound = "CLIENT_NOT_FOUND";

    /// <summary>uid / cid / client_type 与 tsserver 实际值不符。</summary>
    public const string IdentityMismatch = "IDENTITY_MISMATCH";

    /// <summary>被服务器组白名单或黑名单拒绝。</summary>
    public const string NotAllowed = "NOT_ALLOWED";

    /// <summary>触发限流，应按 <c>retry_after_ms</c> 退避。</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>token 缺失、签名错误或与连接不匹配。</summary>
    public const string TokenInvalid = "TOKEN_INVALID";

    /// <summary>token 已过期，应重新 <c>hello</c>。</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    /// <summary>请求的媒体模式未启用。</summary>
    public const string ModeNotSupported = "MODE_NOT_SUPPORTED";

    /// <summary>编解码交集为空。</summary>
    public const string CodecNotSupported = "CODEC_NOT_SUPPORTED";

    /// <summary>流不存在或已结束。</summary>
    public const string StreamNotFound = "STREAM_NOT_FOUND";

    /// <summary>只有发布者能执行该操作。</summary>
    public const string NotStreamOwner = "NOT_STREAM_OWNER";

    /// <summary>订阅者与发布者不在同一频道。</summary>
    public const string NotSameChannel = "NOT_SAME_CHANNEL";

    /// <summary>本客户端已有进行中的共享。</summary>
    public const string AlreadyPublishing = "ALREADY_PUBLISHING";

    /// <summary>频道内流数量超限。</summary>
    public const string TooManyStreams = "TOO_MANY_STREAMS";

    /// <summary>单路流观众数超限。</summary>
    public const string TooManyViewers = "TOO_MANY_VIEWERS";

    /// <summary>发布者拒绝了观看请求。</summary>
    public const string JoinRejected = "JOIN_REJECTED";

    /// <summary>协商失败，可尝试 <c>restart</c>。</summary>
    public const string SignalingFailed = "SIGNALING_FAILED";

    /// <summary>服务端内部错误。</summary>
    public const string Internal = "INTERNAL";

    /// <summary>全部错误码，便于校验与遍历。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        BadRequest,
        UnsupportedProtocol,
        UnknownServer,
        QueryUnavailable,
        ClientNotFound,
        IdentityMismatch,
        NotAllowed,
        RateLimited,
        TokenInvalid,
        TokenExpired,
        ModeNotSupported,
        CodecNotSupported,
        StreamNotFound,
        NotStreamOwner,
        NotSameChannel,
        AlreadyPublishing,
        TooManyStreams,
        TooManyViewers,
        JoinRejected,
        SignalingFailed,
        Internal,
    ];

    /// <summary>判断该错误码是否意味着当前 token 已不可用，需要重新 <c>hello</c>。</summary>
    public static bool RequiresRehello(string? code) =>
        code is TokenInvalid or TokenExpired;

    /// <summary>判断该错误码是否值得重试。</summary>
    public static bool IsTransient(string? code) =>
        code is QueryUnavailable or RateLimited or Internal;
}

/// <summary>媒体模式。</summary>
public static class TsspModes
{
    /// <summary>服务器转发。</summary>
    public const string Sfu = "sfu";

    /// <summary>点对点直连。</summary>
    public const string P2P = "p2p";
}

/// <summary>流类型。</summary>
public static class TsspStreamTypes
{
    /// <summary>整个显示器。</summary>
    public const string Screen = "screen";

    /// <summary>单个窗口。</summary>
    public const string Window = "window";

    /// <summary>摄像头。</summary>
    public const string Camera = "camera";
}

/// <summary>可见性。</summary>
public static class TsspAccessibility
{
    /// <summary>同频道成员可直接观看。</summary>
    public const string Channel = "channel";

    /// <summary>需要发布者逐个审批。</summary>
    public const string InviteOnly = "invite_only";
}

/// <summary>协商角色。</summary>
public static class TsspRoles
{
    /// <summary>发布方。</summary>
    public const string Publisher = "publisher";

    /// <summary>订阅方。</summary>
    public const string Subscriber = "subscriber";
}

/// <summary>发起 offer 的一方，用于 <c>publish.offerer</c>。</summary>
public static class TsspOfferers
{
    /// <summary>由发布客户端发起。</summary>
    public const string Publisher = "publisher";

    /// <summary>由 ts9-stream 发起。</summary>
    public const string Server = "server";
}

/// <summary>信令子类型。</summary>
public static class TsspSignalingTypes
{
    /// <summary>SDP offer。</summary>
    public const string Offer = "offer";

    /// <summary>SDP answer。</summary>
    public const string Answer = "answer";

    /// <summary>单条 ICE candidate，负载是 JSON 字符串。</summary>
    public const string Candidate = "candidate";

    /// <summary>candidate 收集结束。</summary>
    public const string EndOfCandidates = "end_of_candidates";

    /// <summary>ICE 重启，携带新的 offer SDP。</summary>
    public const string Restart = "restart";
}

/// <summary>流移除原因。</summary>
public static class TsspReasons
{
    /// <summary>发布者主动停止。</summary>
    public const string Stopped = "stopped";

    /// <summary>发布者掉线。</summary>
    public const string Disconnected = "disconnected";

    /// <summary>频道变化导致订阅失效。</summary>
    public const string ChannelChanged = "channel_changed";

    /// <summary>被发布者移出。</summary>
    public const string Removed = "removed";

    /// <summary>服务端关闭。</summary>
    public const string ServerShutdown = "server_shutdown";

    /// <summary>媒体链路失败。</summary>
    public const string Failed = "failed";

    /// <summary>本方取消观看。</summary>
    public const string Unsubscribed = "unsubscribed";

    /// <summary>观看请求被拒绝。</summary>
    public const string Rejected = "rejected";
}

/// <summary>订阅状态。</summary>
public static class TsspSubscribeStates
{
    /// <summary>等待发布者审批。</summary>
    public const string Pending = "pending";

    /// <summary>可以开始协商。</summary>
    public const string Ready = "ready";
}

/// <summary><c>setup.properties</c> 与 <c>update.properties</c> 的键名，对应规范 §5.2。</summary>
public static class TsspStreamProperties
{
    /// <summary>宽度（像素）。</summary>
    public const string Width = "width";

    /// <summary>高度（像素）。</summary>
    public const string Height = "height";

    /// <summary>帧率。</summary>
    public const string FrameRate = "fps";

    /// <summary>视频编码，取值 <c>H264</c> 或 <c>VP8</c>。</summary>
    public const string Codec = "codec";

    /// <summary>目标码率（kbps）。</summary>
    public const string BitrateKbps = "bitrate_kbps";

    /// <summary>是否携带音频，取值 <c>"true"</c> / <c>"false"</c>。</summary>
    public const string Audio = "audio";

    /// <summary>采集源标识，例如 <c>display:0</c>。</summary>
    public const string Source = "source";
}
