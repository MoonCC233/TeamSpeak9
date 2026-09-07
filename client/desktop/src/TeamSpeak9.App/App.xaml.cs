// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamSpeak9.App.Infrastructure;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Threading;

namespace TeamSpeak9.App;

public partial class App : Application
{
    private ServiceProvider? services;
    private TsSchedulerLoop? schedulerLoop;
    private ILogger<App>? log;

    /// <summary>Resolved services, available once startup has completed.</summary>
    internal IServiceProvider Services =>
        services ?? throw new InvalidOperationException("服务容器尚未初始化。");

    /// <summary>The thread every TSLib call is marshalled onto.</summary>
    internal TsSchedulerLoop SchedulerLoop =>
        schedulerLoop ?? throw new InvalidOperationException("TSLib 调度器尚未启动。");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();

        AppSettings settings;
        try
        {
            paths.EnsureCreated();
            // Logging needs the level from settings, and the settings store wants a logger, so
            // this first load runs without one and logging is configured immediately after.
            settings = await new SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance)
                .LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法读取配置目录 {paths.Root}：\n{ex.Message}",
                "TeamSpeak9",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        LoggingSetup.Configure(paths, settings.LogLevel);

        // The icon converter resolves ids against the on-disk cache, which is only known now.
        Converters.IconIdToImageConverter.CacheDirectory = paths.IconCacheDirectory;

        // Started before the container so TsConnection can take it as a dependency; the loop is
        // only usable once StartAsync has returned.
        schedulerLoop = await TsSchedulerLoop.StartAsync();

        services = new ServiceCollection()
            .AddTeamSpeak9(paths, settings, schedulerLoop)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        log = services.GetRequiredService<ILogger<App>>();
        log.LogInformation("TeamSpeak9 启动，配置目录 {Root}", paths.Root);

        // Resolved eagerly: its constructor is what subscribes to the connection's session
        // notifications, and nothing else resolves it, so a lazy resolve would never happen.
        services.GetRequiredService<Audio.AudioService>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Blocking rather than async void: the process can terminate before an awaited
        // continuation resumes, which would skip scheduler shutdown and log flushing.
        var connection = services?.GetService<Core.Connection.TsConnection>();
        if (connection is not null)
        {
            // Before the scheduler, since the graceful clientdisconnect runs on it.
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (schedulerLoop is not null)
        {
            schedulerLoop.DisposeAsync().AsTask().GetAwaiter().GetResult();
            schedulerLoop = null;
        }

        // DisposeAsync, not Dispose: the synchronous path throws on any registration that only
        // implements IAsyncDisposable, which TsConnection does.
        if (services is not null)
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            services = null;
        }

        LoggingSetup.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        log?.LogError(e.Exception, "UI 线程未处理异常");

        MessageBox.Show(
            $"发生未处理的错误：\n{e.Exception.Message}",
            "TeamSpeak9",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: a failed command should not take the whole client down.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => log?.LogCritical(e.ExceptionObject as Exception, "后台线程未处理异常，进程即将终止");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        log?.LogError(e.Exception, "未观察的任务异常");
        e.SetObserved();
    }
}
