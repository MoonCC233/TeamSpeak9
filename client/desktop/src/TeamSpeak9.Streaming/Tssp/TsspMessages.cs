// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamSpeak9.Streaming.Tssp;

/// <summary>
/// TSSP v1 的 JSON 约定：全 <c>snake_case</c>、省略空值、时间戳为 Unix 毫秒。
/// </summary>
public static class TsspJson
{
    /// <summary>收发 TSSP 报文统一使用的序列化选项。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>把负载序列化为可放进 <see cref="TsspEnvelope.Data"/> 的元素。</summary>
    public static JsonElement ToElement<T>(T payload) =>
        JsonSerializer.SerializeToElement(payload, Options);
}

/// <summary>
/// 所有 TSSP 消息的外层结构，对应规范 §3。
/// </summary>
public sealed class TsspEnvelope
{
    /// <summary>消息类型。</summary>
    [JsonPropertyName("t")]
    public string Type { get; set; } = string.Empty;

    /// <summary>请求标识；响应回显同一个值，事件推送没有该字段。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>发送时间（Unix 毫秒），仅用于诊断。</summary>
    [JsonPropertyName("ts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Timestamp { get; set; }

    /// <summary>消息负载。</summary>
    [JsonPropertyName("d")]
    public JsonElement? Data { get; set; }

    /// <summary>是否为成功响应。</summary>
    [JsonIgnore]
    public bool IsOk => string.Equals(Type, TsspTypes.Ok, StringComparison.Ordinal);

    /// <summary>是否为失败响应。</summary>
    [JsonIgnore]
    public bool IsError => string.Equals(Type, TsspTypes.Error, StringComparison.Ordinal);

    /// <summary>负载是否为空；<c>respond_join</c> / <c>signaling</c> / <c>stats</c> 的成功响应就是空负载。</summary>
    [JsonIgnore]
    public bool HasData =>
        Data.HasValue && Data.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>构造一条请求。</summary>
    public static TsspEnvelope Request<T>(string type, string id, T payload) => new()
    {
        Type = type,
        Id = id,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Data = TsspJson.ToElement(payload),
    };

    /// <summary>把负载解析为指定类型；负载为空时返回 <c>null</c>。</summary>
    public T? Decode<T>()
        where T : class
    {
        if (!HasData)
        {
            return null;
        }

        return Data!.Value.Deserialize<T>(TsspJson.Options);
    }

    /// <summary>序列化为一帧 UTF-8 JSON 文本。</summary>
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, TsspJson.Options);
}

/// <summary>错误负载，对应规范 §7。</summary>
public sealed class TsspErrorPayload
{
    /// <summary>错误码，取值见 <see cref="TsspErrors"/>。</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = TsspErrors.Internal;

    /// <summary>人类可读的说明。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>限流时给出的建议退避毫秒数。</summary>
    [JsonPropertyName("retry_after_ms")]
    public long RetryAfterMs { get; set; }
}

/// <summary>把服务端返回的 <c>error</c> 响应包装成异常。</summary>
public sealed class TsspException : Exception
{
    /// <summary>用错误负载构造。</summary>
    public TsspException(TsspErrorPayload payload)
        : base(Describe(payload))
    {
        ArgumentNullException.ThrowIfNull(payload);
        Code = payload.Code;
        RetryAfter = payload.RetryAfterMs > 0
            ? TimeSpan.FromMilliseconds(payload.RetryAfterMs)
            : null;
    }

    /// <summary>用错误码与说明构造。</summary>
    public TsspException(string code, string? message)
        : base(string.IsNullOrEmpty(message) ? code : code + ": " + message)
    {
        Code = code;
    }

    /// <summary>默认构造，错误码为 <see cref="TsspErrors.Internal"/>。</summary>
    public TsspException()
        : base(TsspErrors.Internal)
    {
        Code = TsspErrors.Internal;
    }

    /// <summary>带内部异常的构造。</summary>
    public TsspException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = TsspErrors.Internal;
    }

    /// <summary>错误码。</summary>
    public string Code { get; }

    /// <summary>服务端建议的退避时长。</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>该错误是否要求重新走 <c>hello</c>。</summary>
    public bool RequiresRehello => TsspErrors.RequiresRehello(Code);

    private static string Describe(TsspErrorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return string.IsNullOrEmpty(payload.Message)
            ? payload.Code
            : payload.Code + ": " + payload.Message;
    }
}

/// <summary>客户端在 <c>hello</c> 中声明的能力。</summary>
public sealed record TsspClientCapabilities
{
    /// <summary>支持的媒体模式，按偏好排序。</summary>
    [JsonPropertyName("modes")]
    public IReadOnlyList<string>? Modes { get; init; }

    /// <summary>支持的视频编码。</summary>
    [JsonPropertyName("video_codecs")]
    public IReadOnlyList<string>? VideoCodecs { get; init; }

    /// <summary>支持的音频编码。</summary>
    [JsonPropertyName("audio_codecs")]
    public IReadOnlyList<string>? AudioCodecs { get; init; }

    /// <summary>可同时接收的流数量。</summary>
    [JsonPropertyName("max_recv_streams")]
    public int MaxRecvStreams { get; init; }

    /// <summary>最大接收码率（kbps）。</summary>
    [JsonPropertyName("max_bitrate_kbps")]
    public int MaxBitrateKbps { get; init; }
}

/// <summary>客户端自报的软件信息，仅用于服务端日志。</summary>
public sealed record TsspClientInfo
{
    /// <summary>客户端名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>客户端版本。</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>运行平台。</summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }
}

/// <summary><c>hello</c> 请求负载，对应规范 §5.1。</summary>
public sealed record TsspHelloRequest
{
    /// <summary>协议版本。</summary>
    [JsonPropertyName("protocol")]
    public int Protocol { get; init; } = TsspProtocol.Version;

    /// <summary>tsserver 地址，形如 <c>ts.example.com:9987</c>。</summary>
    [JsonPropertyName("server_addr")]
    public required string ServerAddress { get; init; }

    /// <summary>本客户端的 <c>client_unique_identifier</c>。</summary>
    [JsonPropertyName("uid")]
    public required string Uid { get; init; }

    /// <summary>本客户端在 tsserver 上的 clid。</summary>
    [JsonPropertyName("clid")]
    public required int Clid { get; init; }

    /// <summary>本客户端当前所在频道。</summary>
    [JsonPropertyName("cid")]
    public required long Cid { get; init; }

    /// <summary>客户端生成的随机串，服务端原样回显。</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    /// <summary>软件信息。</summary>
    [JsonPropertyName("client")]
    public TsspClientInfo? Client { get; init; }

    /// <summary>能力声明。</summary>
    [JsonPropertyName("capabilities")]
    public TsspClientCapabilities? Capabilities { get; init; }
}

/// <summary>服务端下发的 ICE 服务器条目。</summary>
public sealed record TsspIceServer
{
    /// <summary>STUN/TURN URL 列表。</summary>
    [JsonPropertyName("urls")]
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>TURN 用户名。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>TURN 凭据。</summary>
    [JsonPropertyName("credential")]
    public string? Credential { get; init; }

    /// <summary>凭据有效期（秒）。</summary>
    [JsonPropertyName("credential_ttl")]
    public long CredentialTtl { get; init; }
}

/// <summary>服务端能力与限制。</summary>
public sealed record TsspServerCapabilities
{
    /// <summary>启用的媒体模式。</summary>
    [JsonPropertyName("modes")]
    public IReadOnlyList<string> Modes { get; init; } = [];

    /// <summary>客户端未指定时采用的模式。</summary>
    [JsonPropertyName("default_mode")]
    public string DefaultMode { get; init; } = TsspModes.Sfu;

    /// <summary>支持的视频编码。</summary>
    [JsonPropertyName("video_codecs")]
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];

    /// <summary>支持的音频编码。</summary>
    [JsonPropertyName("audio_codecs")]
    public IReadOnlyList<string>? AudioCodecs { get; init; }

    /// <summary>码率硬上限。</summary>
    [JsonPropertyName("max_bitrate_kbps")]
    public int MaxBitrateKbps { get; init; }

    /// <summary>单频道最大并发流数。</summary>
    [JsonPropertyName("max_streams_per_channel")]
    public int MaxStreamsPerChannel { get; init; }

    /// <summary>单路流最大观众数。</summary>
    [JsonPropertyName("max_viewers_per_stream")]
    public int MaxViewersPerStream { get; init; }

    /// <summary>ICE 服务器列表。</summary>
    [JsonPropertyName("ice_servers")]
    public IReadOnlyList<TsspIceServer>? IceServers { get; init; }
}

/// <summary><c>hello</c> 成功响应。</summary>
public sealed record TsspHelloResponse
{
    /// <summary>会话标识。</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>后续请求必须携带的短时效 token。</summary>
    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = string.Empty;

    /// <summary>token 过期时间（Unix 毫秒）。</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }

    /// <summary>回显的 nonce，客户端应校验一致。</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    /// <summary>服务端从 tsserver 读到的昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }

    /// <summary>服务端能力。</summary>
    [JsonPropertyName("server")]
    public TsspServerCapabilities Server { get; init; } = new();
}

/// <summary><c>setup</c> 请求负载，对应规范 §5.2。</summary>
public sealed record TsspSetupRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>期望的媒体模式。</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>流类型。</summary>
    [JsonPropertyName("stream_type")]
    public required string StreamType { get; init; }

    /// <summary>可见性。</summary>
    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; init; }

    /// <summary>展示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>媒体参数，键名见 <see cref="TsspStreamProperties"/>。</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>
/// 发布协商指令。P2P 模式下服务端回空对象，因此所有字段都可能缺失。
/// </summary>
public sealed record TsspPublishInstruction
{
    /// <summary>由哪一方发起 offer，取值见 <see cref="TsspOfferers"/>。</summary>
    [JsonPropertyName("offerer")]
    public string? Offerer { get; init; }

    /// <summary>服务端强制的码率上限。</summary>
    [JsonPropertyName("max_bitrate_kbps")]
    public int? MaxBitrateKbps { get; init; }

    /// <summary>本次协商允许的编解码交集。</summary>
    [JsonPropertyName("video_codecs")]
    public IReadOnlyList<string>? VideoCodecs { get; init; }
}

/// <summary><c>setup</c> 成功响应。</summary>
public sealed record TsspSetupResponse
{
    /// <summary>服务端分配的流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>实际生效的媒体模式。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>发布协商指令。</summary>
    [JsonPropertyName("publish")]
    public TsspPublishInstruction? Publish { get; init; }
}

/// <summary><c>update</c> 请求负载，对应规范 §5.3。</summary>
public sealed record TsspUpdateRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>新的展示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>要覆盖的媒体参数。</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary><c>stop</c> 请求负载，对应规范 §5.4。</summary>
public sealed record TsspStopRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }
}

/// <summary><c>list</c> 请求负载，对应规范 §5.5。</summary>
public sealed record TsspListRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>只列出指定频道；省略表示当前频道。</summary>
    [JsonPropertyName("cid")]
    public long? Cid { get; init; }
}

/// <summary><c>list</c> 响应。</summary>
public sealed record TsspListResponse
{
    /// <summary>可见流列表。</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<TsspStream> Streams { get; init; } = [];
}

/// <summary><c>subscribe</c> 请求负载，对应规范 §5.6。</summary>
public sealed record TsspSubscribeRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>偏好的媒体模式。</summary>
    [JsonPropertyName("prefer_mode")]
    public string? PreferMode { get; init; }
}

/// <summary>对端客户端引用。</summary>
public sealed record TsspPeerRef
{
    /// <summary>对端 clid。</summary>
    [JsonPropertyName("clid")]
    public int Clid { get; init; }

    /// <summary>对端 uid。</summary>
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = string.Empty;

    /// <summary>对端昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}

/// <summary><c>subscribe</c> 响应。</summary>
public sealed record TsspSubscribeResponse
{
    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>订阅状态，取值见 <see cref="TsspSubscribeStates"/>。</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>实际生效的媒体模式。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>P2P 模式下的发布者信息。</summary>
    [JsonPropertyName("peer")]
    public TsspPeerRef? Peer { get; init; }
}

/// <summary><c>unsubscribe</c> 请求负载，对应规范 §5.7。</summary>
public sealed record TsspUnsubscribeRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }
}

/// <summary><c>respond_join</c> 请求负载，对应规范 §5.8。</summary>
public sealed record TsspRespondJoinRequest
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>申请者 clid。</summary>
    [JsonPropertyName("clid")]
    public required int Clid { get; init; }

    /// <summary>是否批准。</summary>
    [JsonPropertyName("accept")]
    public required bool Accept { get; init; }

    /// <summary>拒绝原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>SDP/ICE 中转负载，双向使用，对应规范 §5.9。</summary>
public sealed record TsspSignalingMessage
{
    /// <summary>会话 token；服务端下推时不带。</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>P2P 模式下的对端 clid；SFU 模式省略。</summary>
    [JsonPropertyName("peer_clid")]
    public int? PeerClid { get; init; }

    /// <summary>发送者自身的角色。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>信令子类型，取值见 <see cref="TsspSignalingTypes"/>。</summary>
    [JsonPropertyName("signaling_type")]
    public required string SignalingType { get; init; }

    /// <summary>
    /// offer / answer / restart 为 SDP 文本；candidate 为 JSON 字符串。
    /// </summary>
    [JsonPropertyName("signaling_data")]
    public string? SignalingData { get; init; }
}

/// <summary><c>candidate</c> 的 <c>signaling_data</c> 内层结构。</summary>
public sealed record TsspIceCandidate
{
    /// <summary>candidate 属性行。</summary>
    [JsonPropertyName("candidate")]
    public string Candidate { get; init; } = string.Empty;

    /// <summary>所属 media 的 mid。</summary>
    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    /// <summary>所属 media 的索引。</summary>
    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }

    /// <summary>ICE ufrag。</summary>
    [JsonPropertyName("usernameFragment")]
    public string? UsernameFragment { get; init; }
}

/// <summary><c>renew</c> 请求负载，对应规范 §5.10。</summary>
public sealed record TsspRenewRequest
{
    /// <summary>当前会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>当前真实 clid，服务端会重新核对。</summary>
    [JsonPropertyName("clid")]
    public required int Clid { get; init; }

    /// <summary>当前真实 cid，服务端会重新核对。</summary>
    [JsonPropertyName("cid")]
    public required long Cid { get; init; }
}

/// <summary><c>renew</c> 响应。</summary>
public sealed record TsspRenewResponse
{
    /// <summary>新 token。</summary>
    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = string.Empty;

    /// <summary>新的过期时间（Unix 毫秒）。</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }

    /// <summary>刷新后的 ICE 凭据。</summary>
    [JsonPropertyName("ice_servers")]
    public IReadOnlyList<TsspIceServer>? IceServers { get; init; }
}

/// <summary><c>stats</c> 上报负载，对应规范 §5.12。</summary>
public sealed record TsspStatsReport
{
    /// <summary>会话 token。</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>目标流。</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>上报方角色。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>实际码率。</summary>
    [JsonPropertyName("bitrate_kbps")]
    public double BitrateKbps { get; init; }

    /// <summary>实际帧率。</summary>
    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    /// <summary>丢包率。</summary>
    [JsonPropertyName("packet_loss")]
    public double PacketLoss { get; init; }

    /// <summary>往返时延（毫秒）。</summary>
    [JsonPropertyName("rtt_ms")]
    public double RttMs { get; init; }

    /// <summary>抖动（毫秒）。</summary>
    [JsonPropertyName("jitter_ms")]
    public double JitterMs { get; init; }

    /// <summary>丢弃帧数。</summary>
    [JsonPropertyName("frames_dropped")]
    public int FramesDropped { get; init; }
}

/// <summary>一路共享流的公开描述，对应规范 §6.1。</summary>
public sealed record TsspStream
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>所属频道。</summary>
    [JsonPropertyName("cid")]
    public long Cid { get; init; }

    /// <summary>媒体模式。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>流类型。</summary>
    [JsonPropertyName("stream_type")]
    public string StreamType { get; init; } = string.Empty;

    /// <summary>可见性。</summary>
    [JsonPropertyName("accessibility")]
    public string Accessibility { get; init; } = string.Empty;

    /// <summary>展示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>发布者。</summary>
    [JsonPropertyName("publisher")]
    public TsspPeerRef Publisher { get; init; } = new();

    /// <summary>媒体参数。</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>当前观众数。</summary>
    [JsonPropertyName("viewer_count")]
    public int ViewerCount { get; init; }

    /// <summary>创建时间（Unix 毫秒）。</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }
}

/// <summary><c>stream_added</c> / <c>stream_updated</c> 事件负载，同时也是 <c>update</c> 的响应体。</summary>
public sealed record TsspStreamEvent
{
    /// <summary>流描述。</summary>
    [JsonPropertyName("stream")]
    public TsspStream Stream { get; init; } = new();
}

/// <summary>
/// <c>stream_removed</c> 事件负载；<c>stop</c> 与 <c>unsubscribe</c> 的成功响应复用该结构。
/// </summary>
public sealed record TsspStreamRemovedEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>移除原因，取值见 <see cref="TsspReasons"/>。</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary><c>subscribe_ready</c> 事件负载。</summary>
public sealed record TsspSubscribeReadyEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>媒体模式。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>P2P 模式下的发布者信息。</summary>
    [JsonPropertyName("peer")]
    public TsspPeerRef? Peer { get; init; }
}

/// <summary><c>join_request</c> 事件负载，只发给发布者。</summary>
public sealed record TsspJoinRequestEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>申请者 clid。</summary>
    [JsonPropertyName("clid")]
    public int Clid { get; init; }

    /// <summary>申请者 uid。</summary>
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = string.Empty;

    /// <summary>申请者昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}

/// <summary><c>join_rejected</c> 事件负载。</summary>
public sealed record TsspJoinRejectedEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>拒绝原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary><c>peer_joined</c> / <c>peer_left</c> 事件负载，仅 P2P 模式使用。</summary>
public sealed record TsspPeerEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>对端 clid。</summary>
    [JsonPropertyName("clid")]
    public int Clid { get; init; }

    /// <summary>对端 uid。</summary>
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    /// <summary>对端昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }

    /// <summary>离开原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary><c>removed_from_stream</c> 事件负载。</summary>
public sealed record TsspRemovedFromStreamEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    /// <summary>原因。</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary><c>token_expiring</c> 事件负载。</summary>
public sealed record TsspTokenExpiringEvent
{
    /// <summary>过期时间（Unix 毫秒）。</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }
}

/// <summary><c>stats_request</c> 事件负载。</summary>
public sealed record TsspStatsRequestEvent
{
    /// <summary>流标识。</summary>
    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;
}

/// <summary><c>bye</c> 事件负载。</summary>
public sealed record TsspByeEvent
{
    /// <summary>断开原因码。</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>说明。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
