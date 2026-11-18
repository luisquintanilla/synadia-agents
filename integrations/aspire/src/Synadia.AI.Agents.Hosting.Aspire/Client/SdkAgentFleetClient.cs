using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NATS.Client.Core;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;
using SdkAgent = Synadia.AI.Agents.Agent;
using SdkAgentsClient = Synadia.AI.Agents.AgentsClient;

namespace Synadia.AI.Agents.Hosting.Aspire.Client;

/// <summary>
/// Caller side backed by the Synadia .NET SDK (<c>Synadia.AI.Agents</c>): discovery via
/// <see cref="SdkAgentsClient.DiscoverAsync"/>, liveness via its <see cref="HeartbeatTracker"/>, status via
/// <see cref="SdkAgent.GetStatusAsync"/>, and prompts through the MEAI adapter <see cref="SdkAgent.AsIChatClient"/>
/// — i.e. the dashboard talks to agents exactly the way a .NET app would.
/// </summary>
internal sealed class SdkAgentFleetClient : IAgentFleetClient
{
    private readonly NatsConnection _connection;
    private readonly SdkAgentsClient _client;
    private readonly Dictionary<string, SdkAgent> _agents = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private SdkAgentFleetClient(NatsConnection connection)
    {
        _connection = connection;
        _client = new SdkAgentsClient(connection, new AgentsClientOptions
        {
            DiscoverStall = NatsAgentFleetClient.DiscoverStall,
            DiscoverMaxWait = NatsAgentFleetClient.DiscoverCap,
        });
    }

    public static async Task<IAgentFleetClient> ConnectAsync(string natsUrl, CancellationToken cancellationToken)
    {
        var connection = new NatsConnection(NatsAgentFleetClient.CreateOptions(natsUrl, "aspire-synadia-agents"));
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SdkAgentFleetClient(connection);
    }

    public async Task<IReadOnlyList<AgentInstance>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var agents = await _client.DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var agent in agents)
            {
                _agents[agent.Info.InstanceId] = agent;
            }
        }

        return agents.Select(ToInstance).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
    }

    public async IAsyncEnumerable<FleetHeartbeat> WatchHeartbeatsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<Synadia.AI.Agents.AgentHeartbeat>(new UnboundedChannelOptions { SingleReader = true });
        void OnBeat(Synadia.AI.Agents.AgentHeartbeat hb) => channel.Writer.TryWrite(hb);

        _client.Heartbeats.HeartbeatReceived += OnBeat;
        try
        {
            if (!_client.Heartbeats.IsStarted)
            {
                await _client.Heartbeats.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            await foreach (var hb in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Re-encode through the SDK's canonical wire form so the dashboard shows the same JSON as before.
                if (FleetProtocol.ParseHeartbeat(hb.Payload.Encode(), hb.Subject) is { } parsed)
                {
                    yield return parsed;
                }
            }
        }
        finally
        {
            _client.Heartbeats.HeartbeatReceived -= OnBeat;
        }
    }

    public async Task<FleetHeartbeat?> StatusAsync(AgentInstance agent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (agent.StatusSubject is null)
        {
            return null;
        }

        var sdkAgent = await ResolveAsync(agent, cancellationToken).ConfigureAwait(false);
        var payload = await sdkAgent.GetStatusAsync(timeout, cancellationToken).ConfigureAwait(false);
        return FleetProtocol.ParseHeartbeat(payload.Encode(), agent.PromptSubject);
    }

    public async Task<PromptReply> PromptAsync(AgentInstance agent, string prompt, IReadOnlyList<DataContent> attachments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        var statuses = new List<string>();
        var chunks = 0;
        string? error = null;
        var terminated = false;

        try
        {
            var sdkAgent = await ResolveAsync(agent, cancellationToken).ConfigureAwait(false);
            using var chat = sdkAgent.AsIChatClient(new AgentChatClientOptions
            {
                PromptOptions = new PromptOptions { MaxWait = timeout, IncludeAcks = true },
            });

            // AgentChatClient maps the message's DataContent items to §5.2 attachments.
            var message = new ChatMessage(ChatRole.User, [new TextContent(prompt), .. attachments]);
            await foreach (var update in chat.GetStreamingResponseAsync([message], cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (update.FinishReason == ChatFinishReason.Stop && update.Contents.Count == 0)
                {
                    terminated = true;
                    continue;
                }

                chunks++;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent t:
                            text.Append(t.Text);
                            break;
                        case AgentStatusContent s:
                            statuses.Add(s.Status);
                            break;
                        case AgentQueryContent q:
                            statuses.Add($"query (unanswered by smoke test): {q.Prompt}");
                            break;
                        case DataContent d:
                            statuses.Add($"attachment: {d.Name ?? d.MediaType} ({d.Data.Length} bytes)");
                            break;
                    }
                }
            }
        }
        catch (NoRespondersException)
        {
            error = "no responders";
        }
        catch (AgentServiceException ex)
        {
            error = $"{ex.Code}: {ex.Description}";
        }
        catch (Exception ex) when (ex is StreamMaxWaitExceededException or StreamStalledException or AgentException)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"timed out after {timeout.TotalSeconds:0}s waiting for the terminator";
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

    private async Task<SdkAgent> ResolveAsync(AgentInstance agent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(agent.InstanceId, out var known))
            {
                return known;
            }
        }

        var found = await _client.LookupAsync(agent.InstanceId, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new NoRespondersException(agent.PromptSubject);
        lock (_gate)
        {
            _agents[agent.InstanceId] = found;
        }

        return found;
    }

    internal static AgentInstance ToInstance(SdkAgent agent)
    {
        var info = agent.Info;
        return new AgentInstance(
            InstanceId: info.InstanceId,
            Agent: info.Agent,
            Owner: info.Owner,
            Name: info.Name,
            Session: info.Session,
            ProtocolVersion: info.ProtocolVersion,
            Description: info.Description,
            Version: info.Version,
            PromptSubject: info.PromptEndpoint.Subject,
            StatusSubject: info.StatusEndpoint?.Subject,
            Metadata: info.Metadata);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
