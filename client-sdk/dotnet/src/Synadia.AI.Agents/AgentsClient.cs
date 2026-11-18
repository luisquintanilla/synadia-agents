using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;

namespace Synadia.AI.Agents;

/// <summary>
/// Entry point of the caller SDK: discovers agents on a NATS connection (spec §4), tracks their heartbeats (§8)
/// and binds <see cref="Agent"/> handles you can prompt.
/// </summary>
/// <example>
/// <code>
/// await using var nc = new NatsClient("nats://localhost:4222");
/// await using var agents = new AgentsClient(nc.Connection);
/// foreach (var agent in await agents.DiscoverAsync())
/// {
///     await foreach (var chunk in agent.PromptAsync("hello"))
///         if (chunk is AgentResponseChunk r) Console.Write(r.Text);
/// }
/// </code>
/// </example>
public sealed class AgentsClient : IAsyncDisposable
{
    /// <summary>Create a client over an existing NATS connection. The connection is not owned.</summary>
    public AgentsClient(INatsConnection connection, AgentsClientOptions? options = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Options = options ?? new AgentsClientOptions();
        Logger = Options.Logger ?? NullLogger.Instance;
        Heartbeats = new HeartbeatTracker(connection, Logger, Options.TimeProvider);
    }

    /// <summary>The underlying NATS connection.</summary>
    public INatsConnection Connection { get; }

    /// <summary>Client options.</summary>
    public AgentsClientOptions Options { get; }

    /// <summary>The heartbeat tracker (started lazily by <see cref="DiscoverAsync"/>).</summary>
    public HeartbeatTracker Heartbeats { get; }

    internal ILogger Logger { get; }

    /// <summary>
    /// Discover running agents via <c>$SRV.INFO.agents</c> (§4.3). Starts the heartbeat tracker first so the
    /// subscription is in place before discovery (§8.5). Instances are de-duplicated by id.
    /// </summary>
    public async Task<IReadOnlyList<Agent>> DiscoverAsync(AgentFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await Heartbeats.StartAsync(cancellationToken).ConfigureAwait(false);
        var infos = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return infos.Where(i => filter is null || filter.Matches(i)).Select(i => new Agent(this, i)).ToArray();
    }

    /// <summary>Raw discovery: every valid <see cref="AgentInfo"/> currently answering <c>$SRV.INFO.agents</c>.</summary>
    public async Task<IReadOnlyList<AgentInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, AgentInfo>(StringComparer.Ordinal);
        var replyOpts = new NatsSubOpts
        {
            Timeout = Options.DiscoverMaxWait,
            StartUpTimeout = Options.DiscoverMaxWait,
            IdleTimeout = Options.DiscoverStall,
        };
        try
        {
            await foreach (var msg in Connection.RequestManyAsync<byte[], ReadOnlyMemory<byte>>(
                               $"$SRV.INFO.{AgentProtocol.ServiceName}", [], replyOpts: replyOpts,
                               cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (msg.HasNoResponders) break;
                var info = ParseInfo(msg.Data.Span);
                if (info is not null) found.TryAdd(info.InstanceId, info);
            }
        }
        catch (NatsNoRespondersException)
        {
        }
        return found.Values.ToArray();
    }

    /// <summary>Look up one instance by id via <c>$SRV.INFO.agents.&lt;id&gt;</c>; <c>null</c> if absent or invalid.</summary>
    public async Task<Agent?> LookupAsync(string instanceId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!AgentSubject.IsValidToken(instanceId)) return null;
        try
        {
            var reply = await Connection.RequestAsync<byte[], ReadOnlyMemory<byte>>(
                $"$SRV.INFO.{AgentProtocol.ServiceName}.{instanceId}", [],
                replyOpts: new NatsSubOpts { Timeout = timeout ?? Options.DiscoverMaxWait },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (reply.HasNoResponders) return null;
            var info = ParseInfo(reply.Data.Span);
            return info is null ? null : new Agent(this, info);
        }
        catch (NatsNoRespondersException)
        {
            return null;
        }
        catch (NatsNoReplyException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>$SRV.PING.agents.&lt;id&gt;</c> (§8.4). Returns the round-trip time, or <c>null</c> when the instance
    /// did not answer within <paramref name="timeout"/>.
    /// </summary>
    public async Task<TimeSpan?> PingAsync(string instanceId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        AgentSubject.ValidateToken(instanceId, nameof(instanceId));
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await Connection.RequestAsync<byte[], ReadOnlyMemory<byte>>(
                $"$SRV.PING.{AgentProtocol.ServiceName}.{instanceId}", [],
                replyOpts: new NatsSubOpts { Timeout = timeout ?? Options.DiscoverMaxWait },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.HasNoResponders ? null : sw.Elapsed;
        }
        catch (NatsNoRespondersException)
        {
            return null;
        }
        catch (NatsNoReplyException)
        {
            return null;
        }
    }

    /// <summary>Bind an <see cref="Agent"/> handle to an already-known <see cref="AgentInfo"/>.</summary>
    public Agent Bind(AgentInfo info) => new(this, info ?? throw new ArgumentNullException(nameof(info)));

    /// <summary>Heartbeat-derived liveness for <paramref name="instanceId"/>; starts the tracker if needed.</summary>
    public async Task<AgentLiveness?> GetLivenessAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await Heartbeats.StartAsync(cancellationToken).ConfigureAwait(false);
        return Heartbeats.GetLiveness(instanceId);
    }

    internal static AgentInfo? ParseInfo(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            var dto = JsonSerializer.Deserialize(body, AgentsJsonContext.Default.SrvInfoDto);
            return dto is null ? null : AgentInfo.From(dto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stops the heartbeat tracker. Does not close the NATS connection.</summary>
    public ValueTask DisposeAsync() => Heartbeats.DisposeAsync();
}
