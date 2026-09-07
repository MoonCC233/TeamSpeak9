// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Model;
using TeamSpeak9.Core.Threading;
using TSLib.Full;
using TSLib.Messages;

namespace TeamSpeak9.Core.Connection;

/// <summary>
/// One connection to one TeamSpeak server.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="TsFullClient"/> and hides three things the UI must not have to deal with:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Thread affinity.</b> Every TSLib call has to happen on the scheduler thread, and the book may
/// only be read there. All calls go through <see cref="TsSchedulerLoop.InvokeAsync(Action)"/>, and
/// snapshots are built on that thread before being published.
/// </description></item>
/// <item><description>
/// <b>A connect path that can hang.</b> <c>TsFullClient.ChangeState</c> reads its own
/// <c>status</c> field <i>after</i> overwriting it, so the <c>Connecting -&gt; Disconnected</c>
/// branch never completes the connect task. Awaiting <c>Connect</c> on a refused or unreachable
/// server therefore waits forever. We race it against a timeout, and also treat
/// <c>OnDisconnected</c> as a failure signal since that event does still fire.
/// </description></item>
/// <item><description>
/// <b>Notification volume.</b> A server with a few hundred channels sends hundreds of
/// notifications during the initial channel list. Rebuilding and publishing a snapshot per
/// notification would swamp the UI thread, so changes are coalesced on a short timer.
/// </description></item>
/// </list>
/// <para>
/// Events are raised on the UI thread via <see cref="IUiDispatcher"/>. Not thread safe for
/// concurrent <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/> calls; drive it from the
/// UI thread.
/// </para>
/// </remarks>
public sealed class TsConnection : IAsyncDisposable
{
    /// <summary>
    /// How long snapshot rebuilds are batched.
    /// </summary>
    /// <remarks>
    /// Short enough to feel instant, long enough that a burst of notifications collapses into one
    /// rebuild. The scheduler clamps timer intervals to a 10 ms minimum anyway.
    /// </remarks>
    private static readonly TimeSpan SnapshotCoalesceInterval = TimeSpan.FromMilliseconds(80);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxRetryAttempts = 6;

    private readonly TsSchedulerLoop loop;
    private readonly IUiDispatcher ui;
    private readonly ILogger<TsConnection> log;

    private TsFullClient? client;
    private TSLib.Scheduler.TickWorker? snapshotTimer;

    private ConnectionRequest? request;
    private ConnectionState state = ConnectionState.Disconnected;

    /// <summary>Set from the scheduler thread when the book changed and a rebuild is pending.</summary>
    private bool snapshotDirty;

    /// <summary>Guards against a stale retry loop from a previous session.</summary>
    private CancellationTokenSource? sessionCts;

    private int disposed;

    public TsConnection(TsSchedulerLoop loop, IUiDispatcher ui, ILogger<TsConnection> log)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);

        this.loop = loop;
        this.ui = ui;
        this.log = log;
    }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on the UI thread with a fresh snapshot after any server side change.</summary>
    public event EventHandler<ServerSnapshot>? SnapshotChanged;

    /// <summary>Raised on the UI thread for incoming text messages.</summary>
    public event EventHandler<ChatMessage>? MessageReceived;

    /// <summary>Raised on the UI thread when a client pokes us.</summary>
    public event EventHandler<ChatMessage>? Poked;

    /// <summary>Raised on the UI thread for server errors that are not the result of our own command.</summary>
    public event EventHandler<string>? ServerError;

    /// <summary>
    /// Raised on the scheduler thread once a session is usable, so the audio layer can wire its
    /// pipes into the client.
    /// </summary>
    public event Action<TsFullClient>? ClientReady;

    /// <summary>
    /// Raised on the scheduler thread before a session is torn down. Skipped when the scheduler is
    /// already gone, so handlers must also tolerate teardown arriving only through their own dispose.
    /// </summary>
    public event Action<TsFullClient>? ClientClosing;

    /// <summary>Current lifecycle state. Only written on the UI thread.</summary>
    public ConnectionState State => state;

    public bool IsConnected => state == ConnectionState.Connected;

    /// <summary>The most recent snapshot, or <see cref="ServerSnapshot.Empty"/> before the first one.</summary>
    public ServerSnapshot Snapshot { get; private set; } = ServerSnapshot.Empty;

    /// <summary>The request of the current or last session, for reconnect and UI attribution.</summary>
    public ConnectionRequest? CurrentRequest => request;

    /// <summary>
    /// Connects, replacing any existing session.
    /// </summary>
    /// <returns>An error message, or <c>null</c> on success.</returns>
    public async Task<string?> ConnectAsync(ConnectionRequest connectionRequest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionRequest);
        ObjectDisposedException.ThrowIf(disposed != 0, this);

        connectionRequest.Validate();
        await DisconnectAsync().ConfigureAwait(false);

        request = connectionRequest;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts = cts;

        SetState(ConnectionState.Connecting, connectionRequest.Address);

        string? error = await AttemptAsync(connectionRequest, cts.Token).ConfigureAwait(false);
        if (error is null)
        {
            SetState(ConnectionState.Connected);
            await PublishSnapshotAsync().ConfigureAwait(false);
            return null;
        }

        await TeardownAsync().ConfigureAwait(false);
        SetState(ConnectionState.Failed, error);
        return error;
    }

    /// <summary>
    /// Performs one connect attempt.
    /// </summary>
    /// <returns>An error message, or <c>null</c> on success.</returns>
    private async Task<string?> AttemptAsync(ConnectionRequest req, CancellationToken ct)
    {
        var connectionData = req.ToConnectionData();

        // Completed by whichever comes first: TSLib's connect task, or OnDisconnected. The latter
        // is the reliable one, because the connect task is not completed on the failure path.
        var outcome = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        TsFullClient created;
        try
        {
            created = await loop.InvokeAsync(() =>
            {
                var c = new TsFullClient(loop.Scheduler);
                Attach(c, outcome);
                return c;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "创建 TSLib 客户端失败。");
            return "无法初始化连接：" + ex.Message;
        }

        client = created;

        try
        {
            _ = loop.InvokeAsync(async () =>
            {
                try
                {
                    var result = await created.Connect(connectionData).ConfigureAwait(false);
                    outcome.TrySetResult(result.Ok ? null : Describe(result.Error));
                }
                catch (Exception ex)
                {
                    // Connect throws for a bad ConnectionData and can throw out of the packet
                    // handler; without this the outcome task would never complete.
                    log.LogError(ex, "连接过程抛出异常。");
                    outcome.TrySetResult(ex.Message);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            return "调度器已关闭。";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(req.Timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(outcome.Task, timeout).ConfigureAwait(false);
        await timeoutCts.CancelAsync().ConfigureAwait(false);

        if (completed != outcome.Task)
        {
            return ct.IsCancellationRequested
                ? "连接已取消。"
                : $"连接超时（{req.Timeout.TotalSeconds:0.#} 秒）。";
        }

        string? failure = await outcome.Task.ConfigureAwait(false);
        if (failure is not null)
            return failure;

        // Connected. From here on, a disconnect means the session dropped.
        await loop.InvokeAsync(() =>
        {
            StartSnapshotTimer(created);
            RaiseClientReady(created);
        }).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Notifies the audio layer on the scheduler thread. A throwing handler must not fail the
    /// connect, so failures are logged and swallowed.
    /// </summary>
    private void RaiseClientReady(TsFullClient created)
    {
        try
        {
            ClientReady?.Invoke(created);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "会话就绪通知的处理程序抛出异常，已忽略。");
        }
    }

    /// <summary>Counterpart of <see cref="RaiseClientReady"/>, called before teardown.</summary>
    private void RaiseClientClosing(TsFullClient owner)
    {
        try
        {
            ClientClosing?.Invoke(owner);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "会话关闭通知的处理程序抛出异常，已忽略。");
        }
    }

    /// <summary>
    /// Subscribes to the notifications the UI needs.
    /// </summary>
    /// <remarks>
    /// Only the <c>OnEach*</c> events are used. TSLib raises the batch <c>On*</c> event
    /// <i>before</i> applying anything to the book, so a handler on those would read stale state;
    /// the per-item events fire after the book was updated for that item.
    /// </remarks>
    private void Attach(TsFullClient c, TaskCompletionSource<string?> outcome)
    {
        c.OnDisconnected += (_, e) =>
        {
            // Doubles as the connect-failure signal, since ChangeState does not complete the
            // connect task on the Connecting -> Disconnected transition.
            string reason = e.Error is not null ? Describe(e.Error) : DescribeReason(e.ExitReason);
            if (!outcome.TrySetResult(reason))
                OnSessionLost(reason);
        };

        c.OnErrorEvent += (_, error) =>
        {
            if (error.Id == TSLib.TsErrorCode.ok)
                return;

            string message = Describe(error);
            log.LogWarning("服务器返回错误：{Error}", message);
            ui.Post(() => ServerError?.Invoke(this, message));
        };

        // Channel tree.
        c.OnEachChannelList += (_, _) => MarkDirty();
        c.OnEachChannelListFinished += (_, _) => MarkDirty();
        c.OnEachChannelCreated += (_, _) => MarkDirty();
        c.OnEachChannelDeleted += (_, _) => MarkDirty();
        c.OnEachChannelEdited += (_, _) => MarkDirty();
        c.OnEachChannelChanged += (_, _) => MarkDirty();
        c.OnEachChannelMoved += (_, _) => MarkDirty();
        c.OnEachChannelPasswordChanged += (_, _) => MarkDirty();
        c.OnEachChannelSubscribed += (_, _) => MarkDirty();
        c.OnEachChannelUnsubscribed += (_, _) => MarkDirty();
        c.OnEachChannelDescriptionChanged += (_, _) => MarkDirty();

        // Clients.
        c.OnEachClientEnterView += (_, _) => MarkDirty();
        c.OnEachClientLeftView += (_, _) => MarkDirty();
        c.OnEachClientMoved += (_, _) => MarkDirty();
        c.OnEachClientUpdated += (_, _) => MarkDirty();
        c.OnEachClientChannelGroupChanged += (_, _) => MarkDirty();
        c.OnEachClientServerGroupAdded += (_, _) => MarkDirty();
        c.OnEachClientServerGroupRemoved += (_, _) => MarkDirty();

        // Server and groups.
        c.OnEachServerUpdated += (_, _) => MarkDirty();
        c.OnEachServerGroupList += (_, _) => MarkDirty();
        c.OnEachInitServer += (_, _) => MarkDirty();

        c.OnEachTextMessage += (_, msg) =>
        {
            var chat = ChatMessage.FromNotification(msg);
            ui.Post(() => MessageReceived?.Invoke(this, chat));
        };

        c.OnEachClientPoke += (_, poke) =>
        {
            var chat = ChatMessage.FromPoke(poke);
            ui.Post(() => Poked?.Invoke(this, chat));
        };
    }

    /// <summary>Flags the book as changed. Runs on the scheduler thread.</summary>
    private void MarkDirty() => snapshotDirty = true;

    private void StartSnapshotTimer(TsFullClient owner)
    {
        snapshotTimer?.Disable();
        snapshotTimer = loop.Scheduler.CreateTimer(
            () =>
            {
                if (!snapshotDirty || !ReferenceEquals(client, owner))
                    return;

                snapshotDirty = false;
                var snapshot = BuildSnapshot(owner);
                ui.Post(() => Publish(snapshot));
            },
            SnapshotCoalesceInterval,
            active: true);
    }

    /// <summary>Builds a snapshot. Must run on the scheduler thread.</summary>
    private ServerSnapshot BuildSnapshot(TsFullClient owner) =>
        ServerSnapshotBuilder.Build(owner.Book, request?.Address ?? string.Empty);

    private void Publish(ServerSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    /// <summary>Rebuilds and publishes immediately, bypassing the coalescing timer.</summary>
    private async Task PublishSnapshotAsync()
    {
        var owner = client;
        if (owner is null)
            return;

        var snapshot = await loop.InvokeAsync(() => BuildSnapshot(owner)).ConfigureAwait(false);
        snapshotDirty = false;
        await ui.InvokeAsync(() => Publish(snapshot)).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an established session dropping. Runs on the scheduler thread.
    /// </summary>
    private void OnSessionLost(string reason)
    {
        log.LogInformation("连接已断开：{Reason}", reason);

        var token = sessionCts?.Token ?? CancellationToken.None;
        var req = request;

        ui.Post(() =>
        {
            // A user-initiated disconnect already moved us out of Connected; nothing to recover.
            if (state is ConnectionState.Disconnecting or ConnectionState.Disconnected)
                return;

            if (req is null || !req.AutoReconnect || token.IsCancellationRequested)
            {
                SetState(ConnectionState.Failed, reason);
                return;
            }

            SetState(ConnectionState.Reconnecting, reason);
            _ = RetryLoopAsync(req, token);
        });
    }

    /// <summary>
    /// Reconnects with exponential backoff. Runs on the UI thread.
    /// </summary>
    private async Task RetryLoopAsync(ConnectionRequest req, CancellationToken ct)
    {
        var delay = FirstRetryDelay;

        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || state != ConnectionState.Reconnecting)
                return;

            log.LogInformation("正在尝试第 {Attempt} 次重连…", attempt);
            await TeardownAsync().ConfigureAwait(true);

            string? error = await AttemptAsync(req, ct).ConfigureAwait(true);
            if (error is null)
            {
                SetState(ConnectionState.Connected);
                await PublishSnapshotAsync().ConfigureAwait(true);
                return;
            }

            log.LogWarning("第 {Attempt} 次重连失败：{Error}", attempt, error);
            SetState(ConnectionState.Reconnecting, $"第 {attempt} 次重连失败：{error}");

            // Doubling caps out at MaxRetryDelay so a long outage does not stretch to minutes.
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }

        await TeardownAsync().ConfigureAwait(true);
        SetState(ConnectionState.Failed, $"已重试 {MaxRetryAttempts} 次仍无法连接。");
    }

    /// <summary>Disconnects gracefully. Safe to call when already disconnected.</summary>
    public async Task DisconnectAsync()
    {
        var cts = sessionCts;
        sessionCts = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (client is null)
        {
            if (state != ConnectionState.Disconnected)
                SetState(ConnectionState.Disconnected);
            return;
        }

        if (state == ConnectionState.Connected)
            SetState(ConnectionState.Disconnecting);

        await TeardownAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected);
    }

    /// <summary>
    /// Tears the TSLib client down. Never throws.
    /// </summary>
    private async Task TeardownAsync()
    {
        var owner = client;
        client = null;
        snapshotDirty = false;

        if (owner is null)
            return;

        try
        {
            await loop.InvokeAsync(async () =>
            {
                snapshotTimer?.Disable();
                snapshotTimer = null;

                RaiseClientClosing(owner);

                try
                {
                    // Sends clientdisconnect when connected, and completes even when it is not.
                    await owner.Disconnect().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "断开连接时出错，忽略。");
                }

                owner.Dispose();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Scheduler already gone (shutdown). Dispose only stops the packet handler, so it is
            // safe to call from here.
            log.LogDebug(ex, "无法在调度器线程上清理连接，改为直接释放。");
            try
            {
                owner.Dispose();
            }
            catch (Exception disposeEx)
            {
                log.LogDebug(disposeEx, "释放 TSLib 客户端失败。");
            }
        }
    }

    /// <summary>
    /// Runs a command on the scheduler thread.
    /// </summary>
    /// <remarks>
    /// The entry point for every feature layer that needs to talk to the server. Returns
    /// <see cref="CommandError.ConnectionClosed"/> rather than throwing when there is no session,
    /// so callers can treat "not connected" like any other command failure.
    /// </remarks>
    public Task<T> ExecuteAsync<T>(Func<TsFullClient, Task<T>> command, T whenDisconnected)
    {
        ArgumentNullException.ThrowIfNull(command);

        var owner = client;
        if (owner is null || !IsConnected)
            return Task.FromResult(whenDisconnected);

        return loop.InvokeAsync(() => command(owner));
    }

    /// <inheritdoc cref="ExecuteAsync{T}(Func{TsFullClient, Task{T}}, T)"/>
    public Task<E<CommandError>> ExecuteAsync(Func<TsFullClient, Task<E<CommandError>>> command) =>
        ExecuteAsync(command, CommandError.ConnectionClosed);

    /// <summary>Forces a snapshot rebuild, e.g. after a command whose effects are not notified.</summary>
    public Task RefreshAsync() => PublishSnapshotAsync();

    /// <summary>Turns a TSLib error into something worth showing a user.</summary>
    private static string Describe(CommandError error) => CommandErrorText.Describe(error);

    private static string DescribeReason(TSLib.Reason reason) => reason switch
    {
        TSLib.Reason.Timeout => "连接超时。",
        TSLib.Reason.KickedFromServer => "已被踢出服务器。",
        TSLib.Reason.KickedFromChannel => "已被踢出频道。",
        TSLib.Reason.Banned => "已被服务器封禁。",
        TSLib.Reason.ServerStopped => "服务器已停止。",
        TSLib.Reason.ServerShutdown => "服务器已关闭。",
        TSLib.Reason.SocketError => "网络错误。",
        TSLib.Reason.LeftServer => "已断开连接。",
        _ => $"连接结束（{reason}）。",
    };

    /// <summary>Updates <see cref="State"/> and raises <see cref="StateChanged"/> on the UI thread.</summary>
    private void SetState(ConnectionState next, string? detail = null)
    {
        if (state == next && detail is null)
            return;

        var previous = state;
        state = next;

        var args = new ConnectionStateChangedEventArgs(previous, next, detail);
        log.LogDebug("连接状态 {Args}", args);

        if (ui.IsOnUiThread)
            StateChanged?.Invoke(this, args);
        else
            ui.Post(() => StateChanged?.Invoke(this, args));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await DisconnectAsync().ConfigureAwait(false);
    }
}
