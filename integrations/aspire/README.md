# Synadia.AI.Agents.Hosting.Aspire

> **Experimental, not published.** Nothing under `integrations/aspire/` is published to NuGet.
> It's a prototype of a .NET Aspire hosting integration for the
> [Synadia Agent Protocol for NATS](https://github.com/synadia-ai/synadia-agent-sdk-docs).

This package adds Aspire AppHost extensions for agents on the Synadia Agent Protocol:

- an **`agents` dashboard resource** that discovers, monitors and prompts every agent on a NATS server;
- helpers that start TypeScript, Python and .NET agents wired to that NATS server.

**Runnable demo:** [`examples/aspire-agent-fleet/`](../../examples/aspire-agent-fleet/) is an AppHost built on this package. One `aspire run` starts NATS, Ollama with its models, the TS / Python / .NET reference agents, the document-extraction agent and the browser client. The example's README covers the resource graph, settings, Ollama modes, live evidence and troubleshooting, and its [`DEMO.md`](../../examples/aspire-agent-fleet/DEMO.md) is a step-by-step walkthrough.

## API

```csharp
var nats = builder.AddNats("nats").WithJetStream();                       // Aspire.Hosting.Nats

builder.AddTypeScriptAgent("ts-agent", tsProjectDir, "examples/_run-reference-agent.ts", nats, installPackages);
builder.AddPythonAgent("py-agent", pyProjectDir, "examples/_reference_agent.py", nats, installPackages)
       .WithArgs("--agent", "py-demo-agent", "--owner", "aspire");

builder.AddBunApp("web-ui", dir, "server/index.ts").WithNatsUrl(nats);    // any resource with env

builder.AddDotNetAgent<Projects.MyAgent>("dotnet-agent", nats)            // or AddDotNetAgent(name, csprojPath, nats)
       .WithReference(chat)                                               // e.g. an Ollama model resource
       .WaitFor(chat);

nats.AddSynadiaAgents("agents", o =>
{
    o.MinimumAgents = 4;                                 // health threshold
    o.Client = AgentFleetClientKind.DotNetSdk;           // default; RawNats = NATS.Net only
    o.AttachmentBaseDirectory = repoRoot;                // Prompt command: relative attachment paths
    o.MissedHeartbeatsBeforeStale = 3;
    o.RediscoverInterval = TimeSpan.FromSeconds(30);
    o.PromptTimeout = TimeSpan.FromSeconds(90);
    o.SmokePrompt = "Hello from .NET Aspire! ...";
    o.ShowAgentsAsResources = true;                      // nested rows per instance
    o.LogHeartbeats = true;                              // resource console log
});
```

| Method | Purpose |
| --- | --- |
| `AddSynadiaAgents(nats, name, configure)` | Fleet monitor resource with commands, nested under `nats` |
| `WithNatsUrl(nats)` | Injects `NATS_URL` (with credentials) and `WaitFor(nats)` into any `IResourceWithEnvironment & IResourceWithWaitSupport` |
| `AddTypeScriptAgent(name, dir, script, nats, install)` | bun app (CommunityToolkit.Aspire.Hosting.Bun) + `WithNatsUrl` |
| `AddPythonAgent(name, dir, script, nats, install)` | `AddPythonApp` on the project's `.venv` + `WithNatsUrl`, unbuffered output, UTF-8 |
| `AddDotNetAgent<TProject>(name, nats)` / `AddDotNetAgent(name, csprojPath, nats)` | .NET project + `WithReference(nats)` (`ConnectionStrings__nats`, for `Aspire.NATS.Net`'s `AddNatsClient("nats")`) + `NATS_URL` + `WaitFor(nats)` |

Reference the library from an AppHost with `IsAspireProjectResource="false"`, so Aspire treats it as a library rather than a project resource:

```xml
<ProjectReference Include="..\..\..\integrations\aspire\src\Synadia.AI.Agents.Hosting.Aspire\Synadia.AI.Agents.Hosting.Aspire.csproj" IsAspireProjectResource="false" />
```

## The `agents` resource

`AddSynadiaAgents` is a custom dashboard resource. It does no protocol work inside the agents themselves; it watches the fleet from the outside, the same way any client would:

- **Heartbeats.** It subscribes to `agents.hb.>`. Each agent instance becomes a child resource row (Alive, then **Stale** after `MissedHeartbeatsBeforeStale` missed intervals). Its properties include the agent key, instance id, protocol and harness versions, prompt subject and last heartbeat.
- **Discovery.** It sends a `$SRV.INFO.agents` request-many with a stall strategy (750 ms idle window, 5 s cap) on start, every `RediscoverInterval`, and on demand.
- **Health.** The resource is Healthy once `MinimumAgents` instances are alive.
- **External agents too.** Anything heartbeating on this NATS server shows up, including agents Aspire did not launch.
- **Client.** By default the commands go through the **Synadia .NET SDK caller** (`agent-sdk/dotnet`): `AgentsClient.DiscoverAsync`, `Agent.GetStatusAsync`, `Agent.AsIChatClient()` streaming and `HeartbeatTracker`. `o.Client = AgentFleetClientKind.RawNats` switches to an SDK-independent NATS.Net implementation behind the same internal `IAgentFleetClient` seam.

| Command | Where | What it does |
| --- | --- | --- |
| **Discover** | `agents` | `$SRV.INFO.agents` → markdown table of every instance |
| **Status** | `agents` and each agent row | Request to each agent's protocol status endpoint (from its INFO metadata) → live status and last heartbeat |
| **Prompt (smoke)** | `agents` (all or filtered) and each agent row | Sends a prompt, optionally with one file **attachment** (§5.2; relative paths resolve from `AttachmentBaseDirectory`). Streams chunks until the protocol terminator, then reports the result, chunk count and latency per agent |
| **Connect a client** | `agents` | Prints the credentialed `NATS_URL` with PowerShell and bash snippets, so external agents and clients can join |

All commands work from the dashboard, the CLI (`aspire resource agents discover|status|prompt|connect`) and the Aspire MCP server.

Notes:
- **Attachments read local files.** The Prompt command's attachment field reads any path the AppHost process can read. That's fine for a local dev dashboard; don't expose it beyond that.
- **Nested agent rows are dashboard-only.** They don't appear in `aspire describe` or the MCP `list_resources`. From the CLI, use the `agents` commands with `--agent`.

## Code layout

```
src/Synadia.AI.Agents.Hosting.Aspire/
  Protocol/FleetProtocol.cs        wire parsing (INFO, heartbeat, prompt chunks), no NATS dependency
  Client/IAgentFleetClient.cs      internal seam: discover / status / prompt / heartbeats
  Client/SdkAgentFleetClient.cs    default: Synadia .NET SDK caller (AgentsClient, Agent.AsIChatClient)
  Client/NatsAgentFleetClient.cs   alternative over raw NATS.Net 3.x
  AgentFleetMonitor.cs             heartbeat tracking, child resources, health
  SynadiaAgentsCommands.cs         dashboard/CLI/MCP commands
  SynadiaAgentsBuilderExtensions.cs
tests/Synadia.AI.Agents.Hosting.Aspire.Tests/
                                   xunit, real payloads captured from the TS and Python SDKs
```

Build and test (.NET 10 SDK):

```powershell
dotnet build integrations/aspire/Synadia.AI.Agents.Hosting.Aspire.slnx
dotnet test  integrations/aspire/Synadia.AI.Agents.Hosting.Aspire.slnx   # 27 tests: protocol parsing, SDK client mapping, hosting behavior
```

## NATS.Net version note

- `Aspire.NATS.Net` 13.6.5 and `Aspire.Hosting.Nats` 13.6.5 depend on **NATS.Net ≥ 2.8.2**. Older versions: 13.5.4 → 2.8.0, 13.0.x → 2.6.11.
- This package and the .NET SDK reference **NATS.Net 3.3.2**. A probe app referencing `Aspire.NATS.Net` 13.6.5 together with NATS.Net 3.3.2 worked: `AddNatsClient`, `PingAsync`, the Aspire health check (Healthy) and subscriptions.
- So a NATS.Net 3.x-based agent can use the Aspire client integration as-is, by referencing NATS.Net 3.x directly (the dependency floor is `≥`).
