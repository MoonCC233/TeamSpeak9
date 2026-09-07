// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Text.Json;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming.Tests;

public class TsspMessagesTests
{
    [Fact]
    public void 请求信封写入四个标签字段()
    {
        var envelope = TsspEnvelope.Request(
            TsspTypes.Stop,
            "7",
            new TsspStopRequest { Token = "tok", StreamId = "s1" });

        using var document = JsonDocument.Parse(envelope.ToUtf8Bytes());
        var root = document.RootElement;

        Assert.Equal("stop", root.GetProperty("t").GetString());
        Assert.Equal("7", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("ts").GetInt64() > 0);
        Assert.Equal("tok", root.GetProperty("d").GetProperty("token").GetString());
        Assert.Equal("s1", root.GetProperty("d").GetProperty("stream_id").GetString());
    }

    [Fact]
    public void 时间戳为零时不写入()
    {
        var envelope = new TsspEnvelope { Type = TsspTypes.Ok, Id = "1" };

        using var document = JsonDocument.Parse(envelope.ToUtf8Bytes());

        Assert.False(document.RootElement.TryGetProperty("ts", out _));
    }

    [Fact]
    public void 空负载不写入并且解析回空()
    {
        var envelope = new TsspEnvelope { Type = TsspTypes.Ok, Id = "1" };

        using var document = JsonDocument.Parse(envelope.ToUtf8Bytes());
        Assert.False(document.RootElement.TryGetProperty("d", out _));

        Assert.False(envelope.HasData);
        Assert.Null(envelope.Decode<TsspSetupResponse>());
    }

    [Fact]
    public void 负载显式为空值时视为无负载()
    {
        var envelope = Deserialize("""{"t":"ok","id":"3","d":null}""");

        Assert.True(envelope.IsOk);
        Assert.False(envelope.HasData);
        Assert.Null(envelope.Decode<TsspStreamRemovedEvent>());
    }

    [Fact]
    public void 成功与失败判定互斥()
    {
        var ok = Deserialize("""{"t":"ok","id":"1"}""");
        var error = Deserialize("""{"t":"error","id":"1"}""");

        Assert.True(ok.IsOk);
        Assert.False(ok.IsError);
        Assert.True(error.IsError);
        Assert.False(error.IsOk);
    }

    [Fact]
    public void 错误负载解析出退避时长()
    {
        var envelope = Deserialize(
            """{"t":"error","id":"9","d":{"code":"RATE_LIMITED","message":"慢一点","retry_after_ms":2500}}""");

        var payload = envelope.Decode<TsspErrorPayload>();

        Assert.NotNull(payload);
        Assert.Equal(TsspErrors.RateLimited, payload.Code);
        Assert.Equal("慢一点", payload.Message);

        var exception = new TsspException(payload);
        Assert.Equal(TsspErrors.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), exception.RetryAfter);
        Assert.False(exception.RequiresRehello);
        Assert.Contains("慢一点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未给退避时长时异常不带退避()
    {
        var payload = new TsspErrorPayload { Code = TsspErrors.TokenExpired };
        var exception = new TsspException(payload);

        Assert.Null(exception.RetryAfter);
        Assert.True(exception.RequiresRehello);
        Assert.Equal(TsspErrors.TokenExpired, exception.Message);
    }

    [Fact]
    public void 握手请求序列化为下划线键名()
    {
        var request = new TsspHelloRequest
        {
            ServerAddress = "ts.example.com:9987",
            Uid = "abc=",
            Clid = 42,
            Cid = 7,
            Nonce = "n1",
            Client = new TsspClientInfo { Name = "TeamSpeak9", Version = "0.1", Platform = "Windows" },
            Capabilities = new TsspClientCapabilities
            {
                Modes = [TsspModes.P2P, TsspModes.Sfu],
                VideoCodecs = ["H264"],
                MaxRecvStreams = 4,
            },
        };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, TsspJson.Options));
        var root = document.RootElement;

        Assert.Equal(TsspProtocol.Version, root.GetProperty("protocol").GetInt32());
        Assert.Equal("ts.example.com:9987", root.GetProperty("server_addr").GetString());
        Assert.Equal("abc=", root.GetProperty("uid").GetString());
        Assert.Equal(42, root.GetProperty("clid").GetInt32());
        Assert.Equal(7, root.GetProperty("cid").GetInt64());
        Assert.Equal("n1", root.GetProperty("nonce").GetString());
        Assert.Equal("Windows", root.GetProperty("client").GetProperty("platform").GetString());
        Assert.Equal(4, root.GetProperty("capabilities").GetProperty("max_recv_streams").GetInt32());
        Assert.Equal("p2p", root.GetProperty("capabilities").GetProperty("modes")[0].GetString());
    }

    [Fact]
    public void 握手请求省略未填的可空字段()
    {
        var request = new TsspHelloRequest
        {
            ServerAddress = "127.0.0.1:9987",
            Uid = "uid",
            Clid = 1,
            Cid = 1,
        };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, TsspJson.Options));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("nonce", out _));
        Assert.False(root.TryGetProperty("client", out _));
        Assert.False(root.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public void 握手响应读出会话令牌与服务端能力()
    {
        var envelope = Deserialize(
            """
            {"t":"ok","id":"1","d":{"session_id":"sess","session_token":"tok","expires_at":1700000000000,
            "nonce":"n1","nickname":"张三","server":{"modes":["sfu","p2p"],"default_mode":"p2p",
            "video_codecs":["H264","VP8"],"max_bitrate_kbps":6000,"max_streams_per_channel":4,
            "max_viewers_per_stream":16,"ice_servers":[{"urls":["stun:stun.example.com:3478"]}]}}}
            """);

        var response = envelope.Decode<TsspHelloResponse>();

        Assert.NotNull(response);
        Assert.Equal("sess", response.SessionId);
        Assert.Equal("tok", response.SessionToken);
        Assert.Equal(1700000000000, response.ExpiresAt);
        Assert.Equal("n1", response.Nonce);
        Assert.Equal("张三", response.Nickname);
        Assert.Equal(TsspModes.P2P, response.Server.DefaultMode);
        Assert.Equal(2, response.Server.Modes.Count);
        Assert.Equal(6000, response.Server.MaxBitrateKbps);
        Assert.NotNull(response.Server.IceServers);
        Assert.Equal("stun:stun.example.com:3478", response.Server.IceServers[0].Urls[0]);
    }

    [Fact]
    public void 握手响应缺少服务端字段时给出默认能力()
    {
        var response = Deserialize("""{"t":"ok","id":"1","d":{"session_token":"tok"}}""")
            .Decode<TsspHelloResponse>();

        Assert.NotNull(response);
        Assert.NotNull(response.Server);
        Assert.Equal(TsspModes.Sfu, response.Server.DefaultMode);
        Assert.Empty(response.Server.Modes);
        Assert.Empty(response.Server.VideoCodecs);
        Assert.Null(response.Server.IceServers);
    }

    [Fact]
    public void 开始共享请求写出媒体参数字典()
    {
        var request = new TsspSetupRequest
        {
            Token = "tok",
            Mode = TsspModes.Sfu,
            StreamType = TsspStreamTypes.Screen,
            Accessibility = TsspAccessibility.InviteOnly,
            Name = "主显示器",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TsspStreamProperties.Width] = "1920",
                [TsspStreamProperties.Height] = "1080",
                [TsspStreamProperties.FrameRate] = "30",
                [TsspStreamProperties.Codec] = "H264",
                [TsspStreamProperties.Audio] = "false",
            },
        };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, TsspJson.Options));
        var properties = document.RootElement.GetProperty("properties");

        Assert.Equal("invite_only", document.RootElement.GetProperty("accessibility").GetString());
        Assert.Equal("screen", document.RootElement.GetProperty("stream_type").GetString());
        Assert.Equal("1920", properties.GetProperty("width").GetString());
        Assert.Equal("30", properties.GetProperty("fps").GetString());
        Assert.Equal("false", properties.GetProperty("audio").GetString());
    }

    [Fact]
    public void 点对点模式的空发布指令解析成全空字段()
    {
        var response = Deserialize(
            """{"t":"ok","id":"2","d":{"stream_id":"s1","mode":"p2p","publish":{}}}""")
            .Decode<TsspSetupResponse>();

        Assert.NotNull(response);
        Assert.Equal("s1", response.StreamId);
        Assert.Equal(TsspModes.P2P, response.Mode);
        Assert.NotNull(response.Publish);
        Assert.Null(response.Publish.Offerer);
        Assert.Null(response.Publish.MaxBitrateKbps);
        Assert.Null(response.Publish.VideoCodecs);
    }

    [Fact]
    public void 转发模式的发布指令带上码率与编码交集()
    {
        var response = Deserialize(
            """
            {"t":"ok","id":"2","d":{"stream_id":"s1","mode":"sfu",
            "publish":{"offerer":"publisher","max_bitrate_kbps":4000,"video_codecs":["H264"]}}}
            """)
            .Decode<TsspSetupResponse>();

        Assert.NotNull(response);
        Assert.NotNull(response.Publish);
        Assert.Equal(TsspOfferers.Publisher, response.Publish.Offerer);
        Assert.Equal(4000, response.Publish.MaxBitrateKbps);
        Assert.Equal(["H264"], response.Publish.VideoCodecs);
    }

    [Fact]
    public void 完全没有发布指令时字段为空()
    {
        var response = Deserialize("""{"t":"ok","id":"2","d":{"stream_id":"s1","mode":"sfu"}}""")
            .Decode<TsspSetupResponse>();

        Assert.NotNull(response);
        Assert.Null(response.Publish);
    }

    [Fact]
    public void 列表请求省略频道号()
    {
        var request = new TsspListRequest { Token = "tok" };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, TsspJson.Options));

        Assert.Equal("tok", document.RootElement.GetProperty("token").GetString());
        Assert.False(document.RootElement.TryGetProperty("cid", out _));
    }

    [Fact]
    public void 列表请求给出频道号时写入()
    {
        var request = new TsspListRequest { Token = "tok", Cid = 12 };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, TsspJson.Options));

        Assert.Equal(12, document.RootElement.GetProperty("cid").GetInt64());
    }

    [Fact]
    public void 列表响应缺少数组时给出空集合()
    {
        var response = Deserialize("""{"t":"ok","id":"4","d":{}}""").Decode<TsspListResponse>();

        Assert.NotNull(response);
        Assert.Empty(response.Streams);
    }

    [Fact]
    public void 流描述解析出发布者与媒体参数()
    {
        var response = Deserialize(
            """
            {"t":"ok","id":"4","d":{"streams":[{"stream_id":"s1","cid":7,"mode":"sfu","stream_type":"window",
            "accessibility":"channel","name":"记事本","publisher":{"clid":42,"uid":"abc=","nickname":"张三"},
            "properties":{"width":"1280","height":"720"},"viewer_count":3,"created_at":1700000000000}]}}
            """)
            .Decode<TsspListResponse>();

        Assert.NotNull(response);
        var stream = Assert.Single(response.Streams);
        Assert.Equal("s1", stream.StreamId);
        Assert.Equal(7, stream.Cid);
        Assert.Equal(TsspStreamTypes.Window, stream.StreamType);
        Assert.Equal(TsspAccessibility.Channel, stream.Accessibility);
        Assert.Equal(42, stream.Publisher.Clid);
        Assert.Equal("张三", stream.Publisher.Nickname);
        Assert.NotNull(stream.Properties);
        Assert.Equal("1280", stream.Properties[TsspStreamProperties.Width]);
        Assert.Equal(3, stream.ViewerCount);
    }

    [Fact]
    public void 流事件缺少流对象时不为空引用()
    {
        var added = Deserialize("""{"t":"stream_added","d":{}}""").Decode<TsspStreamEvent>();

        Assert.NotNull(added);
        Assert.NotNull(added.Stream);
        Assert.Equal(string.Empty, added.Stream.StreamId);
        Assert.NotNull(added.Stream.Publisher);
    }

    [Fact]
    public void 邀请制订阅响应处于等待状态()
    {
        var response = Deserialize("""{"t":"ok","id":"5","d":{"stream_id":"s1","state":"pending"}}""")
            .Decode<TsspSubscribeResponse>();

        Assert.NotNull(response);
        Assert.Equal(TsspSubscribeStates.Pending, response.State);
        Assert.Null(response.Mode);
        Assert.Null(response.Peer);
    }

    [Fact]
    public void 点对点订阅响应带出发布者引用()
    {
        var response = Deserialize(
            """
            {"t":"ok","id":"5","d":{"stream_id":"s1","state":"ready","mode":"p2p",
            "peer":{"clid":42,"uid":"abc=","nickname":"张三"}}}
            """)
            .Decode<TsspSubscribeResponse>();

        Assert.NotNull(response);
        Assert.Equal(TsspSubscribeStates.Ready, response.State);
        Assert.Equal(TsspModes.P2P, response.Mode);
        Assert.NotNull(response.Peer);
        Assert.Equal(42, response.Peer.Clid);
    }

    [Fact]
    public void 信令消息省略空的令牌与对端号()
    {
        var message = new TsspSignalingMessage
        {
            StreamId = "s1",
            SignalingType = TsspSignalingTypes.Offer,
            SignalingData = "v=0\r\n",
        };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(message, TsspJson.Options));
        var root = document.RootElement;

        Assert.Equal("offer", root.GetProperty("signaling_type").GetString());
        Assert.Equal("v=0\r\n", root.GetProperty("signaling_data").GetString());
        Assert.False(root.TryGetProperty("token", out _));
        Assert.False(root.TryGetProperty("peer_clid", out _));
        Assert.False(root.TryGetProperty("role", out _));
    }

    [Fact]
    public void 服务端下推的信令靠角色区分方向()
    {
        var envelope = Deserialize(
            """{"t":"signaling","id":"","d":{"stream_id":"s1","role":"publisher","signaling_type":"answer","signaling_data":"v=0"}}""");
        var message = envelope.Decode<TsspSignalingMessage>();

        Assert.Equal(string.Empty, envelope.Id);
        Assert.NotNull(message);
        Assert.Equal(TsspRoles.Publisher, message.Role);
        Assert.Null(message.Token);
        Assert.Null(message.PeerClid);
    }

    [Fact]
    public void 候选地址使用驼峰键名()
    {
        var candidate = new TsspIceCandidate
        {
            Candidate = "candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host",
            SdpMid = "0",
            SdpMLineIndex = 0,
            UsernameFragment = "ufrag",
        };

        var json = JsonSerializer.Serialize(candidate, TsspJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("sdpMid", out _));
        Assert.True(root.TryGetProperty("sdpMLineIndex", out _));
        Assert.True(root.TryGetProperty("usernameFragment", out _));
        Assert.False(root.TryGetProperty("sdp_mid", out _));

        var roundTrip = JsonSerializer.Deserialize<TsspIceCandidate>(json, TsspJson.Options);
        Assert.NotNull(roundTrip);
        Assert.Equal(candidate, roundTrip);
    }

    [Fact]
    public void 候选地址可作为信令数据嵌套传输()
    {
        var inner = JsonSerializer.Serialize(
            new TsspIceCandidate { Candidate = "candidate:1", SdpMLineIndex = 1 },
            TsspJson.Options);
        var message = new TsspSignalingMessage
        {
            Token = "tok",
            StreamId = "s1",
            SignalingType = TsspSignalingTypes.Candidate,
            SignalingData = inner,
        };

        var envelope = TsspEnvelope.Request(TsspTypes.Signaling, "6", message);
        var decoded = Deserialize(System.Text.Encoding.UTF8.GetString(envelope.ToUtf8Bytes()))
            .Decode<TsspSignalingMessage>();

        Assert.NotNull(decoded);
        Assert.Equal(TsspSignalingTypes.Candidate, decoded.SignalingType);
        var candidate = JsonSerializer.Deserialize<TsspIceCandidate>(decoded.SignalingData!, TsspJson.Options);
        Assert.NotNull(candidate);
        Assert.Equal(1, candidate.SdpMLineIndex);
    }

    [Fact]
    public void 续签响应读出新令牌与刷新后的凭据()
    {
        var response = Deserialize(
            """
            {"t":"ok","id":"7","d":{"session_token":"tok2","expires_at":1700000600000,
            "ice_servers":[{"urls":["turn:turn.example.com:3478"],"username":"u","credential":"p","credential_ttl":600}]}}
            """)
            .Decode<TsspRenewResponse>();

        Assert.NotNull(response);
        Assert.Equal("tok2", response.SessionToken);
        Assert.Equal(1700000600000, response.ExpiresAt);
        Assert.NotNull(response.IceServers);
        var ice = Assert.Single(response.IceServers);
        Assert.Equal("u", ice.Username);
        Assert.Equal(600, ice.CredentialTtl);
    }

    [Fact]
    public void 质量上报写出全部指标()
    {
        var report = new TsspStatsReport
        {
            Token = "tok",
            StreamId = "s1",
            Role = TsspRoles.Subscriber,
            BitrateKbps = 3200.5,
            Fps = 29.97,
            PacketLoss = 0.012,
            RttMs = 42.5,
            JitterMs = 3.25,
            FramesDropped = 7,
        };

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(report, TsspJson.Options));
        var root = document.RootElement;

        Assert.Equal("subscriber", root.GetProperty("role").GetString());
        Assert.Equal(3200.5, root.GetProperty("bitrate_kbps").GetDouble());
        Assert.Equal(29.97, root.GetProperty("fps").GetDouble());
        Assert.Equal(0.012, root.GetProperty("packet_loss").GetDouble());
        Assert.Equal(42.5, root.GetProperty("rtt_ms").GetDouble());
        Assert.Equal(3.25, root.GetProperty("jitter_ms").GetDouble());
        Assert.Equal(7, root.GetProperty("frames_dropped").GetInt32());
    }

    [Fact]
    public void 数字以字符串形式下发时也能解析()
    {
        var stream = Deserialize(
            """{"t":"stream_added","d":{"stream":{"stream_id":"s1","cid":"7","viewer_count":"2"}}}""")
            .Decode<TsspStreamEvent>();

        Assert.NotNull(stream);
        Assert.Equal(7, stream.Stream.Cid);
        Assert.Equal(2, stream.Stream.ViewerCount);
    }

    [Fact]
    public void 未知字段被忽略以便向前兼容()
    {
        var response = Deserialize(
            """{"t":"ok","id":"1","d":{"session_token":"tok","future_field":{"nested":true}}}""")
            .Decode<TsspHelloResponse>();

        Assert.NotNull(response);
        Assert.Equal("tok", response.SessionToken);
    }

    [Fact]
    public void 审批请求写出批准标记与原因()
    {
        var accept = new TsspRespondJoinRequest
        {
            Token = "tok",
            StreamId = "s1",
            Clid = 42,
            Accept = true,
        };
        var reject = new TsspRespondJoinRequest
        {
            Token = "tok",
            StreamId = "s1",
            Clid = 42,
            Accept = false,
            Reason = "人数已满",
        };

        using var accepted = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(accept, TsspJson.Options));
        using var rejected = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(reject, TsspJson.Options));

        Assert.True(accepted.RootElement.GetProperty("accept").GetBoolean());
        Assert.False(accepted.RootElement.TryGetProperty("reason", out _));
        Assert.False(rejected.RootElement.GetProperty("accept").GetBoolean());
        Assert.Equal("人数已满", rejected.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void 事件负载覆盖服务端全部推送类型()
    {
        Assert.Equal(
            TsspReasons.Stopped,
            Decode<TsspStreamRemovedEvent>("""{"t":"stream_removed","d":{"stream_id":"s1","reason":"stopped"}}""")
                .Reason);
        Assert.Equal(
            TsspModes.Sfu,
            Decode<TsspSubscribeReadyEvent>("""{"t":"subscribe_ready","d":{"stream_id":"s1","mode":"sfu"}}""").Mode);
        Assert.Equal(
            "张三",
            Decode<TsspJoinRequestEvent>(
                """{"t":"join_request","d":{"stream_id":"s1","clid":42,"uid":"abc=","nickname":"张三"}}""")
                .Nickname);
        Assert.Equal(
            "拒绝",
            Decode<TsspJoinRejectedEvent>("""{"t":"join_rejected","d":{"stream_id":"s1","reason":"拒绝"}}""").Reason);
        Assert.Equal(
            42,
            Decode<TsspPeerEvent>("""{"t":"peer_joined","d":{"stream_id":"s1","clid":42}}""").Clid);
        Assert.Equal(
            TsspReasons.Removed,
            Decode<TsspRemovedFromStreamEvent>(
                """{"t":"removed_from_stream","d":{"stream_id":"s1","reason":"removed"}}""")
                .Reason);
        Assert.Equal(
            1700000000000,
            Decode<TsspTokenExpiringEvent>("""{"t":"token_expiring","d":{"expires_at":1700000000000}}""").ExpiresAt);
        Assert.Equal(
            "s1",
            Decode<TsspStatsRequestEvent>("""{"t":"stats_request","d":{"stream_id":"s1"}}""").StreamId);
        Assert.Equal(
            TsspErrors.TokenExpired,
            Decode<TsspByeEvent>("""{"t":"bye","d":{"code":"TOKEN_EXPIRED","message":"过期"}}""").Code);
    }

    private static T Decode<T>(string json)
        where T : class
    {
        var payload = Deserialize(json).Decode<T>();
        Assert.NotNull(payload);
        return payload;
    }

    private static TsspEnvelope Deserialize(string json)
    {
        var envelope = JsonSerializer.Deserialize<TsspEnvelope>(json, TsspJson.Options);
        Assert.NotNull(envelope);
        return envelope;
    }
}
