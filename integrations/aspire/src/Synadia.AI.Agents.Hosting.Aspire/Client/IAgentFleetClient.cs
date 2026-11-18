using Microsoft.Extensions.AI;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire.Client;

/// <summary>
/// The caller-side operations the Aspire integration needs. Phase 1 implements this with raw
/// NATS.Net (<see cref="NatsAgentFleetClient"/>); Phase 2 swaps in the Synadia .NET SDK caller
/// without touching the resource, dashboard commands, or health checks.
/// </summary>
internal interface IAgentFleetClient : IAsyncDisposable
{
    /// <summary>Spec §4: <c>$SRV.INFO.agents</c> request-many with the stall strategy.</summary>
    Task<IReadOnlyList<AgentInstance>> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Spec §8: live <c>agents.hb.&gt;</c> stream.</summary>
    IAsyncEnumerable<FleetHeartbeat> WatchHeartbeatsAsync(CancellationToken cancellationToken);

    /// <summary>Spec §8.7: request/response on the instance's <c>status</c> endpoint.</summary>
    Task<FleetHeartbeat?> StatusAsync(AgentInstance agent, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Spec §5–§6: send a prompt (plus optional §5.2 attachments) and collect the streamed reply until the terminator.</summary>
    Task<PromptReply> PromptAsync(AgentInstance agent, string prompt, IReadOnlyList<DataContent> attachments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Creates an <see cref="IAgentFleetClient"/> bound to a NATS URL.</summary>
internal delegate Task<IAgentFleetClient> AgentFleetClientFactory(string natsUrl, CancellationToken cancellationToken);
