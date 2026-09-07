// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TeamSpeak9.Streaming.Tssp;

/// <summary>
/// TSSP 连接状态，对应 <c>docs/protocol/tssp-v1.md</c> §8 的状态机。
/// </summary>
public enum TsspConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在建立 WebSocket 连接。</summary>
    Connecting,

    /// <summary>WebSocket 已打开但尚未鉴权；此状态下只允许发送 <c>hello</c>。</summary>
    Opened,

    /// <summary>已通过 <c>hello</c> 鉴权，可以发送全部请求。</summary>
    Authenticated,

    /// <summary>正在主动关闭。</summary>
    Closing,
}

/// <summary>
/// 旁挂流媒体服务（ts9-stream）的 TSSP v1 信令客户端。
/// </summary>
/// <remarks>
/// <para>
/// 本类型只负责 <b>信令</b>：WebSocket 连接管理、请求/响应关联、服务端事件分发与会话令牌续签。
/// 媒体面（采集、编码、PeerConnection、渲染）由上层的屏幕共享/观看模块负责，本类型不引用任何
/// WebRTC 实现。
/// </para>
/// <para>
/// 生命周期：<see cref="ConnectAsync"/> 会启动一个后台监管循环，负责「连接 → hello → 收包 →
/// 断链 → 退避重连」。首次 <c>hello</c> 失败且错误码不可重试时 <see cref="ConnectAsync"/> 直接抛出；
/// 之后的任何断链都会自动重连，并在每次重连时通过 <c>helloFactory</c> 重新取一次最新的
/// <see cref="TsspHelloRequest"/>（协议 §4 规定令牌与 WebSocket 连接绑定，换连接必须重新 hello）。
/// </para>
/// <para>
/// <b>线程模型：</b>除 <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/> 外，所有事件都在
/// 线程池线程上触发。消费方若要更新 WPF 绑定的集合或视图模型，必须自行经
/// <c>IUiDispatcher</c> 调度回 UI 线程。
/// </para>
/// <para>
/// <b>时序陷阱：</b>服务端在 <c>setup</c> 的 <c>ok</c> 响应<i>之前</i>就可能推送第一条
/// <c>signaling</c>（SFU 的 <c>AddPublisher</c> 早于 <c>replyOK</c>）。因此调用
/// <see cref="SetupAsync"/> 之前必须先订阅 <see cref="SignalingReceived"/>。
/// </para>
/// </remarks>
public sealed class TsspClient : IAsyncDisposable
{
    /// <summary>接收缓冲区大小；一帧最大 <see cref="TsspProtocol.MaxFrameBytes"/>，分多次读入。</summary>
    private const int ReceiveChunkBytes = 16 * 1024;

    /// <summary>主动关闭时等待关闭握手的时间。</summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>客户端侧 WebSocket Ping 间隔，与协议 §2 服务端 20 秒保活对齐。</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    private readonly ILogger<TsspClient> log;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TsspEnvelope>> pending =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();

    private ClientWebSocket? socket;
    private Task? supervisor;
    private Func<CancellationToken, ValueTask<TsspHelloRequest>>? helloFactory;
    private Func<CancellationToken, ValueTask<TsspRenewRequest>>? renewFactory;
    private Uri? endpoint;
    private long requestSequence;
    private long lastTrafficTicks;
    private TsspConnectionState state = TsspConnectionState.Disconnected;
    private volatile string? token;
    private volatile TsspHelloResponse? session;
    private bool disposed;

    /// <summary>创建一个尚未连接的 TSSP 客户端。</summary>
    /// <param name="log">日志记录器。</param>
    public TsspClient(ILogger<TsspClient> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    /// <summary>连接状态发生变化。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspConnectionState>? StateChanged;

    /// <summary>
    /// 收到服务端推送的 <c>signaling</c>（SDP / ICE）。<b>在线程池线程上触发。</b>
    /// </summary>
    /// <remarks>
    /// SFU 模式下服务端推送的 <c>signaling</c> 恒不带 <c>peer_clid</c>，消费方必须依据
    /// <c>Role</c>（<c>publisher</c> / <c>subscriber</c>）区分该消息属于「我的发布」还是「我的订阅」。
    /// </remarks>
    public event EventHandler<TsspSignalingMessage>? SignalingReceived;

    /// <summary>同频道内新增了一路流。<b>在线程池线程上触发。</b>发布者不会收到自己的这个事件。</summary>
    public event EventHandler<TsspStreamEvent>? StreamAdded;

    /// <summary>某路流的属性或观众数发生变化。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspStreamEvent>? StreamUpdated;

    /// <summary>某路流已结束。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspStreamRemovedEvent>? StreamRemoved;

    /// <summary>先前处于 <c>pending</c> 的订阅已被发布者批准。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspSubscribeReadyEvent>? SubscribeReady;

    /// <summary>有人申请观看本机发布的 <c>invite_only</c> 流。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspJoinRequestEvent>? JoinRequested;

    /// <summary>本机的观看申请被发布者拒绝。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspJoinRejectedEvent>? JoinRejected;

    /// <summary>P2P 模式下有观众加入。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspPeerEvent>? PeerJoined;

    /// <summary>P2P 模式下有观众离开。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspPeerEvent>? PeerLeft;

    /// <summary>本机被发布者或服务端移出某路流。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspRemovedFromStreamEvent>? RemovedFromStream;

    /// <summary>
    /// 会话令牌即将过期（协议 §4 规定过期前 2 分钟推送）。<b>在线程池线程上触发。</b>
    /// </summary>
    /// <remarks>
    /// 若 <see cref="ConnectAsync"/> 时提供了 <c>renewFactory</c>，客户端会在触发该事件的同时
    /// 自动发起 <c>renew</c>，消费方无需处理。
    /// </remarks>
    public event EventHandler<TsspTokenExpiringEvent>? TokenExpiring;

    /// <summary>服务端索取媒体统计。<b>在线程池线程上触发。</b>消费方应回以 <see cref="ReportStatsAsync"/>。</summary>
    public event EventHandler<TsspStatsRequestEvent>? StatsRequested;

    /// <summary>服务端主动告别（下线、被踢、tsserver 掉线等）。<b>在线程池线程上触发。</b></summary>
    public event EventHandler<TsspByeEvent>? Bye;

    /// <summary>当前连接状态。</summary>
    public TsspConnectionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>是否已完成 <c>hello</c> 鉴权。</summary>
    public bool IsAuthenticated => State is TsspConnectionState.Authenticated;

    /// <summary>最近一次 <c>hello</c> 的响应（含服务端能力与 ICE 服务器）；未鉴权时为 <see langword="null"/>。</summary>
    public TsspHelloResponse? Session => session;

    /// <summary>当前连接的端点；尚未调用 <see cref="ConnectAsync"/> 时为 <see langword="null"/>。</summary>
    public Uri? Endpoint => endpoint;

    /// <summary>最近一次收到任意帧的时刻（UTC），用于诊断空闲超时。</summary>
    public DateTimeOffset LastTraffic => new(Interlocked.Read(ref lastTrafficTicks), TimeSpan.Zero);

    /// <summary>
    /// 连接指定端点并完成首次 <c>hello</c> 鉴权，随后在后台维持连接（断链自动重连）。
    /// </summary>
    /// <param name="target">TSSP 端点，应由 <see cref="TsspEndpointResolver"/> 归一化得到。</param>
    /// <param name="hello">
    /// <c>hello</c> 请求工厂。每次（含首次）建立连接都会调用一次，实现方应返回<b>当时</b>真实的
    /// <c>clid</c> / <c>cid</c> / <c>nonce</c>。
    /// </param>
    /// <param name="renew">
    /// 可选的 <c>renew</c> 请求工厂。提供后，收到 <c>token_expiring</c> 或换频道时可自动续签。
    /// 实现方必须返回与服务端 ServerQuery 查询结果一致的 <c>cid</c> / <c>clid</c>，否则服务端会拒绝。
    /// </param>
    /// <param name="cancellationToken">取消首次连接的等待。</param>
    /// <returns>首次 <c>hello</c> 的响应。</returns>
    public async Task<TsspHelloResponse> ConnectAsync(
        Uri target,
        Func<CancellationToken, ValueTask<TsspHelloRequest>> hello,
        Func<CancellationToken, ValueTask<TsspRenewRequest>>? renew = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(hello);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException("TSSP 客户端已关闭，请新建实例。");
        }

        if (supervisor is not null)
        {
            throw new InvalidOperationException("TSSP 客户端已经启动。");
        }

        endpoint = target;
        helloFactory = hello;
        renewFactory = renew;

        var ready = new TaskCompletionSource<TsspHelloResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor = Task.Run(() => SuperviseAsync(ready), CancellationToken.None);

        try
        {
            return await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "TSSP 首次连接 {Endpoint} 失败", target);
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>主动断开连接并停止后台重连。可重复调用。</summary>
    /// <param name="cancellationToken">保留参数；关闭流程本身有独立超时，不会无限等待。</param>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var running = supervisor;
        if (running is null && lifetime.IsCancellationRequested)
        {
            return;
        }

        SetState(TsspConnectionState.Closing);

        if (!lifetime.IsCancellationRequested)
        {
            lifetime.Cancel();
        }

        await TryCloseHandshakeAsync(socket).ConfigureAwait(false);

        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "TSSP 监管循环退出时抛出异常");
            }

            supervisor = null;
        }

        SetState(TsspConnectionState.Disconnected);
    }

    /// <summary>发送 <c>hello</c> 完成鉴权。通常由内部监管循环调用，仅在自定义握手流程时才需要手动调用。</summary>
    public async Task<TsspHelloResponse> HelloAsync(
        TsspHelloRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(TsspTypes.Hello, request, cancellationToken).ConfigureAwait(false);
        var response = Require<TsspHelloResponse>(reply, TsspTypes.Hello);

        token = response.SessionToken;
        session = response;
        SetState(TsspConnectionState.Authenticated);
        return response;
    }

    /// <summary>
    /// 申请发布一路流。<b>调用前必须先订阅 <see cref="SignalingReceived"/></b>，服务端可能在本方法返回前
    /// 就推送第一条 <c>signaling</c>。
    /// </summary>
    public async Task<TsspSetupResponse> SetupAsync(
        TsspSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Setup,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspSetupResponse>(reply, TsspTypes.Setup);
    }

    /// <summary>更新本机已发布流的属性（分辨率、帧率、码率等）。</summary>
    public async Task<TsspStreamEvent> UpdateAsync(
        TsspUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Update,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspStreamEvent>(reply, TsspTypes.Update);
    }

    /// <summary>停止本机发布的流。</summary>
    public async Task<TsspStreamRemovedEvent> StopAsync(
        TsspStopRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Stop,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspStreamRemovedEvent>(reply, TsspTypes.Stop);
    }

    /// <summary>列出可观看的流。</summary>
    public async Task<TsspListResponse> ListAsync(
        TsspListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.List,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspListResponse>(reply, TsspTypes.List);
    }

    /// <summary>
    /// 订阅一路流。<b>调用前必须先订阅 <see cref="SignalingReceived"/></b>。
    /// </summary>
    /// <remarks>
    /// 若目标流是 <c>invite_only</c>，响应的状态为 <c>pending</c>，需等待
    /// <see cref="SubscribeReady"/> 或 <see cref="JoinRejected"/>。
    /// </remarks>
    public async Task<TsspSubscribeResponse> SubscribeAsync(
        TsspSubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Subscribe,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspSubscribeResponse>(reply, TsspTypes.Subscribe);
    }

    /// <summary>退订一路流。</summary>
    public async Task<TsspStreamRemovedEvent> UnsubscribeAsync(
        TsspUnsubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Unsubscribe,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        return Require<TsspStreamRemovedEvent>(reply, TsspTypes.Unsubscribe);
    }

    /// <summary>批准或拒绝一个观看申请。服务端的 <c>ok</c> 响应没有负载。</summary>
    public async Task RespondJoinAsync(
        TsspRespondJoinRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ExchangeAsync(
            TsspTypes.RespondJoin,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送 SDP / ICE 信令。服务端的 <c>ok</c> 响应没有负载。</summary>
    public async Task SendSignalingAsync(
        TsspSignalingMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await ExchangeAsync(
            TsspTypes.Signaling,
            message with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>续签会话令牌。换频道后必须调用（协议 §4）。</summary>
    public async Task<TsspRenewResponse> RenewAsync(
        TsspRenewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reply = await ExchangeAsync(
            TsspTypes.Renew,
            request with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);

        var response = Require<TsspRenewResponse>(reply, TsspTypes.Renew);
        token = response.SessionToken;
        return response;
    }

    /// <summary>上报媒体统计。服务端的 <c>ok</c> 响应没有负载。</summary>
    public async Task ReportStatsAsync(
        TsspStatsReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        await ExchangeAsync(
            TsspTypes.Stats,
            report with { Token = RequireToken() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "TSSP 释放时断开连接失败");
        }

        FailPending(new ObjectDisposedException(nameof(TsspClient)));

        socket?.Dispose();
        socket = null;
        lifetime.Dispose();
        sendGate.Dispose();
    }

    /// <summary>后台监管循环：连接 → hello → 收包 → 断链 → 退避重连。</summary>
    private async Task SuperviseAsync(TaskCompletionSource<TsspHelloResponse> ready)
    {
        var attempt = 0;

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ready, lifetime.Token).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ready.Task.IsCompleted && ex is TsspException fatal && !TsspErrors.IsTransient(fatal.Code))
                {
                    ready.TrySetException(fatal);
                    break;
                }

                log.LogWarning(ex, "TSSP 会话中断，将进行第 {Attempt} 次重连", attempt + 1);
            }
            finally
            {
                await TeardownSessionAsync().ConfigureAwait(false);
            }

            if (lifetime.IsCancellationRequested)
            {
                break;
            }

            var delay = TsspProtocol.ReconnectDelayFor(attempt);
            attempt++;

            try
            {
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ready.TrySetCanceled();
    }

    /// <summary>建立一次完整的会话，直到 WebSocket 断开为止。</summary>
    private async Task RunSessionAsync(
        TaskCompletionSource<TsspHelloResponse> ready,
        CancellationToken cancellationToken)
    {
        var ws = await OpenSocketAsync(cancellationToken).ConfigureAwait(false);

        // 先启动收包循环再发 hello：响应必须由收包循环关联回来。
        var pump = PumpAsync(ws, cancellationToken);

        try
        {
            var factory = helloFactory
                ?? throw new InvalidOperationException("缺少 hello 请求工厂。");

            var request = await factory(cancellationToken).ConfigureAwait(false);
            var response = await HelloAsync(request, cancellationToken).ConfigureAwait(false);
            ready.TrySetResult(response);
        }
        catch
        {
            await TryCloseHandshakeAsync(ws).ConfigureAwait(false);
            await SwallowAsync(pump).ConfigureAwait(false);
            throw;
        }

        await pump.ConfigureAwait(false);
    }

    private async Task<ClientWebSocket> OpenSocketAsync(CancellationToken cancellationToken)
    {
        var target = endpoint ?? throw new InvalidOperationException("尚未设置 TSSP 端点。");

        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(TsspProtocol.SubProtocol);
        ws.Options.KeepAliveInterval = KeepAliveInterval;

        SetState(TsspConnectionState.Connecting);

        try
        {
            await ws.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            throw;
        }

        socket = ws;
        MarkTraffic();
        SetState(TsspConnectionState.Opened);
        log.LogInformation("TSSP 已连接 {Endpoint}，协商子协议 {SubProtocol}", target, ws.SubProtocol);
        return ws;
    }

    /// <summary>收包循环：按帧读取、校验大小、解析并分发。</summary>
    private async Task PumpAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var chunk = new byte[ReceiveChunkBytes];
        using var frame = new MemoryStream(ReceiveChunkBytes);

        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult result;

            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TsspProtocol.IdleTimeout);
                result = await ws.ReceiveAsync(chunk.AsMemory(), idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TsspException(
                    TsspErrors.Internal,
                    $"TSSP 连接 {TsspProtocol.IdleTimeout.TotalSeconds:0} 秒无数据，判定为断链。");
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                log.LogInformation(
                    "TSSP 服务端关闭连接：{Status} {Description}",
                    ws.CloseStatus,
                    ws.CloseStatusDescription);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                throw new TsspException(TsspErrors.Internal, "TSSP 只接受文本帧，收到二进制帧。");
            }

            frame.Write(chunk, 0, result.Count);

            if (frame.Length > TsspProtocol.MaxFrameBytes)
            {
                throw new TsspException(
                    TsspErrors.Internal,
                    $"TSSP 收到超过 {TsspProtocol.MaxFrameBytes} 字节的帧，已中断连接。");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            MarkTraffic();
            Dispatch(frame.GetBuffer().AsSpan(0, (int)frame.Length));
            frame.SetLength(0);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        TsspEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<TsspEnvelope>(utf8, TsspJson.Options);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "TSSP 收到无法解析的帧，已忽略");
            return;
        }

        if (envelope is null)
        {
            return;
        }

        if (envelope.Id is { Length: > 0 } id && pending.TryRemove(id, out var slot))
        {
            slot.TrySetResult(envelope);
            return;
        }

        RaiseServerMessage(envelope);
    }

    private void RaiseServerMessage(TsspEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case TsspTypes.Signaling:
                Raise(SignalingReceived, envelope.Decode<TsspSignalingMessage>());
                break;

            case TsspEvents.StreamAdded:
                Raise(StreamAdded, envelope.Decode<TsspStreamEvent>());
                break;

            case TsspEvents.StreamUpdated:
                Raise(StreamUpdated, envelope.Decode<TsspStreamEvent>());
                break;

            case TsspEvents.StreamRemoved:
                Raise(StreamRemoved, envelope.Decode<TsspStreamRemovedEvent>());
                break;

            case TsspEvents.SubscribeReady:
                Raise(SubscribeReady, envelope.Decode<TsspSubscribeReadyEvent>());
                break;

            case TsspEvents.JoinRequest:
                Raise(JoinRequested, envelope.Decode<TsspJoinRequestEvent>());
                break;

            case TsspEvents.JoinRejected:
                Raise(JoinRejected, envelope.Decode<TsspJoinRejectedEvent>());
                break;

            case TsspEvents.PeerJoined:
                Raise(PeerJoined, envelope.Decode<TsspPeerEvent>());
                break;

            case TsspEvents.PeerLeft:
                Raise(PeerLeft, envelope.Decode<TsspPeerEvent>());
                break;

            case TsspEvents.RemovedFromStream:
                Raise(RemovedFromStream, envelope.Decode<TsspRemovedFromStreamEvent>());
                break;

            case TsspEvents.TokenExpiring:
                var expiring = envelope.Decode<TsspTokenExpiringEvent>();
                Raise(TokenExpiring, expiring);
                _ = RenewSilentlyAsync();
                break;

            case TsspEvents.StatsRequest:
                Raise(StatsRequested, envelope.Decode<TsspStatsRequestEvent>());
                break;

            case TsspEvents.Bye:
                Raise(Bye, envelope.Decode<TsspByeEvent>());
                break;

            case TsspTypes.Ok:
            case TsspTypes.Error:
                log.LogDebug("TSSP 收到无法关联的 {Type} 响应，已忽略", envelope.Type);
                break;

            default:
                log.LogDebug("TSSP 收到未知消息类型 {Type}，已忽略", envelope.Type);
                break;
        }
    }

    /// <summary>收到 <c>token_expiring</c> 后自动续签；失败只记日志，等待下一次重连兜底。</summary>
    private async Task RenewSilentlyAsync()
    {
        var factory = renewFactory;
        if (factory is null)
        {
            return;
        }

        try
        {
            var request = await factory(lifetime.Token).ConfigureAwait(false);
            await RenewAsync(request, lifetime.Token).ConfigureAwait(false);
            log.LogInformation("TSSP 会话令牌已自动续签");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "TSSP 会话令牌自动续签失败");
        }
    }

    /// <summary>发送请求并等待与之关联的 <c>ok</c> / <c>error</c> 响应。</summary>
    private async Task<TsspEnvelope> ExchangeAsync<TPayload>(
        string type,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var ws = socket
            ?? throw new TsspException(TsspErrors.Internal, "TSSP 尚未连接。");

        var isHello = string.Equals(type, TsspTypes.Hello, StringComparison.Ordinal);
        if (!isHello && State is not TsspConnectionState.Authenticated)
        {
            throw new TsspException(TsspErrors.TokenInvalid, "TSSP 会话尚未完成 hello 鉴权。");
        }

        var id = NextRequestId();
        var slot = new TaskCompletionSource<TsspEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!pending.TryAdd(id, slot))
        {
            throw new InvalidOperationException($"TSSP 请求编号 {id} 冲突。");
        }

        try
        {
            await SendAsync(ws, TsspEnvelope.Request(type, id, payload), cancellationToken).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(TsspProtocol.RequestTimeout);

            var reply = await slot.Task.WaitAsync(deadline.Token).ConfigureAwait(false);

            if (reply.IsError)
            {
                var error = reply.Decode<TsspErrorPayload>();
                throw error is null
                    ? new TsspException(TsspErrors.Internal, $"TSSP 请求 {type} 失败，且错误体无法解析。")
                    : new TsspException(error);
            }

            return reply;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (lifetime.IsCancellationRequested)
            {
                throw new TsspException(TsspErrors.Internal, $"TSSP 已关闭，请求 {type} 未完成。");
            }

            throw new TimeoutException(
                $"TSSP 请求 {type} 超过 {TsspProtocol.RequestTimeout.TotalSeconds:0} 秒未收到响应。");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>串行化发送，保证同一时刻只有一个 WebSocket 写操作。</summary>
    private async Task SendAsync(ClientWebSocket ws, TsspEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.ToUtf8Bytes();

        if (payload.Length > TsspProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"待发送的 {envelope.Type} 帧为 {payload.Length} 字节，超出上限 {TsspProtocol.MaxFrameBytes} 字节。");
        }

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task TeardownSessionAsync()
    {
        var current = socket;
        socket = null;
        token = null;
        session = null;

        FailPending(new TsspException(TsspErrors.Internal, "TSSP 连接已断开。"));

        if (current is not null)
        {
            try
            {
                current.Abort();
            }
            catch (Exception ex)
            {
                log.LogTrace(ex, "TSSP 中止 WebSocket 时抛出异常");
            }

            current.Dispose();
        }

        if (!lifetime.IsCancellationRequested)
        {
            SetState(TsspConnectionState.Disconnected);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task TryCloseHandshakeAsync(ClientWebSocket? ws)
    {
        if (ws is null || ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "TSSP 关闭握手失败，将直接中止连接");
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 握手失败时收包循环的异常无意义，调用方已经拿到更精确的错误。
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var key in pending.Keys)
        {
            if (pending.TryRemove(key, out var slot))
            {
                slot.TrySetException(error);
            }
        }
    }

    private void Raise<TPayload>(EventHandler<TPayload>? handler, TPayload? payload)
        where TPayload : class
    {
        if (handler is null || payload is null)
        {
            return;
        }

        try
        {
            handler(this, payload);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TSSP 事件 {Event} 的处理器抛出异常", typeof(TPayload).Name);
        }
    }

    private void SetState(TsspConnectionState next)
    {
        TsspConnectionState previous;

        lock (gate)
        {
            if (state == next)
            {
                return;
            }

            previous = state;
            state = next;
        }

        log.LogDebug("TSSP 状态 {Previous} → {Next}", previous, next);

        try
        {
            StateChanged?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TSSP 状态变更事件的处理器抛出异常");
        }
    }

    private static TResponse Require<TResponse>(TsspEnvelope reply, string type)
        where TResponse : class
    {
        return reply.Decode<TResponse>()
            ?? throw new TsspException(TsspErrors.Internal, $"TSSP 请求 {type} 的响应缺少负载。");
    }

    private string RequireToken()
    {
        return token
            ?? throw new TsspException(TsspErrors.TokenInvalid, "TSSP 会话未鉴权，无可用令牌。");
    }

    private string NextRequestId()
    {
        return Interlocked.Increment(ref requestSequence).ToString(CultureInfo.InvariantCulture);
    }

    private void MarkTraffic()
    {
        Interlocked.Exchange(ref lastTrafficTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
