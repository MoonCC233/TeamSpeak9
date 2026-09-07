// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming.Tests;

public class TsspProtocolTests
{
    [Fact]
    public void 传输层常量与规范一致()
    {
        Assert.Equal(1, TsspProtocol.Version);
        Assert.Equal("tssp.v1", TsspProtocol.SubProtocol);
        Assert.Equal(10099, TsspProtocol.DefaultPort);
        Assert.Equal("/tssp/v1", TsspProtocol.DefaultPath);
        Assert.Equal(256 * 1024, TsspProtocol.MaxFrameBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), TsspProtocol.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), TsspProtocol.RequestTimeout);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    public void 重连退避按倍增序列并夹在上限(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TsspProtocol.ReconnectDelayFor(attempt));
    }

    [Fact]
    public void 首次失败与非法次数都退回起始间隔()
    {
        Assert.Equal(TsspProtocol.ReconnectBaseDelay, TsspProtocol.ReconnectDelayFor(1));
        Assert.Equal(TsspProtocol.ReconnectBaseDelay, TsspProtocol.ReconnectDelayFor(0));
        Assert.Equal(TsspProtocol.ReconnectBaseDelay, TsspProtocol.ReconnectDelayFor(-5));
    }

    [Fact]
    public void 极大失败次数不溢出且不超过上限()
    {
        Assert.Equal(TsspProtocol.ReconnectMaxDelay, TsspProtocol.ReconnectDelayFor(int.MaxValue));
        Assert.Equal(TsspProtocol.ReconnectMaxDelay, TsspProtocol.ReconnectDelayFor(1000));
    }

    [Fact]
    public void 退避序列单调不减()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var delay = TsspProtocol.ReconnectDelayFor(attempt);
            Assert.True(delay >= previous, $"第 {attempt} 次退避 {delay} 小于上一次 {previous}");
            Assert.True(delay <= TsspProtocol.ReconnectMaxDelay);
            previous = delay;
        }
    }

    [Fact]
    public void 错误码清单与规范逐项对齐()
    {
        string[] expected =
        [
            "BAD_REQUEST",
            "UNSUPPORTED_PROTOCOL",
            "UNKNOWN_SERVER",
            "QUERY_UNAVAILABLE",
            "CLIENT_NOT_FOUND",
            "IDENTITY_MISMATCH",
            "NOT_ALLOWED",
            "RATE_LIMITED",
            "TOKEN_INVALID",
            "TOKEN_EXPIRED",
            "MODE_NOT_SUPPORTED",
            "CODEC_NOT_SUPPORTED",
            "STREAM_NOT_FOUND",
            "NOT_STREAM_OWNER",
            "NOT_SAME_CHANNEL",
            "ALREADY_PUBLISHING",
            "TOO_MANY_STREAMS",
            "TOO_MANY_VIEWERS",
            "JOIN_REJECTED",
            "SIGNALING_FAILED",
            "INTERNAL",
        ];

        Assert.Equal(expected, TsspErrors.All);
        Assert.Equal(expected, expected.Distinct(StringComparer.Ordinal).ToArray());
        Assert.All(TsspErrors.All, code => Assert.False(string.IsNullOrWhiteSpace(code)));
    }

    [Fact]
    public void 错误码全为大写下划线形式()
    {
        foreach (var code in TsspErrors.All)
        {
            Assert.Equal(code.ToUpperInvariant(), code);
            Assert.All(code, ch => Assert.True(char.IsAsciiLetterUpper(ch) || ch == '_', code));
        }
    }

    [Theory]
    [InlineData(TsspErrors.TokenInvalid, true)]
    [InlineData(TsspErrors.TokenExpired, true)]
    [InlineData(TsspErrors.RateLimited, false)]
    [InlineData(TsspErrors.NotAllowed, false)]
    [InlineData(TsspErrors.Internal, false)]
    [InlineData("", false)]
    public void 只有令牌类错误要求重新握手(string code, bool expected)
    {
        Assert.Equal(expected, TsspErrors.RequiresRehello(code));
    }

    [Fact]
    public void 空错误码不要求重新握手()
    {
        Assert.False(TsspErrors.RequiresRehello(null));
        Assert.False(TsspErrors.IsTransient(null));
    }

    [Theory]
    [InlineData(TsspErrors.QueryUnavailable, true)]
    [InlineData(TsspErrors.RateLimited, true)]
    [InlineData(TsspErrors.Internal, true)]
    [InlineData(TsspErrors.BadRequest, false)]
    [InlineData(TsspErrors.NotSameChannel, false)]
    [InlineData(TsspErrors.TokenExpired, false)]
    public void 仅三类错误值得重试(string code, bool expected)
    {
        Assert.Equal(expected, TsspErrors.IsTransient(code));
    }

    [Fact]
    public void 重新握手与可重试互斥()
    {
        foreach (var code in TsspErrors.All)
        {
            Assert.False(
                TsspErrors.RequiresRehello(code) && TsspErrors.IsTransient(code),
                $"{code} 同时被判定为需重新握手与可重试");
        }
    }

    [Fact]
    public void 枚举型常量取值与服务端一致()
    {
        Assert.Equal("sfu", TsspModes.Sfu);
        Assert.Equal("p2p", TsspModes.P2P);
        Assert.Equal("channel", TsspAccessibility.Channel);
        Assert.Equal("invite_only", TsspAccessibility.InviteOnly);
        Assert.Equal("publisher", TsspRoles.Publisher);
        Assert.Equal("subscriber", TsspRoles.Subscriber);
        Assert.Equal("server", TsspOfferers.Server);
        Assert.Equal("pending", TsspSubscribeStates.Pending);
        Assert.Equal("ready", TsspSubscribeStates.Ready);
    }

    [Fact]
    public void 信令子类型覆盖协商全流程()
    {
        Assert.Equal("offer", TsspSignalingTypes.Offer);
        Assert.Equal("answer", TsspSignalingTypes.Answer);
        Assert.Equal("candidate", TsspSignalingTypes.Candidate);
        Assert.Equal("end_of_candidates", TsspSignalingTypes.EndOfCandidates);
        Assert.Equal("restart", TsspSignalingTypes.Restart);
    }

    [Fact]
    public void 流移除原因与服务端字面量一致()
    {
        Assert.Equal("stopped", TsspReasons.Stopped);
        Assert.Equal("unsubscribed", TsspReasons.Unsubscribed);
        Assert.Equal("disconnected", TsspReasons.Disconnected);
        Assert.Equal("channel_changed", TsspReasons.ChannelChanged);
        Assert.Equal("removed", TsspReasons.Removed);
        Assert.Equal("server_shutdown", TsspReasons.ServerShutdown);
        Assert.Equal("failed", TsspReasons.Failed);
        Assert.Equal("rejected", TsspReasons.Rejected);
    }

    [Fact]
    public void 媒体参数键名全为下划线风格()
    {
        string[] keys =
        [
            TsspStreamProperties.Width,
            TsspStreamProperties.Height,
            TsspStreamProperties.FrameRate,
            TsspStreamProperties.Codec,
            TsspStreamProperties.BitrateKbps,
            TsspStreamProperties.Audio,
            TsspStreamProperties.Source,
        ];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            Assert.Equal(key.ToLowerInvariant(), key);
        }

        Assert.Equal("fps", TsspStreamProperties.FrameRate);
        Assert.Equal("bitrate_kbps", TsspStreamProperties.BitrateKbps);
    }
}
