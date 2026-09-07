// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using TeamSpeak9.Core.Audio;
using TeamSpeak9.Core.Connection;
using TeamSpeak9.Core.Identity;
using TeamSpeak9.Core.Management;
using TeamSpeak9.Core.Security;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.Core.Threading;
using TeamSpeak9.App.Streaming;
using TeamSpeak9.Streaming;
using TeamSpeak9.Streaming.Capture;
using TeamSpeak9.Streaming.Encoding;
using TeamSpeak9.Streaming.Publishing;
using TeamSpeak9.Streaming.Subscribing;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.App.Infrastructure;

/// <summary>
/// Composition root: everything the app resolves is registered here.
/// </summary>
internal static class ServiceRegistration
{
    /// <param name="schedulerLoop">
    /// Already started, because <see cref="TsSchedulerLoop.StartAsync"/> is asynchronous and
    /// nothing may resolve <see cref="TsConnection"/> before the scheduler accepts work.
    /// </param>
    public static IServiceCollection AddTeamSpeak9(
        this IServiceCollection services,
        AppPaths paths,
        AppSettings settings,
        TsSchedulerLoop schedulerLoop)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(schedulerLoop);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            // Routes ILogger<T> into the same NLog targets TSLib already writes to.
            builder.AddNLog();
        });

        services.AddSingleton(paths);
        services.AddSingleton(settings);
        services.AddSingleton(schedulerLoop);

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IdentityStore>();

        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(System.Windows.Application.Current.Dispatcher));

        // One connection per app, matching the single-server UI.
        services.AddSingleton<TsConnection>();

        // Management layer. Stateless wrappers over the connection, so singletons are fine.
        services.AddSingleton<ChannelService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<ServerService>();

        // Audio. The WASAPI implementations live in the app layer so Core stays platform neutral
        // and free of CA1416 Windows-only annotations.
        services.AddSingleton<IAudioDeviceEnumerator, Audio.WasapiDeviceEnumerator>();
        services.AddSingleton<IAudioDeviceFactory, Audio.WasapiDeviceFactory>();
        services.AddSingleton<AudioPipeline>();
        services.AddSingleton<Audio.AudioService>();

                // Streaming (screen share).
                services.AddSingleton<IScreenTargetEnumerator, WindowsScreenTargetEnumerator>();
                services.AddSingleton<IScreenCaptureFactory, WindowsScreenCaptureFactory>();
                services.AddSingleton<ScreenVideoEncoder>();
                services.AddSingleton<TsspClient>();
                                services.AddSingleton<ScreenShareService>(sp => new ScreenShareService(
                                    sp.GetRequiredService<TsConnection>(),
                                    sp.GetRequiredService<IScreenCaptureFactory>(),
                                    sp.GetRequiredService<ScreenVideoEncoder>(),
                                    sp.GetRequiredService<TsspClient>(),
                                    sp.GetRequiredService<StreamSettings>(),
                                    sp.GetRequiredService<IUiDispatcher>(),
                                    sp.GetRequiredService<ILogger<ScreenShareService>>(),
                                    sp.GetRequiredService<Func<ViewModels.SharePickerViewModel, Views.SharePickerWindow>>()));
                                                services.AddSingleton<ViewModels.StreamViewerViewModel>();

                                // One shell for the one window.
                                services.AddSingleton<ViewModels.ShellViewModel>();

        // Dialog view models are transient: each dialog gets a fresh one, and closing the dialog
        // has to drop the state it accumulated.
        services.AddTransient<ViewModels.ChannelEditorViewModel>();
        services.AddTransient<ViewModels.IconBrowserViewModel>();
        services.AddTransient<ViewModels.ServerEditorViewModel>();
                services.AddTransient<ViewModels.SharePickerViewModel>();

                // Explicit factories because these windows' real constructors are internal, and the
                // container's automatic constructor selection only considers public ones.
                services.AddSingleton(sp => new MainWindow(sp.GetRequiredService<ViewModels.ShellViewModel>()));
                services.AddTransient(sp => new Views.ChannelEditorWindow(sp.GetRequiredService<ViewModels.ChannelEditorViewModel>()));
                services.AddTransient(sp => new Views.IconBrowserWindow(sp.GetRequiredService<ViewModels.IconBrowserViewModel>()));
                services.AddTransient(sp => new Views.ServerEditorWindow(sp.GetRequiredService<ViewModels.ServerEditorViewModel>()));
                                services.AddTransient<Func<ViewModels.SharePickerViewModel, Views.SharePickerWindow>>(sp => vm => new Views.SharePickerWindow(vm));

        return services;
    }
}
