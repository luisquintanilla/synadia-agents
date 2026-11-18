using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NATS.Client.Core;
using Synadia.AI.Agents;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire.Client;

/// <summary>
/// Raw NATS.Net (core request/reply + subscriptions) implementation of the caller side.
/// Deliberately uses only core NATS so it matches what the TS/Python SDKs put on the wire.
/// </summary>
internal sealed class NatsAgentFleetClient : IAgentFleetClient
{
    /// <summary>Spec-aligned default idle window after the last discovery reply (TS: DEFAULT_DISCOVER_STALL_MS).</summary>
    internal static readonly TimeSpan DiscoverStall = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan DiscoverFirstReply = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DiscoverCap = TimeSpan.FromSeconds(5);

    private static readonly NatsRawSerializer<byte[]> Raw = NatsRawSerializer<byte[]>.Default;
    private readonly NatsConnection _connection;

    private NatsAgentFleetClient(NatsConnection connection) => _connection = connection;

    public static async Task<IAgentFleetClient> ConnectAsync(string natsUrl, CancellationToken cancellationToken)
    {
        var connection = new NatsConnection(CreateOptions(natsUrl, "aspire-synadia-agents"));
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new NatsAgentFleetClient(connection);
    }

    /// <summary>
    /// NATS.Net does not reliably take credentials from URL userinfo, so split
    /// <c>nats://user:pass@host:port</c> into <see cref="NatsOpts.Url"/> + <see cref="NatsAuthOpts"/>.
    /// </summary>
    internal static NatsOpts CreateOptions(string natsUrl, string clientName)
    {
        var uri = new Uri(natsUrl);
        var opts = NatsOpts.Default with { Name = clientName };
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return opts with { Url = natsUrl };
        }

        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        var bare = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString().TrimEnd('/');
        return opts with
        {
            Url = bare,
            AuthOpts = NatsAuthOpts.Default with { Username = user, Password = pass },
        };
    }

    public async Task<IReadOnlyList<AgentInstance>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, AgentInstance>(StringComparer.Ordinal);
        var inbox = _connection.NewInbox();
        await using var sub = await _connection.SubscribeCoreAsync(inbox, serializer: Raw, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _connection.PublishAsync(FleetProtocol.InfoSubject, Array.Empty<byte>(), replyTo: inbox, serializer: Raw, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(DiscoverCap);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cap.Token);
        idle.CancelAfter(DiscoverFirstReply);

        try
        {
            while (true)
            {
                var msg = await sub.Msgs.ReadAsync(idle.Token).ConfigureAwait(false);
                idle.CancelAfter(DiscoverStall);
                if (msg.Data is { Length: > 0 } data && FleetProtocol.ParseServiceInfo(data) is { } agent)
                {
                    results[agent.InstanceId] = agent;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stall / cap reached: discovery is complete.
        }
        catch (ChannelClosedException)
        {
        }

        return results.Values.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
    }

    public async IAsyncEnumerable<FleetHeartbeat> WatchHeartbeatsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var msg in _connection.SubscribeAsync(FleetProtocol.HeartbeatWildcard, serializer: Raw, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (msg.Data is { Length: > 0 } data && FleetProtocol.ParseHeartbeat(data, msg.Subject) is { } hb)
            {
                yield return hb;
            }
        }
    }

    public async Task<FleetHeartbeat?> StatusAsync(AgentInstance agent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (agent.StatusSubject is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var reply = await _connection.RequestAsync(agent.StatusSubject, Array.Empty<byte>(), requestSerializer: Raw, replySerializer: Raw, cancellationToken: cts.Token).ConfigureAwait(false);
        return reply.Data is { Length: > 0 } data ? FleetProtocol.ParseHeartbeat(data, agent.PromptSubject) : null;
    }

    public async Task<PromptReply> PromptAsync(AgentInstance agent, string prompt, IReadOnlyList<DataContent> attachments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = new System.Text.StringBuilder();
        var statuses = new List<string>();
        var chunks = 0;
        string? error = null;
        var terminated = false;

        var inbox = _connection.NewInbox();
        await using var sub = await _connection.SubscribeCoreAsync(inbox, serializer: Raw, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = attachments.Count == 0 ? FleetProtocol.EncodePrompt(prompt) : new PromptEnvelope(prompt, attachments).Encode();
        await _connection.PublishAsync(agent.PromptSubject, payload, replyTo: inbox, serializer: Raw, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (!terminated)
            {
                var msg = await sub.Msgs.ReadAsync(cts.Token).ConfigureAwait(false);
                var headers = msg.Headers;

                if (headers is { Code: 503 })
                {
                    error = "no responders";
                    break;
                }

                if (headers is not null && headers.TryGetValue(FleetProtocol.ServiceErrorCodeHeader, out var code))
                {
                    headers.TryGetValue(FleetProtocol.ServiceErrorHeader, out var description);
                    error = $"{code}: {description}";
                    if (msg.Data is not { Length: > 0 })
                    {
                        // An error reply may itself close the stream when the agent sends no separate terminator.
                        cts.CancelAfter(TimeSpan.FromSeconds(2));
                    }

                    continue;
                }

                if (msg.Data is not { Length: > 0 } data)
                {
                    // Spec §6: an empty-body, headerless message terminates the stream.
                    terminated = true;
                    break;
                }

                chunks++;
                if (FleetProtocol.ParseChunk(data) is not { } chunk)
                {
                    continue;
                }

                switch (chunk.Type)
                {
                    case "response":
                        text.Append(chunk.Data);
                        break;
                    case "status":
                        statuses.Add(chunk.Data ?? string.Empty);
                        break;
                    case "query":
                        statuses.Add($"query (unanswered by smoke test): {chunk.Data}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error ??= $"timed out after {timeout.TotalSeconds:0}s waiting for the terminator";
        }

        return new PromptReply(
            AgentKey: agent.Key,
            Success: terminated && error is null,
            Text: text.ToString(),
            ChunkCount: chunks,
            StatusUpdates: statuses,
            Error: error,
            Elapsed: stopwatch.Elapsed);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
