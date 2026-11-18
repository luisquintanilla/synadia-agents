namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// NATS <c>max_payload</c> arithmetic for the prompt envelope
/// <c>{"prompt": "...", "attachments": [{"filename": "...", "content": "&lt;base64&gt;"}]}</c>.
/// </summary>
/// <remarks>
/// The broker rejects any message larger than <c>max_payload</c> (1 MiB by default), and
/// base64 inflates binary by 4/3, so the largest raw file that fits in a default
/// request is roughly 760 KiB once the JSON envelope is accounted for. A caller SDK
/// rejects oversize envelopes before publishing (spec §5.4), so an agent normally only
/// sees oversize attachments when the broker allows a bigger payload than the agent is
/// willing to process. Either way the agent answers with a readable message instead of
/// failing the whole request.
/// </remarks>
public static class PayloadLimits
{
    /// <summary>NATS server default <c>max_payload</c>: 1 MiB.</summary>
    public const int DefaultMaxPayloadBytes = 1024 * 1024;

    /// <summary>Bytes reserved for the JSON envelope, prompt text and headers.</summary>
    public const int DefaultEnvelopeReserveBytes = 8 * 1024;

    /// <summary>Base64 encoded length of <paramref name="rawBytes"/>.</summary>
    public static long Base64Length(long rawBytes) => (rawBytes + 2) / 3 * 4;

    /// <summary>The largest raw attachment (all attachments combined) that fits in one request.</summary>
    public static long MaxRawAttachmentBytes(long maxPayloadBytes = DefaultMaxPayloadBytes, long reserveBytes = DefaultEnvelopeReserveBytes) =>
        Math.Max(0, (maxPayloadBytes - reserveBytes) / 4 * 3);

    /// <summary>Human-readable byte count (B/KB/MB, base 1024).</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
