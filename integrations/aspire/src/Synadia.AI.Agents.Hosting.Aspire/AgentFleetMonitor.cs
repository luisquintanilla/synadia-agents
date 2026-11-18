using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Synadia.AI.Agents.Hosting.Aspire.Client;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire;

/// <summary>
/// Owns the NATS connection for a <see cref="SynadiaAgentsResource"/>, tracks agent liveness from
/// heartbeats + discovery, and keeps the dashboard snapshots (parent + per-agent children) current.
/// </summary>
internal sealed class AgentFleetMonitor : IAsyncDisposable
{
    private readonly SynadiaAgentsResource _resource;
    private readonly ResourceNotificationService _notifications;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TrackedAgent> _agents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _loops = [];
    private IAgentFleetClient? _client;
    private string? _displayUrl;
    private int _lastAliveCount = -1;

    public AgentFleetMonitor(SynadiaAgentsResource resource, ResourceNotificationService notifications, ILogger logger, TimeProvider? time = null)
    {
        _resource = resource;
        _notifications = notifications;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConnected => _client is not null;

    public IAgentFleetClient Client => _client ?? throw new InvalidOperationException("The agent fleet monitor is not connected to NATS yet.");

    public int AliveCount => _agents.Values.Count(a => a.IsAlive(_time.GetUtcNow(), _resource.Options.MissedHeartbeatsBeforeStale));

    public IReadOnlyList<TrackedAgent> Agents => _agents.Values.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();

    public async Task StartAsync(string natsUrl, AgentFleetClientFactory factory, CancellationToken cancellationToken)
    {
        _displayUrl = RedactUserInfo(natsUrl);
        _client = await factory(natsUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Connected to NATS at {Url}; watching {Subject} and {Info}", _displayUrl, FleetProtocol.HeartbeatWildcard, FleetProtocol.InfoSubject);

        _loops.Add(Task.Run(() => HeartbeatLoopAsync(_stopping.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => HousekeepingLoopAsync(_stopping.Token), CancellationToken.None));
        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <c>$SRV.INFO.agents</c> and merges the results into the tracked set.</summary>
    public async Task<IReadOnlyList<AgentInstance>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var found = await Client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        foreach (var info in found)
        {
            var tracked = _agents.GetOrAdd(info.InstanceId, id => new TrackedAgent(id));
            var isNew = tracked.Info is null;
            tracked.Info = info;
            tracked.LastDiscovered = now;
            if (isNew)
            {
                _logger.LogInformation("Discovered agent {Key} (instance {InstanceId}, protocol {Protocol}, harness {Version})", info.Key, info.InstanceId, info.ProtocolVersion, info.Version);
            }
        }

        await PublishAsync().ConfigureAwait(false);
        return found;
    }

    public AgentInstance? FindAgent(string keyOrInstanceId) =>
        _agents.Values.FirstOrDefault(a => a.InstanceId == keyOrInstanceId || a.Key == keyOrInstanceId)?.Info;

    /// <summary>Agents matching a filter: blank = all known; otherwise exact key/instance id, then substring on the key.</summary>
    public IReadOnlyList<AgentInstance> Select(string? filter)
    {
        var known = _agents.Values.Where(a => a.Info is not null).Select(a => a.Info!).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return known;
        }

        var exact = known.Where(a => a.Key == filter || a.InstanceId == filter).ToList();
        return exact.Count > 0 ? exact : known.Where(a => a.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var hb in Client.WatchHeartbeatsAsync(cancellationToken).ConfigureAwait(false))
                {
                    var tracked = _agents.GetOrAdd(hb.InstanceId, id => new TrackedAgent(id));
                    var wasAlive = tracked.IsAlive(_time.GetUtcNow(), _resource.Options.MissedHeartbeatsBeforeStale);
                    tracked.LastHeartbeat = hb;
                    tracked.LastHeartbeatReceived = _time.GetUtcNow();
                    tracked.HeartbeatCount++;

                    if (_resource.Options.LogHeartbeats)
                    {
                        _logger.LogInformation("heartbeat {Key} instance={InstanceId} interval={Interval}s seq#{Count}", hb.Key, hb.InstanceId, hb.IntervalSeconds, tracked.HeartbeatCount);
                    }

                    if (tracked.Info is null)
                    {
                        // A heartbeat from an instance we have not seen in INFO yet: discover it now.
                        _ = Task.Run(() => SafeDiscoverAsync(cancellationToken), CancellationToken.None);
                    }
                    else if (!wasAlive)
                    {
                        _logger.LogInformation("Agent {Key} is alive", hb.Key);
                    }

                    await PublishAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat subscription failed; retrying in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HousekeepingLoopAsync(CancellationToken cancellationToken)
    {
        var lastDiscover = _time.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_time.GetUtcNow() - lastDiscover >= _resource.Options.RediscoverInterval)
                {
                    lastDiscover = _time.GetUtcNow();
                    await SafeDiscoverAsync(cancellationToken).ConfigureAwait(false);
                }

                await PublishAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SafeDiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background discovery failed");
        }
    }

    private readonly SemaphoreSlim _publishLock = new(1, 1);

    /// <summary>Pushes the parent + child snapshots to the dashboard.</summary>
    public async Task PublishAsync()
    {
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var missed = _resource.Options.MissedHeartbeatsBeforeStale;
            var agents = Agents;
            var alive = agents.Count(a => a.IsAlive(now, missed));

            if (alive != _lastAliveCount)
            {
                _logger.LogInformation("{Alive} of {Known} known agent instance(s) alive", alive, agents.Count);
                _lastAliveCount = alive;
            }

            var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
            properties.Add(new ResourcePropertySnapshot("nats.url", _displayUrl));
            properties.Add(new ResourcePropertySnapshot("agents.alive", alive.ToString(CultureInfo.InvariantCulture)));
            properties.Add(new ResourcePropertySnapshot("agents.known", agents.Count.ToString(CultureInfo.InvariantCulture)));
            properties.Add(new ResourcePropertySnapshot("agents.minimum", _resource.Options.MinimumAgents.ToString(CultureInfo.InvariantCulture)));
            foreach (var agent in agents)
            {
                properties.Add(new ResourcePropertySnapshot($"agent: {agent.Key}", agent.Describe(now, missed)));
            }

            // Stay in "Running" so Aspire evaluates the health check (healthy once MinimumAgents are alive).
            var style = alive >= _resource.Options.MinimumAgents ? KnownResourceStateStyles.Success : KnownResourceStateStyles.Info;
            await _notifications.PublishUpdateAsync(_resource, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Running, style),
                StartTimeStamp = s.StartTimeStamp ?? now.UtcDateTime,
                Properties = MergeProperties(s.Properties, properties.ToImmutable()),
            }).ConfigureAwait(false);

            if (_resource.Options.ShowAgentsAsResources)
            {
                foreach (var agent in agents)
                {
                    await PublishChildAsync(agent, now, missed).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task PublishChildAsync(TrackedAgent agent, DateTimeOffset now, int missed)
    {
        agent.Resource ??= CreateChildResource(agent);

        var state = agent.State(now, missed);
        var style = state switch
        {
            "Alive" => KnownResourceStateStyles.Success,
            "Stale" => KnownResourceStateStyles.Warn,
            _ => KnownResourceStateStyles.Info,
        };

        var props = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
        var info = agent.Info;
        var hb = agent.LastHeartbeat;
        // Nests the dynamic agent row under the fleet resource in the dashboard tree.
        props.Add(new ResourcePropertySnapshot(CustomResourceKnownProperties.Source, agent.Key));
        props.Add(new ResourcePropertySnapshot("resource.parentName", _resource.Name));
        props.Add(new ResourcePropertySnapshot("agent.key", agent.Key));
        props.Add(new ResourcePropertySnapshot("agent.instance_id", agent.InstanceId));
        props.Add(new ResourcePropertySnapshot("agent.session", info?.Session ?? hb?.Session));
        props.Add(new ResourcePropertySnapshot("agent.protocol_version", info?.ProtocolVersion ?? hb?.ProtocolVersion));
        props.Add(new ResourcePropertySnapshot("agent.version", info?.Version));
        props.Add(new ResourcePropertySnapshot("agent.description", info?.Description));
        props.Add(new ResourcePropertySnapshot("agent.prompt_subject", info?.PromptSubject ?? hb?.PromptSubject));
        props.Add(new ResourcePropertySnapshot("agent.status_subject", info?.StatusSubject));
        props.Add(new ResourcePropertySnapshot("heartbeat.last", agent.LastHeartbeatReceived?.ToString("O", CultureInfo.InvariantCulture)));
        props.Add(new ResourcePropertySnapshot("heartbeat.interval_s", hb?.IntervalSeconds.ToString(CultureInfo.InvariantCulture)));
        props.Add(new ResourcePropertySnapshot("heartbeat.count", agent.HeartbeatCount.ToString(CultureInfo.InvariantCulture)));
        if (info is not null)
        {
            foreach (var (key, value) in info.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                props.Add(new ResourcePropertySnapshot($"metadata.{key}", value));
            }
        }

        await _notifications.PublishUpdateAsync(agent.Resource, s => s with
        {
            ResourceType = "SynadiaAgent",
            State = new ResourceStateSnapshot(state, style),
            StartTimeStamp = s.StartTimeStamp ?? (agent.FirstSeen.UtcDateTime),
            Properties = MergeProperties(s.Properties, props.ToImmutable()),
        }).ConfigureAwait(false);
    }

    private DiscoveredAgentResource CreateChildResource(TrackedAgent agent)
    {
        var baseName = SanitizeName(agent.Key);
        var name = _agents.Values.Any(a => a.Resource?.Name == baseName) ? $"{baseName}-{agent.InstanceId[..Math.Min(6, agent.InstanceId.Length)].ToLowerInvariant()}" : baseName;
        var child = new DiscoveredAgentResource(name, _resource, agent.Key);
        SynadiaAgentsCommands.AddAgentCommands(child);
        return child;
    }

    internal static string SanitizeName(string key)
    {
        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var name = new string(chars).Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        return name.Length == 0 ? "agent" : name;
    }

    private static ImmutableArray<ResourcePropertySnapshot> MergeProperties(ImmutableArray<ResourcePropertySnapshot> existing, ImmutableArray<ResourcePropertySnapshot> updates)
    {
        var map = new Dictionary<string, ResourcePropertySnapshot>(StringComparer.Ordinal);
        foreach (var p in existing)
        {
            map[p.Name] = p;
        }

        foreach (var p in updates)
        {
            map[p.Name] = p;
        }

        return [.. map.Values];
    }

    internal static string RedactUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            return url;
        }

        var user = uri.UserInfo.Split(':', 2)[0];
        return new UriBuilder(uri) { UserName = Uri.UnescapeDataString(user), Password = "****" }.Uri.ToString().TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown is best-effort.
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
        _publishLock.Dispose();
    }
}

/// <summary>Mutable per-instance tracking state.</summary>
internal sealed class TrackedAgent(string instanceId)
{
    public string InstanceId { get; } = instanceId;
    public AgentInstance? Info { get; set; }
    public FleetHeartbeat? LastHeartbeat { get; set; }
    public DateTimeOffset? LastHeartbeatReceived { get; set; }
    public DateTimeOffset? LastDiscovered { get; set; }
    public DateTimeOffset FirstSeen { get; } = DateTimeOffset.UtcNow;
    public long HeartbeatCount { get; set; }
    public DiscoveredAgentResource? Resource { get; set; }

    public string Key => Info?.Key ?? LastHeartbeat?.Key ?? InstanceId;

    /// <summary>§8.2: online while the last heartbeat is within <c>slack × interval_s</c>.</summary>
    public bool IsAlive(DateTimeOffset now, int slack)
    {
        if (LastHeartbeatReceived is not { } last || LastHeartbeat is not { } hb)
        {
            return false;
        }

        var interval = hb.IntervalSeconds > 0 ? hb.IntervalSeconds : 5;
        return (now - last).TotalSeconds <= slack * interval;
    }

    public string State(DateTimeOffset now, int slack) =>
        IsAlive(now, slack) ? "Alive" : LastHeartbeatReceived is null ? "Discovered" : "Stale";

    public string Describe(DateTimeOffset now, int slack)
    {
        var age = LastHeartbeatReceived is { } last ? $"last heartbeat {(now - last).TotalSeconds:0}s ago" : "no heartbeat yet";
        return $"{State(now, slack)} — {age}; instance {InstanceId}";
    }
}
