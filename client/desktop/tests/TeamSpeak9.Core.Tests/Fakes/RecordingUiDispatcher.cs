// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Threading;

namespace TeamSpeak9.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IUiDispatcher"/> that stands in for the WPF dispatcher and records how it was used.
/// </summary>
/// <remarks>
/// <see cref="ImmediateUiDispatcher"/> claims to always be on the UI thread, which makes it useless
/// for asserting that a producer marshals. This one lets the test say which thread counts as the UI
/// thread and queues everything else instead of running it.
/// </remarks>
internal sealed class RecordingUiDispatcher : IUiDispatcher
{
    private readonly List<Action> queued = [];

    /// <param name="uiThread">The thread <see cref="IsOnUiThread"/> reports as the UI thread.</param>
    public RecordingUiDispatcher(Thread? uiThread = null)
    {
        UiThread = uiThread ?? Thread.CurrentThread;
    }

    /// <summary>The thread that stands in for the WPF dispatcher thread.</summary>
    /// <remarks>
    /// Settable because an async test resumes on whatever pool thread the runner picks, so a value
    /// captured before an <c>await</c> is stale by the time the assertions run.
    /// </remarks>
    public Thread UiThread { get; set; }

    public bool IsOnUiThread => ReferenceEquals(Thread.CurrentThread, UiThread);

    /// <summary>Actions handed to <see cref="Post"/> or <see cref="InvokeAsync"/> and not yet drained.</summary>
    public IReadOnlyList<Action> Pending => queued;

    /// <summary>How many times work was marshalled rather than run inline.</summary>
    public int PostCount { get; private set; }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (queued)
        {
            PostCount++;
            queued.Add(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        Post(action);
        return Task.CompletedTask;
    }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            var tcs = new TaskCompletionSource<T>();
            Post(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        /// <summary>Runs everything queued so far, on the calling thread.</summary>
    public int Drain()
    {
        Action[] pending;
        lock (queued)
        {
            pending = [.. queued];
            queued.Clear();
        }

        foreach (var action in pending)
            action();

        return pending.Length;
    }
}
