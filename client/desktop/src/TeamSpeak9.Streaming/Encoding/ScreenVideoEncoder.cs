// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
using TeamSpeak9.Core.Streaming;

namespace TeamSpeak9.Streaming.Encoding;

/// <summary>
/// Encodes captured BGRA frames into H.264 or VP8 for the screen share pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Wraps (rather than implements) <see cref="IVideoEncoder"/> because the concrete backends
/// implement <c>EncodeVideo</c> with a nullable <see cref="byte"/> array while the interface
/// declares it non-nullable; implementing the interface directly would fail the build under
/// <c>TreatWarningsAsErrors</c>.
/// </para>
/// <para>
/// The encoder prefers FFmpeg's H.264 (hardware first, then <c>libx264</c>) and falls back to
/// the managed VP8 encoder when FFmpeg is unavailable. The FFmpeg probe runs once per process and
/// its result is cached forever: the underlying <c>registered</c> flag makes a retry meaningless.
/// </para>
/// <para>
/// Frames arrive on the capture thread as borrowed BGRA buffers with a possibly padded stride.
/// This class repacks each frame into a tightly packed buffer (mandatory for both backends) and
/// hands it to the encoder. It never subscribes to <see cref="FFmpegVideoEncoder.OnVideoEncoderStatistics"/>
/// because that event fires on the encoder's internal thread and would force a UI marshalling
/// hop for no benefit.
/// </para>
/// </remarks>
public sealed class ScreenVideoEncoder : IDisposable
{
    /// <summary>Hardware H.264 encoders probed in order of preference.</summary>
    private static readonly string[] HardwareH264Names = ["h264_qsv", "h264_nvenc"];

    private readonly object _sync = new();
    private readonly ILogger _log;

    // The active backend. Null until the first frame arrives, because the codec is negotiated
    // per stream and the backend is created lazily on first use.
    private IVideoEncoder? _backend;

    // FFmpeg-specific state, only touched while _backend is an FFmpegVideoEncoder.
        private FFmpegVideoEncoder? _ffmpeg;

    // VP8-specific state, only touched while _backend is a VpxVideoEncoder.
    private VpxVideoEncoder? _vpx;

    // The negotiated profile for the current stream.
    private StreamMediaProfile? _profile;

    // Reusable tightly packed BGRA buffer, grown on demand. Guarded by _sync.
    private byte[] _packed = Array.Empty<byte>();

    // The dimensions the backend was last initialised with; a change forces a recreate.
    private int _initialisedWidth;
    private int _initialisedHeight;

    private bool _disposed;

    /// <summary>
    /// Initialises a new encoder.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    public ScreenVideoEncoder(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// The codecs this machine can actually encode, in preference order. Passed to
    /// <see cref="StreamCodecs.Negotiate"/> as the <c>available</c> set.
    /// </summary>
    /// <remarks>
    /// Computed once from the cached FFmpeg probe; never changes for the lifetime of the process.
    /// </remarks>
    public static IReadOnlyList<VideoCodec> AvailableCodecs { get; } = ProbeAvailableCodecs();

    /// <summary>
    /// Encodes one captured frame into an H.264 or VP8 access unit.
    /// </summary>
    /// <param name="frame">The captured frame. Its buffer is only valid for this call.</param>
    /// <param name="profile">The negotiated media parameters for the stream.</param>
    /// <returns>
    /// The encoded access unit, or <see langword="null"/> when the encoder needs more input
    /// (an <c>EAGAIN</c> from FFmpeg). Callers must treat a null return as "no packet this frame".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The frame does not carry a usable buffer.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public byte[]? Encode(in ScreenFrame frame, StreamMediaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!frame.IsValid)
        {
            throw new InvalidOperationException("帧数据无效，无法编码。");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            EnsureBackend(profile, frame.Width, frame.Height);
            byte[] packed = Pack(frame);

            return _backend switch
            {
                FFmpegVideoEncoder ffmpeg => ffmpeg.EncodeVideo(
                    frame.Width,
                    frame.Height,
                    packed,
                    VideoPixelFormatsEnum.Bgra,
                    StreamCodecs.ToSipSorcery(profile.Codec)),
                VpxVideoEncoder vpx => vpx.EncodeVideo(
                    frame.Width,
                    frame.Height,
                    packed,
                    VideoPixelFormatsEnum.Bgra,
                    StreamCodecs.ToSipSorcery(profile.Codec)),
                _ => throw new InvalidOperationException("编码后端尚未初始化。"),
            };
        }
    }

    /// <summary>
    /// Requests a keyframe on the next encode. Used to answer a picture-loss indication (PLI).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public void ForceKeyFrame()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _backend?.ForceKeyFrame();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ffmpeg?.Dispose();
            _vpx?.Dispose();
            _ffmpeg = null;
            _vpx = null;
            _backend = null;
            _profile = null;
            _packed = Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Creates the backend for <paramref name="profile"/>, recreating it when the codec or the
    /// frame dimensions change. Must be called with <see cref="_sync"/> held.
    /// </summary>
    private void EnsureBackend(StreamMediaProfile profile, int width, int height)
    {
        bool sizeChanged = width != _initialisedWidth || height != _initialisedHeight;

        if (_backend is not null
            && _profile is not null
            && _profile.Codec == profile.Codec
            && !sizeChanged)
        {
            return;
        }

        // The codec or the dimensions changed: tear down and rebuild.
        _ffmpeg?.Dispose();
        _vpx?.Dispose();
        _ffmpeg = null;
        _vpx = null;
        _backend = null;

        _profile = profile;
        _initialisedWidth = width;
        _initialisedHeight = height;

        if (profile.Codec == VideoCodec.H264 && _ffmpeg is null && AvailableCodecs.Contains(VideoCodec.H264))
        {
            _ffmpeg = CreateFfmpegEncoder(profile, width, height);
            _backend = _ffmpeg;
            return;
        }

        if (profile.Codec == VideoCodec.Vp8)
        {
            _vpx = CreateVpxEncoder(profile);
            _backend = _vpx;
            return;
        }

        throw new InvalidOperationException($"当前环境不支持 {StreamCodecs.ToWire(profile.Codec)} 编码。");
    }

    /// <summary>
    /// Creates and initialises an FFmpeg H.264 encoder for the given profile.
    /// </summary>
    /// <remarks>
    /// <see cref="FFmpegVideoEncoder.InitialiseEncoder"/> sets its internal "initialised" flag as
    /// its very first statement, before any fallible work, so a failure leaves the instance
    /// claiming to be ready. Any exception here is therefore fatal for that instance: we dispose
    /// it and let the caller fall back, never retry.
    /// </remarks>
    private FFmpegVideoEncoder CreateFfmpegEncoder(StreamMediaProfile profile, int width, int height)
    {
        var encoder = new FFmpegVideoEncoder();

        try
        {
            // Prefer a hardware encoder, then software libx264.
            AVCodecID codecId = AVCodecID.AV_CODEC_ID_H264;
            bool selected = false;

            foreach (string name in HardwareH264Names)
            {
                if (encoder.SetCodec(codecId, name))
                {
                    _log.LogDebug("屏幕共享：选用硬件 H.264 编码器 {Name}。", name);
                    selected = true;
                    break;
                }
            }

            if (!selected)
            {
                _log.LogDebug("屏幕共享：未找到硬件 H.264 编码器，回退到 libx264。");
            }

            // Bake in the real frame rate and bitrate before the first frame. InitialiseEncoder
            // is a no-op once called, so this must happen before any EncodeVideo.
            encoder.SetThreadCount(0);
            encoder.SetBitrate(profile.BitrateKbps * 1000, null, null, null);
            encoder.InitialiseEncoder(codecId, width, height, profile.FrameRate);

                        return encoder;
        }
        catch (Exception ex) when (ex is ApplicationException or DllNotFoundException or BadImageFormatException)
        {
            _log.LogWarning(ex, "屏幕共享：FFmpeg H.264 编码器初始化失败，回退到 VP8。");
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates and initialises the managed VP8 encoder for the given profile.
    /// </summary>
    private VpxVideoEncoder CreateVpxEncoder(StreamMediaProfile profile)
    {
        var encoder = new VpxVideoEncoder
        {
                    TargetKbps = (uint)Math.Max(0, profile.BitrateKbps),
        };

        return encoder;
    }

    /// <summary>
    /// Repacks a borrowed BGRA frame into a tightly packed buffer, reusing the pooled array.
    /// Must be called with <see cref="_sync"/> held.
    /// </summary>
    private byte[] Pack(in ScreenFrame frame)
    {
        int rowBytes = frame.Width * ScreenFrame.BytesPerPixel;
        int total = rowBytes * frame.Height;

        if (_packed.Length < total)
        {
            _packed = new byte[total];
        }

        for (int y = 0; y < frame.Height; y++)
        {
            Marshal.Copy(frame.Data + (y * frame.Stride), _packed, y * rowBytes, rowBytes);
        }

        return _packed;
    }

    /// <summary>
    /// Probes once whether FFmpeg is loadable and which H.264 encoders it exposes, then caches the
    /// resulting codec set for the lifetime of the process.
    /// </summary>
    private static IReadOnlyList<VideoCodec> ProbeAvailableCodecs()
    {
        var result = new List<VideoCodec>(2);

        if (TryProbeFfmpeg())
        {
            result.Add(VideoCodec.H264);
        }

        // The managed VP8 encoder ships with the package and needs no native runtime, so it is
        // always available.
        result.Add(VideoCodec.Vp8);

        return result;
    }

    /// <summary>
    /// Attempts to load FFmpeg and confirm an H.264 encoder is present.
    /// </summary>
    /// <remarks>
    /// The probe is deliberately conservative: any failure — missing native binaries, a bad image,
    /// an absent codec — is treated as "no FFmpeg" and the caller falls back to VP8. The result is
    /// cached by <see cref="AvailableCodecs"/>, so this runs at most once.
    /// </remarks>
    private static bool TryProbeFfmpeg()
    {
        try
        {
            FFmpegInit.Initialise(logLevel: null, libPath: null, appLogger: null);

            // In SIPSorcery 10.x, Initialise registers binaries automatically
            // A throwaway encoder is enough to confirm the codec exists; SetCodec returns false
            // when the name is absent without resetting anything.
            using var probe = new FFmpegVideoEncoder();
            return probe.SetCodec(AVCodecID.AV_CODEC_ID_H264, "libx264");
        }
        catch (Exception ex) when (ex is ApplicationException or DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}