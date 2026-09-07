// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SIPSorceryMedia.Abstractions;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming;

/// <summary>
/// Bridges <see cref="VideoCodec"/> between its three representations: this enum, the TSSP wire
/// strings, and SIPSorcery's <see cref="VideoCodecsEnum"/>.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="StreamMediaProfile"/> so the domain record stays free of a
/// SIPSorcery dependency, and so the mapping can be unit tested without any media stack.
/// </remarks>
public static class StreamCodecs
{
    /// <summary>Payload type for H.264, matching the constrained baseline entry on the SFU.</summary>
    /// <remarks>
    /// The numbers here mirror <c>registerCodecs</c> in the stream service's
    /// <c>internal/sfu/engine.go</c>, which is the single source of truth: H.264 <c>42e01f</c> is
    /// 102, H.264 <c>640c1f</c> is 108, VP8 is 96 and Opus is 111. The SFU rewrites payload types
    /// when it re-offers a forwarded track, so a mismatch would only surface in P2P mode — where
    /// the two clients exchange SDP directly and nothing remaps for them.
    /// </remarks>
    public const int H264PayloadType = 102;

    /// <summary>Payload type for the VP8 fallback track, matching the SFU's VP8 entry.</summary>
    public const int Vp8PayloadType = 96;

    /// <summary>
    /// SDP <c>fmtp</c> parameters for H.264: constrained baseline level 3.1, non-interleaved.
    /// </summary>
    /// <remarks>
    /// <c>42e01f</c> is constrained baseline 3.1, which every Android hardware decoder supports and
    /// which caps out at 1080p30 — exactly the top profile this client offers.
    /// <c>packetization-mode=1</c> enables the fragmentation units the RTP packetiser emits for
    /// keyframes larger than the MTU. <c>level-asymmetry-allowed=1</c> is required for a byte-wise
    /// match with the SFU's <c>SDPFmtpLine</c>, and lets a peer decode at a level above the one it
    /// encodes at.
    /// </remarks>
    public const string H264FormatParameters =
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";

    /// <summary>RTP clock rate for video, fixed at 90 kHz by RFC 3551.</summary>
    public const int VideoClockRate = 90_000;

    /// <summary>Codec preference order: H.264 first, VP8 as the fallback.</summary>
    public static IReadOnlyList<VideoCodec> PreferenceOrder { get; } = [VideoCodec.H264, VideoCodec.Vp8];

    /// <summary>Maps to the value used in <c>setup.properties.codec</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codec"/> is not a known value.</exception>
    public static string ToWire(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H264",
        VideoCodec.Vp8 => "VP8",
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的视频编码。"),
    };

    /// <summary>
    /// Parses a wire codec name. Case-insensitive, and tolerates the <c>H.264</c> spelling.
    /// </summary>
    /// <remarks>
    /// Lenient on input because the value can come from another implementation's
    /// <c>hello.server.video_codecs</c>; strict on output because we control that.
    /// </remarks>
    public static bool TryParseWire(string? value, [NotNullWhen(true)] out VideoCodec? codec)
    {
        codec = value?.Trim().ToUpperInvariant() switch
        {
            "H264" or "H.264" or "AVC" => VideoCodec.H264,
            "VP8" => VideoCodec.Vp8,
            _ => null,
        };

        return codec is not null;
    }

    /// <summary>Maps to SIPSorcery's codec enum.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codec"/> is not a known value.</exception>
    public static VideoCodecsEnum ToSipSorcery(VideoCodec codec) => codec switch
    {
        // Note the underlying values are not contiguous: VP8 = 8, H264 = 11.
        VideoCodec.H264 => VideoCodecsEnum.H264,
        VideoCodec.Vp8 => VideoCodecsEnum.VP8,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的视频编码。"),
    };

    /// <summary>Maps back from SIPSorcery's codec enum; null for codecs this client does not handle.</summary>
    public static VideoCodec? FromSipSorcery(VideoCodecsEnum codec) => codec switch
    {
        VideoCodecsEnum.H264 => VideoCodec.H264,
        VideoCodecsEnum.VP8 => VideoCodec.Vp8,
        _ => null,
    };

    /// <summary>Payload type this client assigns to <paramref name="codec"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codec"/> is not a known value.</exception>
    public static int PayloadTypeFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => H264PayloadType,
        VideoCodec.Vp8 => Vp8PayloadType,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的视频编码。"),
    };

    /// <summary>Builds the SDP media format offered for <paramref name="codec"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codec"/> is not a known value.</exception>
    public static VideoFormat ToVideoFormat(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new VideoFormat(
            VideoCodecsEnum.H264,
            H264PayloadType,
            VideoClockRate,
            H264FormatParameters),
        VideoCodec.Vp8 => new VideoFormat(VideoCodecsEnum.VP8, Vp8PayloadType, VideoClockRate),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "未知的视频编码。"),
    };

    /// <summary>
    /// Intersects our preference order with what the peer advertised, keeping our order.
    /// </summary>
    /// <param name="advertised">
    /// Wire codec names from <c>hello.server.video_codecs</c> or <c>setup.publish.video_codecs</c>.
    /// Null or empty means "no constraint", which yields the full preference order.
    /// </param>
    /// <param name="available">
    /// Codecs this machine can actually encode. Null means "no constraint"; in practice
    /// <see cref="Encoding.ScreenVideoEncoder"/> passes what the loaded backends support.
    /// </param>
    /// <remarks>
    /// Unknown names in <paramref name="advertised"/> are ignored rather than rejected, so a peer
    /// offering <c>["H264", "AV1"]</c> still negotiates H.264.
    /// </remarks>
    public static IReadOnlyList<VideoCodec> Negotiate(
        IEnumerable<string>? advertised,
        IEnumerable<VideoCodec>? available = null)
    {
        var allowed = new HashSet<VideoCodec>(PreferenceOrder);

        if (advertised is not null)
        {
            var peer = new HashSet<VideoCodec>();
            foreach (string name in advertised)
            {
                if (TryParseWire(name, out var parsed))
                {
                    peer.Add(parsed.Value);
                }
            }

            // An advertisement listing only codecs we do not know is treated as no constraint
            // rather than as "nothing works": the peer may simply be newer than we are.
            if (peer.Count > 0)
            {
                allowed.IntersectWith(peer);
            }
        }

        if (available is not null)
        {
            allowed.IntersectWith(available);
        }

        return [.. PreferenceOrder.Where(allowed.Contains)];
    }

    /// <summary>
    /// Projects a profile onto the <c>setup.properties</c> / <c>update.properties</c> dictionary.
    /// </summary>
    /// <param name="profile">Media parameters to describe.</param>
    /// <param name="sourceId">
    /// Value for <c>source</c>, from <see cref="Core.Streaming.ScreenCaptureTarget.ToSourceId"/>.
    /// Omitted when null or empty.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, string> ToProperties(
        StreamMediaProfile profile,
        string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TsspStreamProperties.Width] = profile.Width.ToString(CultureInfo.InvariantCulture),
            [TsspStreamProperties.Height] = profile.Height.ToString(CultureInfo.InvariantCulture),
            [TsspStreamProperties.FrameRate] = profile.FrameRate.ToString(CultureInfo.InvariantCulture),
            [TsspStreamProperties.Codec] = ToWire(profile.Codec),
            [TsspStreamProperties.BitrateKbps] = profile.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            // Lower case because the spec types the value as the JSON-ish "true" / "false".
            [TsspStreamProperties.Audio] = profile.HasAudio ? "true" : "false",
        };

        if (!string.IsNullOrEmpty(sourceId))
        {
            properties[TsspStreamProperties.Source] = sourceId;
        }

        return properties;
    }

    /// <summary>
    /// Reads a profile back out of a <c>properties</c> dictionary, using
    /// <paramref name="fallback"/> for anything missing or malformed.
    /// </summary>
    /// <remarks>
    /// Used on the viewing side, where the dictionary comes from a <c>stream_added</c> event and is
    /// only a hint: the real parameters come from the negotiated SDP. Malformed values therefore
    /// degrade to the fallback instead of throwing.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is <see langword="null"/>.</exception>
    public static StreamMediaProfile FromProperties(
        IReadOnlyDictionary<string, string>? properties,
        StreamMediaProfile fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        if (properties is null || properties.Count == 0)
        {
            return fallback;
        }

        return fallback with
        {
            Width = ReadInt(properties, TsspStreamProperties.Width, fallback.Width),
            Height = ReadInt(properties, TsspStreamProperties.Height, fallback.Height),
            FrameRate = ReadInt(properties, TsspStreamProperties.FrameRate, fallback.FrameRate),
            BitrateKbps = ReadInt(properties, TsspStreamProperties.BitrateKbps, fallback.BitrateKbps),
            Codec = properties.TryGetValue(TsspStreamProperties.Codec, out string? codecName)
                && TryParseWire(codecName, out var codec)
                    ? codec.Value
                    : fallback.Codec,
            HasAudio = properties.TryGetValue(TsspStreamProperties.Audio, out string? audio)
                ? string.Equals(audio.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                : fallback.HasAudio,
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> properties, string key, int fallback) =>
        properties.TryGetValue(key, out string? raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value > 0
            ? value
            : fallback;
}
