using System;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Settings;
using TSLib;
using TSLib.Audio;
using TSLib.Full;
using TSLib.Helper;

namespace TeamSpeak9.Core.Audio;

/// <summary>
/// Builds the send and receive audio chains for a voice session and keeps them in sync with
/// <see cref="AudioSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The send chain is clocked by a <see cref="PreciseTimedPipe"/> that pulls from the capture
/// device: capture → timer → <see cref="VolumePipe"/> → <see cref="GatePipe"/> →
/// <see cref="EncoderPipe"/> → <see cref="StaticMetaPipe"/> → client.
/// </para>
/// <para>
/// The receive chain has no timer of its own because <see cref="IAudioPlaybackSink"/> is an
/// active consumer driven by the playback device clock: client → <see cref="AudioPacketReader"/>
/// → <see cref="DecoderPipe"/> → <see cref="ClientMixdown"/>, with the sink pulling from the
/// mixdown.
/// </para>
/// <para>
/// <see cref="Attach"/> and <see cref="Detach"/> are idempotent. The connection layer skips its
/// closing notification when the scheduler thread is already gone, so teardown may also arrive
/// through <see cref="Dispose"/> only.
/// </para>
/// </remarks>
public sealed class AudioPipeline : IDisposable
{
	/// <summary>Sample rate used by both chains; the only rate TSLib's Opus pipes emit.</summary>
	public const int SampleRate = 48000;

	/// <summary>Channel count of the capture chain. Opus voice frames are mono.</summary>
	public const int CaptureChannels = 1;

	/// <summary>Channel count of the playback chain. <see cref="DecoderPipe"/> always emits stereo.</summary>
	public const int PlaybackChannels = 2;

	/// <summary>Bit depth of the PCM buffers exchanged with the devices.</summary>
	public const int BitsPerSample = 16;

	private readonly IAudioDeviceFactory factory;
	private readonly AppSettings settings;
	private readonly ILogger<AudioPipeline> log;
	private readonly object sync = new();

	private Session? session;
	private bool hotkeyHeld;
	private volatile bool transmitting;
	private bool disposed;

	/// <summary>Creates a pipeline that will build its devices from <paramref name="factory"/>.</summary>
	public AudioPipeline(IAudioDeviceFactory factory, AppSettings settings, ILogger<AudioPipeline> log)
	{
		ArgumentNullException.ThrowIfNull(factory);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(log);

		this.factory = factory;
		this.settings = settings;
		this.log = log;
	}

	/// <summary>
	/// Raised when the send gate opens or closes. Invoked on the audio tick thread, so handlers
	/// that touch bound collections must marshal to the UI thread themselves.
	/// </summary>
	public event Action<bool>? TransmittingChanged;

	/// <summary>Whether a session is currently wired up.</summary>
	public bool IsAttached
	{
		get
		{
			lock (sync)
				return session is not null;
		}
	}

	/// <summary>Whether the send gate is currently passing audio.</summary>
	public bool IsTransmitting => transmitting;

	/// <summary>
	/// Current input level in dBFS, or <see cref="VoiceActivityDetector.SilenceDb"/> while detached.
	/// </summary>
	public double LevelDb
	{
		get
		{
			lock (sync)
				return session?.Detector.LevelDb ?? VoiceActivityDetector.SilenceDb;
		}
	}

	/// <summary>
	/// Whether the push-to-talk key is held. Ignored unless
	/// <see cref="AudioSettings.TransmitMode"/> is <see cref="PushToTalkMode.PushToTalk"/>.
	/// </summary>
	public bool HotkeyHeld
	{
		get => hotkeyHeld;
		set
		{
			hotkeyHeld = value;
			lock (sync)
			{
				if (session is not null)
					session.Gate.HotkeyHeld = value;
			}
		}
	}

	/// <summary>
	/// Wires both chains to <paramref name="client"/>. A second call without an intervening
	/// <see cref="Detach"/> is ignored. Device or codec failures are logged and leave the
	/// pipeline detached rather than propagating to the caller's scheduler thread.
	/// </summary>
	public void Attach(TsFullClient client)
	{
		ArgumentNullException.ThrowIfNull(client);

		lock (sync)
		{
			if (disposed || session is not null)
				return;

			AttachCore(client);
		}
	}

	/// <summary>Tears both chains down and releases the devices. Safe to call when already detached.</summary>
	public void Detach()
	{
		lock (sync)
			DetachCore();
	}

	/// <summary>
	/// Pushes the current <see cref="AudioSettings"/> into the live chains. A changed input or
	/// output device id rebuilds the affected session from scratch.
	/// </summary>
	public void ApplySettings()
	{
		lock (sync)
		{
			var current = session;
			if (current is null)
				return;

			var audio = settings.Audio;
			if (!string.Equals(current.InputDeviceId, audio.InputDeviceId, StringComparison.Ordinal)
				|| !string.Equals(current.OutputDeviceId, audio.OutputDeviceId, StringComparison.Ordinal))
			{
				var client = current.Client;
				DetachCore();
				AttachCore(client);
				return;
			}

			ApplyTo(current);
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (sync)
		{
			if (disposed)
				return;

			disposed = true;
			DetachCore();
		}
	}

	private void AttachCore(TsFullClient client)
	{
		var audio = settings.Audio;

		IAudioCaptureSource? capture = null;
		IAudioPlaybackSink? sink = null;
		PreciseTimedPipe? sendTimer = null;
		EncoderPipe? encoder = null;
		DecoderPipe? decoder = null;
		ClientMixdown? mixdown = null;

		try
		{
			capture = factory.CreateCapture(audio.InputDeviceId);
			sink = factory.CreatePlayback(audio.OutputDeviceId);

			encoder = new EncoderPipe(Codec.OpusVoice);
			decoder = new DecoderPipe();
			mixdown = new ClientMixdown();

			var detector = new VoiceActivityDetector(
				SampleRate,
				audio.VoiceActivationThresholdDb,
				audio.VoiceActivationHangoverMs);
			var gate = new GatePipe(detector);
			var inputVolume = new VolumePipe();
			var sendMeta = new StaticMetaPipe();
			var packetReader = new AudioPacketReader();

			sendTimer = new PreciseTimedPipe(
				new SampleInfo(SampleRate, CaptureChannels, BitsPerSample),
				new Id(0));

			// Wire back to front: Active propagates upstream from the terminal consumer, so a
			// still-unset OutStream anywhere would silently stall the whole chain.
			sendMeta.OutStream = client;
			encoder.OutStream = sendMeta;
			gate.OutStream = encoder;
			inputVolume.OutStream = gate;
			sendTimer.OutStream = inputVolume;
			sendTimer.InStream = capture;

			packetReader.OutStream = decoder;
			decoder.OutStream = mixdown;
			sink.InStream = mixdown;

			var created = new Session(
				client,
				audio.InputDeviceId,
				audio.OutputDeviceId,
				capture,
				sendTimer,
				inputVolume,
				detector,
				gate,
				encoder,
				sendMeta,
				packetReader,
				decoder,
				mixdown,
				sink);

			capture.Start();
			sink.Start();

			// StaticMetaPipe drops every packet while its send mode is None.
			sendMeta.SetVoice();
			ApplyTo(created);
			gate.HotkeyHeld = hotkeyHeld;
			gate.TransmittingChanged += OnGateTransmittingChanged;

			// PreciseTimedPipe starts paused; without this nothing is ever pulled from capture.
			sendTimer.Paused = false;
			client.OutStream = packetReader;

			session = created;
			log.LogInformation(
				"音频管道已启动：输入 {Input}，输出 {Output}。",
				capture.Device.Name,
				sink.Device.Name);
		}
		catch (Exception ex)
		{
			log.LogError(ex, "音频管道启动失败，本次会话没有语音。");

			client.OutStream = null;
			Quiet(() => sendTimer?.Dispose());
			Quiet(() => encoder?.Dispose());
			Quiet(() => decoder?.Dispose());
			Quiet(() => mixdown?.Dispose());
			Quiet(() => capture?.Dispose());
			Quiet(() => sink?.Dispose());
		}
	}

	private void DetachCore()
	{
		var current = session;
		if (current is null)
			return;

		session = null;
		current.Client.OutStream = null;
		current.Gate.TransmittingChanged -= OnGateTransmittingChanged;

		Quiet(current.Capture.Stop);
		Quiet(current.Sink.Stop);

		// Dispose the timer first: it joins its tick thread, which guarantees no further writes
		// reach the encoder afterwards.
		Quiet(current.SendTimer.Dispose);
		Quiet(current.Encoder.Dispose);
		Quiet(current.Decoder.Dispose);
		Quiet(current.Mixdown.Dispose);
		Quiet(current.Capture.Dispose);
		Quiet(current.Sink.Dispose);
		Quiet(current.SendMeta.SetNone);

		if (transmitting)
		{
			transmitting = false;
			TransmittingChanged?.Invoke(false);
		}
	}

	private void ApplyTo(Session current)
	{
		var audio = settings.Audio;

		current.InputVolume.Volume = (float)audio.InputVolume;
		current.Sink.Volume = audio.OutputMuted ? 0f : (float)audio.OutputVolume;
		current.Detector.ThresholdDb = audio.VoiceActivationThresholdDb;
		current.Detector.HangoverMilliseconds = audio.VoiceActivationHangoverMs;
		current.Gate.Mode = audio.TransmitMode;
		current.Gate.Muted = audio.InputMuted;
	}

	private void OnGateTransmittingChanged(bool value)
	{
		transmitting = value;
		TransmittingChanged?.Invoke(value);
	}

	private void Quiet(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			log.LogDebug(ex, "音频管道拆除时忽略了一个异常。");
		}
	}

	private sealed class Session(
		TsFullClient client,
		string inputDeviceId,
		string outputDeviceId,
		IAudioCaptureSource capture,
		PreciseTimedPipe sendTimer,
		VolumePipe inputVolume,
		VoiceActivityDetector detector,
		GatePipe gate,
		EncoderPipe encoder,
		StaticMetaPipe sendMeta,
		AudioPacketReader packetReader,
		DecoderPipe decoder,
		ClientMixdown mixdown,
		IAudioPlaybackSink sink)
	{
		public TsFullClient Client { get; } = client;

		public string InputDeviceId { get; } = inputDeviceId;

		public string OutputDeviceId { get; } = outputDeviceId;

		public IAudioCaptureSource Capture { get; } = capture;

		public PreciseTimedPipe SendTimer { get; } = sendTimer;

		public VolumePipe InputVolume { get; } = inputVolume;

		public VoiceActivityDetector Detector { get; } = detector;

		public GatePipe Gate { get; } = gate;

		public EncoderPipe Encoder { get; } = encoder;

		public StaticMetaPipe SendMeta { get; } = sendMeta;

		public AudioPacketReader PacketReader { get; } = packetReader;

		public DecoderPipe Decoder { get; } = decoder;

		public ClientMixdown Mixdown { get; } = mixdown;

		public IAudioPlaybackSink Sink { get; } = sink;
	}
}
