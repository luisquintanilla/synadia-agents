# Synadia.AI.Agents.AgentFramework

[Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) adapter for the
[Synadia Agent Protocol for NATS](https://github.com/synadia-ai/synadia-agent-sdk-docs). It works in both
directions:

| Direction | API | What it does |
| --- | --- | --- |
| **Call** a remote NATS agent from MAF | `agent.AsAIAgent()` → `NatsAIAgent : AIAgent` | Any discovered NATS agent (TS, Python, .NET, Claude Code, …) becomes a MAF `AIAgent`. Use it with `RunAsync`, sessions, `AsAIFunction()` (as a tool of another agent) and workflows. |
| **Serve** a MAF agent on NATS | `agentService.ServeAIAgent(aiAgent)` / `services.AddNatsAgentServer(...)` | Any MAF `AIAgent` (e.g. `ChatClientAgent`) becomes a protocol-compliant NATS agent that every SDK can discover and prompt. |

It is a thin layer over the MEAI-native core (`Synadia.AI.Agents` / `Synadia.AI.Agents.Service`): the wire protocol,
streaming, status, queries, and heartbeats all live in the SDK. This package only maps MAF concepts
(sessions, input requests, tool approvals) onto them.

## Packages and versions

Pinned to **MAF 1.28.0** (the stable GA line on nuget.org):

- The library depends only on `Microsoft.Agents.AI.Abstractions` 1.28.0 (`AIAgent`, `AgentSession`, `AgentRunOptions`,
  `AgentResponse[Update]`) plus `Microsoft.Extensions.Hosting.Abstractions` for the hosted service.
- `Microsoft.Agents.AI` 1.28.0 (`ChatClientAgent`, `AsAIFunction`) and `Microsoft.Agents.AI.Workflows` 1.28.0
  (`AgentWorkflowBuilder`, `InProcessExecution`, `RequestInfoEvent`) are only needed by consumers; the tests and the
  sample use them.
- `Microsoft.Agents.AI.Hosting` is **preview-only** (`1.28.0-preview.*`) and pulls in ASP.NET Core. This package does
  not depend on it. `AddNatsAgentServer(agentName, …)` resolves a *keyed* `AIAgent`, the same registration MAF
  Hosting's `AddAIAgent(name, …)` produces, so the two compose without a hard dependency.
- MAF 1.28.0 targets MEAI 10.x; the SDK uses MEAI 11.0.0, which it binds to without issue.

## Call a remote NATS agent from MAF

```csharp
await using var client = new AgentsClient(natsConnection);
var remote = (await client.DiscoverAsync(new AgentFilter { Name = "example" })).Single();

AIAgent agent = remote.AsAIAgent();                 // NatsAIAgent; Name/Description come from discovery
AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("hello", session));
Console.WriteLine(await agent.RunAsync("and again", session));   // same protocol conversation
```

- **Sessions.** A `NatsAgentSession` carries a protocol `session_id` (sent as a §5.6 envelope extra) so the
  remote agent can keep per-conversation memory. Sessions serialize with `SerializeSessionAsync` /
  `DeserializeSessionAsync`. Other `AgentRunOptions.AdditionalProperties` are forwarded as extras too.
- **Status** chunks arrive as `AgentStatusContent` in the response updates.
- **As a tool.** `agent.AsAIFunction()` turns the remote agent into a function tool (single `query` argument) for
  any `ChatClientAgent`. Without an explicit session each tool call uses a fresh session.

- **Fixed agent vs. dynamic discovery.** `AsAIAgent()` binds one known agent. To let a model discover and prompt
  agents itself, give a `ChatClientAgent` the SDK's agent tools instead:
  `new AgentTools(client).AsAITools()` (`discover_agents`, `prompt_agent`, `answer_agent`, …; `Synadia.AI.Agents.Tools`).
  Both rely on the `AgentsClient`, so its options (e.g. sender identity) apply to both.

### Remote questions (protocol §7 queries)

When the remote agent asks the caller something mid-stream, the run **pauses**: the stream is held open, the
question is surfaced, and the run returns. The *next* run on the same session answers it and continues the original
stream. `NatsAIAgentOptions.QueryMode` controls how the question is surfaced:

| `QueryMode` | Surfaced as | Answer with |
| --- | --- | --- |
| `InputRequest` (default) | `AgentQueryContent` (an MEAI `InputRequestContent`) | `query.CreateResponse("text")`, or just plain user text |
| `FunctionCall` | `FunctionCallContent("nats_agent_query", { prompt })` | `FunctionResultContent(callId, "text")` — this is what MAF **workflows** raise as a `RequestInfoEvent` |
| `ToolApproval` | `ToolApprovalRequestContent` | `approval.CreateResponse(true/false)` → sent as `yes`/`no` |

```csharp
var paused = await agent.RunAsync("ask What is your favorite color?", session);
var query = paused.Messages.SelectMany(m => m.Contents).OfType<AgentQueryContent>().Single();
var resumed = await agent.RunAsync(new ChatMessage(ChatRole.User, [query.CreateResponse("teal")]), session);
```

Alternatively, set `NatsAIAgentOptions.QueryHandler` to answer inline without pausing. Use a handler when the agent
runs as a tool (`AsAIFunction`): a function call cannot pause halfway.

**Workflows.** In `FunctionCall` mode, `AgentWorkflowBuilder.BuildSequential(natsAgent, …)` surfaces the remote
question as a `RequestInfoEvent`; answer it with `run.SendResponseAsync(request.CreateResponse(new FunctionResultContent(call.CallId, "answer")))`.
See `WorkflowSurfacesRemoteQueryAsRequestInfoInFunctionCallMode` in the tests.

## Serve a MAF agent on NATS

```csharp
var concierge = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "concierge",
    ChatOptions = new() { Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(BookTable))] },
});

await using var service = new AgentService(natsConnection, new AgentServiceOptions
{
    Agent = "maf", Owner = "demo", Name = "concierge", Description = "Books restaurant tables",
}).ServeAIAgent(concierge);
await service.StartAsync();
```

Or with dependency injection (registers an `IHostedService`; resolves `INatsConnection` from the container):

```csharp
builder.Services.AddKeyedSingleton<AIAgent>("concierge", (sp, _) => concierge);
builder.Services.AddNatsAgentServer("concierge", sp => new AgentServiceOptions { Agent = "maf", Owner = "demo", Name = "concierge" });
```

- One MAF `AgentSession` is kept per caller conversation (`AIAgentServeOptions.ConversationKey`, default: the
  envelope's `session_id` extra), so a `NatsAIAgent` caller gets multi-turn memory automatically.
- MAF human-in-the-loop requests become protocol queries to the caller:
  `ToolApprovalRequestContent` → `"Allow tool 'x' with arguments {…}? (yes/no)"` (customize with `Approval`, a
  `ToolApprovalOptions`; a timeout denies), and other input requests (including a nested `NatsAIAgent`'s
  `AgentQueryContent`, so queries relay through chains of agents) are asked verbatim.
- Updates stream back as protocol chunks; reasoning is dropped unless `IncludeReasoning` is set.
- Providers that ask for permission outside MAF content (for example GitHub Copilot's `OnPermissionRequest`) can
  use `AIAgentServeOptions.OnRun` to find the active `ServedRun` and ask the caller with `ServedRun.AskAsync`.

## Durable conversations

By default, `ServeAIAgent` keeps each conversation's `AgentSession` in memory, so a restart forgets it. Set
`SessionStore` to any MAF `AgentSessionStore` to load the session before each run and save it after:

```csharp
#pragma warning disable SYNAGENT001, MAAI001 // experimental: SessionStore, AgentSessionStore
await using var service = new AgentService(natsConnection, new AgentServiceOptions
{
    Agent = "maf", Owner = "demo", Name = "concierge",
}).ServeAIAgent(new ChatClientAgent(chatClient, name: "concierge"), new AIAgentServeOptions
{
    SessionStore = sessionStore, // e.g. a Cosmos DB or file-backed AgentSessionStore
});
#pragma warning restore SYNAGENT001, MAAI001
```

The store key is the conversation key (the caller's `session_id` extra), partitioned by `agent`, `owner` and
`session`, so one store can serve many agents. See
[SYNAGENT001](https://github.com/synadia-ai/synadia-agents/blob/main/docs/dotnet-diagnostics.md#synagent001).

For durable conversations over a plain `IChatClient`, serve `new ChatClientAgent(chatClient, …)` this way rather than
using `ServeChatClient`, whose history lives in memory.

## Layering

- `NatsAIAgent` is a custom `AIAgent` over `Agent.AsIChatClient()`, like `A2AAgent` is over A2A.
- `ServeAIAgent` is the Agent Framework twin of `ServeChatClient`: both sit on `AgentService.OnPrompt` and share
  `PromptStream.AskApprovalAsync`.
- Rejected: serving an `AIAgent` through `AsIChatClient()` and `ServeChatClient`. History would be kept twice and the
  `AgentSession` would be lost.
- Rejected: implementing `ServeChatClient` via `ChatClientAgent`. The core host package would then depend on Agent
  Framework.

## Demo

[`samples/MafNatsDemo`](../../samples/MafNatsDemo) composes a local MAF agent on Ollama (`qwen3.5:4b`, via OllamaSharp's
native `IChatClient`) with remote NATS agents. See its README for the commands.

[`samples/CopilotNatsAgent`](../../samples/CopilotNatsAgent) serves GitHub Copilot (`Microsoft.Agents.AI.GitHub.Copilot`)
as agent type `copilot`, with Copilot's permission requests asked of the NATS caller. The TypeScript counterpart is
`agents/copilot` (being consolidated onto the Copilot SDK separately).

## Caveats

- A paused query lives in memory (the open stream); serializing a session while a query is pending does not
  persist the pending query. `NatsAgentSession.CancelPendingQueryAsync()` abandons it.
- The remote agent decides how long it waits for an answer (the .NET service default is 5 minutes).
- Remote agents without per-session memory (e.g. the reference agents) ignore `session_id`.
