# Synadia.AI.Agents.AgentFramework

Use agents on NATS from Microsoft Agent Framework, and serve Agent Framework agents on NATS. A remote agent becomes an
`AIAgent`; any `AIAgent` becomes a discoverable NATS agent.

> Preview — APIs may change before 1.0.

```shell
dotnet add package Synadia.AI.Agents.AgentFramework --prerelease
```

## Requirements

- .NET 8 or .NET 10.
- A NATS server (`nats-server`).

## Call a remote agent as an `AIAgent`

```csharp
using Microsoft.Agents.AI;
using NATS.Client.Core;
using Synadia.AI.Agents;
using Synadia.AI.Agents.AgentFramework;

await using var nats = new NatsConnection(new NatsOpts { Url = "nats://localhost:4222" });
await using var agents = new AgentsClient(nats);
var remote = (await agents.DiscoverAsync(new AgentFilter { Agent = "demo-agent" }))[0];

AIAgent agent = remote.AsAIAgent();
AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("hello", session));
```

Against the reference agent from `Synadia.AI.Agents.Testing`, this prints `echo: hello`. The session keeps the
conversation, so later turns reach the same remote conversation.

## Serve an `AIAgent` on NATS

This serves a `ChatClientAgent` (package `Microsoft.Agents.AI`) over a local Ollama model:

```csharp
using Microsoft.Agents.AI;
using NATS.Client.Core;
using OllamaSharp;
using Synadia.AI.Agents.AgentFramework;
using Synadia.AI.Agents.Service;

await using var nats = new NatsConnection(new NatsOpts { Url = "nats://localhost:4222" });
var concierge = new ChatClientAgent(new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3.5:4b"),
    instructions: "You are a concise concierge.", name: "concierge");

await using var service = new AgentService(nats, new AgentServiceOptions { Agent = "maf", Owner = "demo", Name = "concierge" })
    .ServeAIAgent(concierge);
await service.StartAsync();
Console.ReadLine();
```

Each caller conversation gets its own `AgentSession`. Tool approval requests become protocol queries to the caller.

In a host app, `services.AddNatsAgentServer(...)` registers the same thing as a hosted service.

## Keep conversations across restarts

By default, sessions live in memory. Set `SessionStore` to any Agent Framework `AgentSessionStore` to load each
session before a run and save it after:

```csharp
#pragma warning disable SYNAGENT001, MAAI001 // experimental: SessionStore, AgentSessionStore
service.ServeAIAgent(concierge, new AIAgentServeOptions { SessionStore = sessionStore });
#pragma warning restore SYNAGENT001, MAAI001
```

`SessionStore` is experimental ([SYNAGENT001](https://github.com/synadia-ai/synadia-agents/blob/main/docs/dotnet-diagnostics.md#synagent001)).

## `NatsAIAgentOptions` (calling)

| Option | Default | Effect |
| --- | --- | --- |
| `Name` / `Id` / `Description` | from the remote agent | The `AIAgent` identity. |
| `IncludeStatus` | `true` | Surfaces status chunks as `AgentStatusContent`. |
| `QueryMode` | `InputRequest` | How a remote question surfaces: `InputRequest`, `FunctionCall` or `ToolApproval`. |
| `QueryHandler` | `null` | Answers remote questions inline instead of surfacing them. |
| `PromptOptions` | `null` | Per-prompt protocol options, such as timeouts. |

## `AIAgentServeOptions` (serving)

| Option | Default | Effect |
| --- | --- | --- |
| `ConversationKey` | the caller's `session_id` extra | Picks the `AgentSession` for a prompt. |
| `SessionStore` | `null` (in memory) | Persists sessions. Experimental. |
| `RunOptions` | `null` | Passed to every run. |
| `IncludeReasoning` | `false` | Streams reasoning text to the caller. |
| `Approval` | `null` | Prompt, parser and timeout for tool approvals. A timeout denies. |
| `QueryTimeout` | the service default | How long a query waits for the caller. |
| `MaxInputRounds` | `8` | Maximum approval rounds per prompt. |

## Learn more

- [Protocol specification](https://github.com/synadia-ai/synadia-agent-sdk-docs)
- [Adapter guide](https://github.com/synadia-ai/synadia-agents/tree/main/integrations/agent-framework/src/Synadia.AI.Agents.AgentFramework)
- [Samples](https://github.com/synadia-ai/synadia-agents/tree/main/integrations/agent-framework/samples)
- Companion packages: [Synadia.AI.Agents](https://www.nuget.org/packages/Synadia.AI.Agents) and
  [Synadia.AI.Agents.Service](https://www.nuget.org/packages/Synadia.AI.Agents.Service).
