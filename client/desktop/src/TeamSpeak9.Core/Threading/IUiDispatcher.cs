// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

namespace TeamSpeak9.Core.Threading;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// Core raises events from the TSLib scheduler thread; view models need them on the WPF
/// dispatcher. This interface keeps the WPF dependency out of Core so the domain layer stays
/// testable.
/// </remarks>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    /// <summary>Queues the action and returns immediately.</summary>
    void Post(Action action);

    /// <summary>Queues the action and completes when it has run.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Queues the function and completes when it has run, returning its result.</summary>
    Task<T> InvokeAsync<T>(Func<T> func);
}

/// <summary>
/// Runs everything inline on the calling thread. For unit tests and headless use.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static readonly ImmediateUiDispatcher Instance = new();

    public bool IsOnUiThread => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            return Task.FromResult(func());
        }
    }
