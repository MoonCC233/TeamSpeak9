// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Windows.Threading;
using TeamSpeak9.Core.Threading;

namespace TeamSpeak9.App.Infrastructure;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by the WPF dispatcher.
/// </summary>
/// <remarks>
/// Core raises events on the TSLib scheduler thread. Anything that touches a view model has to
/// come back through here, otherwise WPF throws on cross-thread collection changes.
/// </remarks>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsOnUiThread => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(func);

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(func());
            }

            return dispatcher.InvokeAsync(func).Task;
        }
    }
