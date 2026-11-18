using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Synadia.AI.Agents.Service;

/// <summary>Configuration of an <see cref="AgentService"/> registration.</summary>
public sealed class AgentServiceOptions
{
    /// <summary>§2 <c>agent</c> token — the harness/agent type (e.g. <c>claude-code</c>). Required.</summary>
    public required string Agent { get; init; }

    /// <summary>§2 <c>owner</c> token — who runs it. Required.</summary>
    public required string Owner { get; init; }

    /// <summary>§2 <c>name</c> token — the session/instance name this registration serves. Required.</summary>
    public required string Name { get; init; }

    /// <summary>Optional session label advertised in metadata and heartbeats.</summary>
    public string? Session { get; init; }

    /// <summary><c>$SRV.INFO</c> description. Default: <c>"{agent} agent {name}"</c>.</summary>
    public string? Description { get; init; }

    /// <summary><c>$SRV.INFO</c> version. Default <c>0.0.1</c> (matches the TypeScript and Python hosts).</summary>
    public string Version { get; init; } = "0.0.1";

    /// <summary>Extra service metadata. Required protocol keys always win over these.</summary>
    public IReadOnlyDictionary<string, string>? ExtraMetadata { get; init; }

    /// <summary>§2.1 <c>attachments_ok</c>. Default <c>true</c>.</summary>
    public bool AttachmentsOk { get; init; } = true;

    /// <summary>
    /// §2.1 <c>max_payload</c> override in bytes. Clamped to the server's negotiated limit; when null the server
    /// limit is advertised (or <c>1MB</c> if the server reported none).
    /// </summary>
    public long? MaxPayload { get; init; }

    /// <summary>§8.2 heartbeat cadence. Default 30 s.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = AgentProtocol.DefaultHeartbeatInterval;

    /// <summary>Periodic keep-alive <c>ack</c> cadence while a handler runs (§6.4). <c>null</c> disables. Default 30 s.</summary>
    public TimeSpan? KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sender-identity <c>min_sender_trust</c> advertised on the prompt endpoint: <c>any</c> (default) or <c>signed</c>.</summary>
    public string MinSenderTrust { get; init; } = "any";

    /// <summary>Default timeout for <see cref="PromptStream.AskAsync(string, IEnumerable{Microsoft.Extensions.AI.DataContent}?, TimeSpan?, CancellationToken)"/>.</summary>
    public TimeSpan DefaultQueryTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Provider of extra heartbeat fields, read on every beat. Reserved §8.3 keys are refused.</summary>
    public Func<IReadOnlyDictionary<string, JsonElement>>? HeartbeatExtras { get; init; }

    /// <summary>How long <see cref="AgentService.StopAsync"/> waits for in-flight prompts after cancelling them.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Clock (tests).</summary>
    public TimeProvider? TimeProvider { get; init; }
}
