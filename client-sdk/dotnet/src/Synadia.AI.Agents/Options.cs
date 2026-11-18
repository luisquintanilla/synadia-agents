using Microsoft.Extensions.Logging;

namespace Synadia.AI.Agents;

/// <summary>Options for <see cref="AgentsClient"/>.</summary>
public sealed class AgentsClientOptions
{
    /// <summary>Default per-prompt inactivity timeout (§6.6). Default 60 s.</summary>
    public TimeSpan InactivityTimeout { get; set; } = AgentProtocol.DefaultInactivityTimeout;

    /// <summary>Default overall prompt deadline. Default 10 min.</summary>
    public TimeSpan PromptMaxWait { get; set; } = AgentProtocol.DefaultPromptMaxWait;

    /// <summary>Default status-request timeout (§8.7). Default 2 s.</summary>
    public TimeSpan StatusTimeout { get; set; } = AgentProtocol.DefaultStatusTimeout;

    /// <summary>Discovery stall window: stop once no reply has arrived for this long. Default 750 ms.</summary>
    public TimeSpan DiscoverStall { get; set; } = AgentProtocol.DefaultDiscoverStall;

    /// <summary>Discovery overall cap. Default 2 s.</summary>
    public TimeSpan DiscoverMaxWait { get; set; } = AgentProtocol.DefaultDiscoverMaxWait;

    /// <summary>Optional logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Optional clock (tests).</summary>
    public TimeProvider? TimeProvider { get; set; }
}

/// <summary>Discovery filter; all set fields must match exactly (§4.3).</summary>
public sealed record AgentFilter
{
    /// <summary>Match <c>metadata.agent</c>.</summary>
    public string? Agent { get; init; }

    /// <summary>Match <c>metadata.owner</c>.</summary>
    public string? Owner { get; init; }

    /// <summary>Match the instance name (5th prompt-subject token).</summary>
    public string? Name { get; init; }

    /// <summary>Match <c>metadata.session</c>.</summary>
    public string? Session { get; init; }

    /// <summary>Match <c>metadata.protocol_version</c> verbatim.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>True when <paramref name="info"/> satisfies every set field.</summary>
    public bool Matches(AgentInfo info)
        => (Agent is null || info.Agent == Agent)
           && (Owner is null || info.Owner == Owner)
           && (Name is null || info.Name == Name)
           && (Session is null || info.Session == Session)
           && (ProtocolVersion is null || info.ProtocolVersion == ProtocolVersion);
}

/// <summary>Per-prompt options.</summary>
public sealed class PromptOptions
{
    /// <summary>Overrides <see cref="AgentsClientOptions.InactivityTimeout"/>.</summary>
    public TimeSpan? InactivityTimeout { get; set; }

    /// <summary>Overrides <see cref="AgentsClientOptions.PromptMaxWait"/>.</summary>
    public TimeSpan? MaxWait { get; set; }

    /// <summary>
    /// When false (the default) the protocol-level <c>"ack"</c> status chunks are filtered out of the stream;
    /// set true to observe them (e.g. for liveness diagnostics).
    /// </summary>
    public bool IncludeAcks { get; set; }

    /// <summary>Extra request headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }
}
