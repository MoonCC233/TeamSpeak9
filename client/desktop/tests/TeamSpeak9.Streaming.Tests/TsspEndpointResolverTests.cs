// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Settings;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming.Tests;

/// <summary>覆盖 <see cref="TsspEndpointResolver"/> 的规范化、回退与信任判定逻辑（规范 §2.1）。</summary>
public sealed class TsspEndpointResolverTests
{
    [Fact]
    public void 服务器属性名与规范一致()
    {
        Assert.Equal("virtualserver_sfu_endpoint", TsspEndpointResolver.ServerPropertyName);
    }

    [Fact]
    public void 裸主机名补全协议端口与路径()
    {
        var endpoint = EndpointOf(TsspEndpointResolver.Resolve("stream.example.com", new StreamSettings()));

        Assert.Equal("wss", endpoint.Scheme);
        Assert.Equal("stream.example.com", endpoint.Host);
        Assert.Equal(TsspProtocol.DefaultPort, endpoint.Port);
        Assert.Equal(TsspProtocol.DefaultPath, endpoint.AbsolutePath);
    }

    [Fact]
    public void 显式端口与路径不会被覆盖()
    {
        var endpoint = EndpointOf(
            TsspEndpointResolver.Resolve("wss://stream.example.com:20000/custom/path", new StreamSettings()));

        Assert.Equal(20000, endpoint.Port);
        Assert.Equal("/custom/path", endpoint.AbsolutePath);
    }

    [Fact]
    public void 协议名大小写不敏感且信任键小写化()
    {
        var resolution = TsspEndpointResolver.Resolve("WSS://Stream.EXAMPLE.com", new StreamSettings());
        var endpoint = EndpointOf(resolution);

        Assert.Equal("wss", endpoint.Scheme);
        Assert.Equal("stream.example.com:" + TsspProtocol.DefaultPort, resolution.TrustKey);
    }

    [Fact]
    public void 查询串与片段被清空()
    {
        var endpoint = EndpointOf(
            TsspEndpointResolver.Resolve("wss://stream.example.com/tssp/v1?token=abc#frag", new StreamSettings()));

        Assert.Equal(string.Empty, endpoint.Query);
        Assert.Equal(string.Empty, endpoint.Fragment);
        Assert.Equal("/tssp/v1", endpoint.AbsolutePath);
    }

    [Fact]
    public void 空白地址按未填写处理()
    {
        var resolution = TsspEndpointResolver.Resolve("   ", new StreamSettings());

        Assert.Equal(TsspEndpointProblem.Missing, resolution.Problem);
    }

    [Fact]
    public void IPv6地址无显式端口时补默认端口()
    {
        var resolution = TsspEndpointResolver.Resolve("wss://[::1]", new StreamSettings());
        var endpoint = EndpointOf(resolution);

        Assert.Equal(UriHostNameType.IPv6, endpoint.HostNameType);
        Assert.Equal(TsspProtocol.DefaultPort, endpoint.Port);
        Assert.Equal(TsspProtocol.DefaultPath, endpoint.AbsolutePath);
        Assert.Equal("[::1]:" + TsspProtocol.DefaultPort, resolution.TrustKey);
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("[::1]:10099")]
    [InlineData("[0:0:0:0:0:0:0:1]:10099")]
    [InlineData("[0000:0000:0000:0000:0000:0000:0000:0001]:10099")]
    [InlineData("wss://[::1]:10099/tssp/v1")]
    public void 已确认的IPv6主机不再需要确认(string entry)
    {
        var settings = new StreamSettings();
        settings.TrustedEndpoints.Add(entry);

        var resolution = TsspEndpointResolver.Resolve("wss://[::1]", settings);

        Assert.True(resolution.Success);
        Assert.False(resolution.RequiresConfirmation);
    }

    [Fact]
    public void IPv6端口不同的主机仍需确认()
    {
        var settings = new StreamSettings();
        settings.TrustedEndpoints.Add("[::1]:20000");

        var resolution = TsspEndpointResolver.Resolve("wss://[::1]", settings);

        Assert.True(resolution.RequiresConfirmation);
    }

    [Fact]
    public void IPv6地址的显式端口不会被覆盖()
    {
        var endpoint = EndpointOf(TsspEndpointResolver.Resolve("wss://[::1]:20000", new StreamSettings()));

        Assert.Equal(UriHostNameType.IPv6, endpoint.HostNameType);
        Assert.Equal(20000, endpoint.Port);
    }

    [Fact]
    public void 明文协议默认被拒绝()
    {
        var resolution = TsspEndpointResolver.Resolve("ws://stream.example.com", new StreamSettings());

        Assert.False(resolution.Success);
        Assert.Equal(TsspEndpointProblem.InsecureScheme, resolution.Problem);
        Assert.Null(resolution.Endpoint);
        Assert.Contains("wss://", MessageOf(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public void 开发模式下允许明文协议()
    {
        var endpoint = EndpointOf(
            TsspEndpointResolver.Resolve("ws://127.0.0.1", new StreamSettings(), allowInsecureScheme: true));

        Assert.Equal("ws", endpoint.Scheme);
        Assert.Equal(TsspProtocol.DefaultPort, endpoint.Port);
    }

    [Fact]
    public void 非WebSocket协议被拒绝()
    {
        var resolution = TsspEndpointResolver.Resolve("https://stream.example.com", new StreamSettings());

        Assert.Equal(TsspEndpointProblem.UnsupportedScheme, resolution.Problem);
        Assert.Contains("https", MessageOf(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public void 畸形地址被拒绝()
    {
        var resolution = TsspEndpointResolver.Resolve("wss://stream.example.com:notaport", new StreamSettings());

        Assert.Equal(TsspEndpointProblem.Malformed, resolution.Problem);
        Assert.Contains("wss://stream.example.com:notaport", MessageOf(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public void 公告地址优先于手工地址()
    {
        var settings = new StreamSettings { ManualEndpoint = "wss://manual.example.com" };

        var resolution = TsspEndpointResolver.Resolve("wss://advertised.example.com", settings);

        Assert.Equal(TsspEndpointSource.ServerAdvertised, resolution.Source);
        Assert.Equal("advertised.example.com", EndpointOf(resolution).Host);
    }

    [Fact]
    public void 公告地址缺失时回退手工地址()
    {
        var settings = new StreamSettings { ManualEndpoint = "manual.example.com" };

        var resolution = TsspEndpointResolver.Resolve(null, settings);

        Assert.Equal(TsspEndpointSource.ManualSetting, resolution.Source);
        Assert.Equal("manual.example.com", EndpointOf(resolution).Host);
    }

    [Fact]
    public void 公告地址非法时回退手工地址()
    {
        var settings = new StreamSettings { ManualEndpoint = "wss://manual.example.com" };

        var resolution = TsspEndpointResolver.Resolve("ftp://broken.example.com", settings);

        Assert.Equal(TsspEndpointSource.ManualSetting, resolution.Source);
        Assert.Equal("manual.example.com", EndpointOf(resolution).Host);
    }

    [Fact]
    public void 两者皆空时提示填写设置()
    {
        var resolution = TsspEndpointResolver.Resolve(null, new StreamSettings());

        Assert.False(resolution.Success);
        Assert.Equal(TsspEndpointProblem.Missing, resolution.Problem);
        Assert.Equal(TsspEndpointSource.ManualSetting, resolution.Source);
        Assert.Contains("请在设置中填写", MessageOf(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public void 公告非法且手工为空时上报公告的问题()
    {
        var resolution = TsspEndpointResolver.Resolve("ws://advertised.example.com", new StreamSettings());

        Assert.Equal(TsspEndpointSource.ServerAdvertised, resolution.Source);
        Assert.Equal(TsspEndpointProblem.InsecureScheme, resolution.Problem);
    }

    [Fact]
    public void 手工地址非法时上报手工的问题()
    {
        var settings = new StreamSettings { ManualEndpoint = "ws://manual.example.com" };

        var resolution = TsspEndpointResolver.Resolve(null, settings);

        Assert.Equal(TsspEndpointSource.ManualSetting, resolution.Source);
        Assert.Equal(TsspEndpointProblem.InsecureScheme, resolution.Problem);
    }

    [Fact]
    public void 首次连接的主机需要用户确认()
    {
        var resolution = TsspEndpointResolver.Resolve("stream.example.com", new StreamSettings());

        Assert.True(resolution.RequiresConfirmation);
        Assert.Equal("stream.example.com:" + TsspProtocol.DefaultPort, resolution.TrustKey);
    }

    [Theory]
    [InlineData("stream.example.com:10099")]
    [InlineData("STREAM.Example.COM:10099")]
    [InlineData("stream.example.com")]
    [InlineData("wss://stream.example.com:10099/tssp/v1")]
    [InlineData("wss://stream.example.com/tssp/v1")]
    public void 已确认的主机不再需要确认(string entry)
    {
        var settings = new StreamSettings();
        settings.TrustedEndpoints.Add("   ");
        settings.TrustedEndpoints.Add(entry);

        var resolution = TsspEndpointResolver.Resolve("stream.example.com", settings);

        Assert.True(resolution.Success);
        Assert.False(resolution.RequiresConfirmation);
    }

    [Fact]
    public void 端口不同的主机仍需确认()
    {
        var settings = new StreamSettings();
        settings.TrustedEndpoints.Add("stream.example.com:20000");

        var resolution = TsspEndpointResolver.Resolve("stream.example.com", settings);

        Assert.True(resolution.RequiresConfirmation);
    }

    [Fact]
    public void 信任键由主机与端口组成()
    {
        Assert.Equal(
            "stream.example.com:20000",
            TsspEndpointResolver.TrustKeyFor(new Uri("wss://Stream.Example.com:20000/tssp/v1")));
    }

    [Fact]
    public void IPv6信任键使用紧凑写法()
    {
        Assert.Equal(
            "[::1]:20000",
            TsspEndpointResolver.TrustKeyFor(new Uri("wss://[0000:0000:0000:0000:0000:0000:0000:0001]:20000/tssp/v1")));
    }

    [Fact]
    public void 重复信任同一端点不产生重复条目()
    {
        var settings = new StreamSettings();
        var endpoint = new Uri("wss://stream.example.com:10099/tssp/v1");

        Assert.True(TsspEndpointResolver.Trust(settings, endpoint));
        Assert.False(TsspEndpointResolver.Trust(settings, endpoint));
        Assert.Equal("stream.example.com:10099", Assert.Single(settings.TrustedEndpoints));
    }

    [Fact]
    public void 信任后解析结果不再要求确认()
    {
        var settings = new StreamSettings();
        var first = TsspEndpointResolver.Resolve("stream.example.com", settings);
        Assert.True(first.RequiresConfirmation);

        Assert.True(TsspEndpointResolver.Trust(settings, EndpointOf(first)));

        var second = TsspEndpointResolver.Resolve("stream.example.com", settings);
        Assert.False(second.RequiresConfirmation);
    }

    [Fact]
    public void 兼容早期存成完整URL的信任条目()
    {
        var settings = new StreamSettings();
        settings.TrustedEndpoints.Add("wss://stream.example.com:10099/tssp/v1");

        Assert.False(TsspEndpointResolver.Trust(settings, new Uri("wss://stream.example.com:10099/tssp/v1")));
        Assert.Single(settings.TrustedEndpoints);
    }

    [Fact]
    public void 参数为空时抛出异常()
    {
        Assert.Throws<ArgumentNullException>(() => TsspEndpointResolver.Resolve("stream.example.com", null!));
        Assert.Throws<ArgumentNullException>(() => TsspEndpointResolver.TrustKeyFor(null!));
        Assert.Throws<ArgumentNullException>(
            () => TsspEndpointResolver.Trust(null!, new Uri("wss://stream.example.com:10099/tssp/v1")));
        Assert.Throws<ArgumentNullException>(() => TsspEndpointResolver.Trust(new StreamSettings(), null!));
    }

    private static Uri EndpointOf(TsspEndpointResolution resolution)
    {
        Assert.True(resolution.Success, resolution.Message);
        Assert.NotNull(resolution.Endpoint);
        return resolution.Endpoint!;
    }

    private static string MessageOf(TsspEndpointResolution resolution)
    {
        Assert.NotNull(resolution.Message);
        return resolution.Message!;
    }
}
