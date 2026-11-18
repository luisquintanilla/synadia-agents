using Aspire.Hosting.ApplicationModel;

namespace Synadia.AI.Agents.Hosting.Aspire;

/// <summary>
/// A dashboard-only resource that watches a NATS server for Synadia Agent Protocol agents:
/// it subscribes to heartbeats (<c>agents.hb.&gt;</c>), runs <c>$SRV.INFO.agents</c> discovery,
/// and exposes Discover / Status / Prompt commands. It does not start any process.
/// </summary>
public sealed class SynadiaAgentsResource(string name, NatsServerResource nats)
    : Resource(name), IResourceWithParent<NatsServerResource>, IResourceWithWaitSupport
{
    public NatsServerResource Parent { get; } = nats;

    public SynadiaAgentsOptions Options { get; } = new();

    internal AgentFleetMonitor? Monitor { get; set; }
}

/// <summary>
/// A protocol-level agent instance discovered on NATS. These are published to the dashboard at
/// run time (they are not part of the static app model) and nest under <see cref="SynadiaAgentsResource"/>.
/// </summary>
public sealed class DiscoveredAgentResource(string name, SynadiaAgentsResource fleet, string agentKey)
    : Resource(name), IResourceWithParent<SynadiaAgentsResource>
{
    public SynadiaAgentsResource Parent { get; } = fleet;

    /// <summary><c>agent/owner/name</c>.</summary>
    public string AgentKey { get; } = agentKey;
}

/// <summary>Tunables for <see cref="SynadiaAgentsResource"/>.</summary>
public sealed class SynadiaAgentsOptions
{
    /// <summary>The resource reports Healthy once at least this many agents are heartbeating. Default 1.</summary>
    public int MinimumAgents { get; set; } = 1;

    /// <summary>Write each heartbeat to the resource's console log. Default <c>true</c>.</summary>
    public bool LogHeartbeats { get; set; } = true;

    /// <summary>Publish every discovered instance as its own child row in the dashboard. Default <c>true</c>.</summary>
    public bool ShowAgentsAsResources { get; set; } = true;

    /// <summary>How often to re-run <c>$SRV.INFO.agents</c> discovery in the background. Default 30s.</summary>
    public TimeSpan RediscoverInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default per-agent timeout for the Prompt command. Default 90s (local LLMs can be slow).</summary>
    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Default prompt text for the Prompt (smoke) command.</summary>
    public string SmokePrompt { get; set; } = "Hello from .NET Aspire! Please reply with one short sentence.";

    /// <summary>
    /// Directory that relative <c>attachment</c> paths in the Prompt command resolve against.
    /// Default <c>null</c> = the AppHost's working directory.
    /// </summary>
    public string? AttachmentBaseDirectory { get; set; }

    /// <summary>
    /// An agent is considered stale after this many missed heartbeat intervals (spec §8 liveness). Default 3.
    /// </summary>
    public int MissedHeartbeatsBeforeStale { get; set; } = 3;

    /// <summary>
    /// Which caller implementation drives discovery/status/prompt. Default <see cref="AgentFleetClientKind.DotNetSdk"/>.
    /// </summary>
    public AgentFleetClientKind Client { get; set; } = AgentFleetClientKind.DotNetSdk;
}

/// <summary>Caller implementations available to <see cref="SynadiaAgentsResource"/>.</summary>
public enum AgentFleetClientKind
{
    /// <summary>The Synadia .NET SDK (<c>AgentsClient</c> / <c>Agent.AsIChatClient</c>).</summary>
    DotNetSdk,

    /// <summary>Hand-rolled NATS.Net requests — an SDK-independent wire-level cross-check.</summary>
    RawNats,
}
