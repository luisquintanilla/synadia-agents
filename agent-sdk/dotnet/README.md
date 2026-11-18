# Synadia.AI.Agents.Service (.NET host SDK)

Host-side .NET SDK for the [Synadia Agent Protocol for NATS](https://github.com/synadia-ai/synadia-agent-sdk-docs)
(wire protocol **v0.3**). It registers an agent as a NATS micro service, streams responses, asks mid-stream queries
and publishes heartbeats. It can also **serve any Microsoft.Extensions.AI `IChatClient` as an agent** in one call.

| Package | What |
| --- | --- |
| `Synadia.AI.Agents.Service` | `AgentService`, `PromptContext`, `PromptStream`, `ServeChatClient` |
| `Synadia.AI.Agents.Testing` | Spec-compliant `ReferenceAgent` (the .NET counterpart of the TS/Python reference agents) and `NatsServerProcess` (a throwaway `nats-server` on a random free port) |

These packages depend on the caller SDK [`Synadia.AI.Agents`](../../client-sdk/dotnet/), which holds the shared
envelope, chunk and identity types.

> Status: preview (`0.1.0-preview.1`), fork-only — not published to NuGet.

## Quickstart: a hand-written agent

```csharp
using NATS.Client.Core;
using Synadia.AI.Agents.Service;

await using var nc = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" });
await using var service = new AgentService(nc, new AgentServiceOptions
{
    Agent = "echo", Owner = "me", Name = "demo", Description = "echoes prompts",
}).OnPrompt(async (ctx, stream, ct) =>
{
    await stream.WriteStatusAsync("thinking", ct);
    if (ctx.Prompt.Contains("delete"))
    {
        var answer = await stream.AskAsync("Really delete? (yes/no)", cancellationToken: ct);   // §7 query
        if (answer.Prompt != "yes") throw new RequestRejectedException(403, "not confirmed");     // §9 error frame
    }
    await stream.WriteAsync($"echo: {ctx.Prompt}", ctx.Attachments, ct);   // attachments are MEAI DataContent
});                                                                          // the SDK sends the terminator
await service.StartAsync();   // registers `agents` service + prompt/status endpoints, starts heartbeats
```

## Quickstart: serve an `IChatClient`

```csharp
using Microsoft.Extensions.AI;

IChatClient model = /* any MEAI chat client: OpenAI, Azure OpenAI, Ollama, Foundry, … */;
service.ServeChatClient(model.AsBuilder().UseFunctionInvocation().Build(), new ChatClientServeOptions
{
    Instructions = "You are a helpful assistant.",
    ChatOptions = new ChatOptions { Tools = [/* AIFunctions; ApprovalRequiredAIFunction → asked as a §7 query */] },
});
```

What `ServeChatClient` does with each update:

| Model output | Protocol |
| --- | --- |
| Text and `DataContent` | `response` chunks |
| `AgentStatusContent`, or a content-less update with `AdditionalProperties["synadia.status"]` | `status` chunks |
| Reasoning | `status` chunks, only with `IncludeReasoning` |
| Tool approval request | A query to the caller; the answer resumes the model |

With `AgentTools = new AgentTools(...)` set, the model can also discover and prompt other agents.

History is kept per conversation (`ConversationKey`, default one shared conversation) and is on by default. For agents
whose prompts carry large attachments (documents, images), set `KeepHistory = false`; otherwise every stored
attachment is re-sent to the model on each later prompt.

## Sender identity (§13)

```csharp
using Synadia.AI.Agents.Identity;

await using var secure = new AgentService(nc, new AgentServiceOptions
{
    Agent = "secure", Owner = "me", Name = "demo",
    MinSenderTrust = "signed",                                       // 401 for unsigned / invalid Agent-Sender
    AcceptSender = (sender, ct) => ValueTask.FromResult(true),       // optional allow-list hook (false → 403)
    Identity = new AgentServiceIdentityOptions { Signer = SenderSigner.FromCredsFile("agent.creds") },   // signs id_sig
}).OnPrompt(async (ctx, stream, ct) =>
{
    var who = ctx.Sender is VerifiedSender v ? v.Id.ToString() : "anonymous";
    await stream.WriteAsync($"hello {who}", cancellationToken: ct);
});
```

Every prompt's `Agent-Sender` header is verified (signature, subject binding, replay window, nonce). The result is
`PromptContext.Sender`. Empty prompts and malformed envelopes are rejected with a 400 error frame before your handler
runs.

## Examples ([`examples/`](examples/), .NET 10 file-based apps)

A host-side ladder mirroring the Python and TypeScript agent-sdk examples (same agent names, flags, env vars and demo
prompts), written MEAI-first: every model is an `IChatClient` served with one `ServeChatClient` call. See
[`examples/README.md`](examples/README.md) for configuration, expected output and the gaps vs. Python/TS.

| Script | Registers | What it does |
| --- | --- | --- |
| `01-echo.cs` | `echo` | Minimal `OnPrompt` agent — replies `echo: <prompt>`. |
| `02-ollama.cs` | `ollama` | A local Ollama model (OllamaSharp) served with `ServeChatClient`. |
| `03-openrouter.cs` | `openrouter` | The hosted OpenRouter API through `Microsoft.Extensions.AI.OpenAI` (`OPENAI_BASE_URL` overrides the endpoint). |
| `04-combined.cs` | `llm` | Ollama or OpenRouter, picked from the env by a function returning `IChatClient`. |
| `05-tools.cs` | `tools` | A `read_sensor` `AIFunction` backed by a `sensors` NATS microservice in the same file; `FunctionInvokingChatClient` runs the tool loop. |
| `06-approvals.cs` | `approvals` | A free tool plus an approval-gated tool (`ApprovalRequiredAIFunction` → mid-stream `query`). `--agent-tools` lets it delegate to other agents. |
| `run-reference-agent.cs` | `demo-agent` | Run the .NET `ReferenceAgent` (`ask …`, `fail …`, `slow …`, echo) for any caller to talk to. |

All of them share `--url`/`NATS_URL`, `--owner`/`--session-name` (the `SYNADIA_<AGENT>_OWNER` > `SYNADIA_OWNER` >
`NATS_AGENT_OWNER` ladder), `--heartbeat-interval`, `--nkey`/`--creds` and `--sender-identity signed` through
[`examples/_shared/ExampleHost.cs`](examples/_shared/ExampleHost.cs).

### Demo: approvals agent driven from the TypeScript (and .NET) callers

```sh
nats-server &                                                     # or any NATS server; NATS_URL overrides the URL
ollama pull qwen3.5:4b
dotnet run agent-sdk/dotnet/examples/06-approvals.cs              # agents.prompt.approvals.<you>.main

# TypeScript caller (build once: cd client-sdk/typescript && bun install && bun run build)
cd client-sdk/typescript
bun examples/01-discover.ts
bun examples/02-prompt-text.ts "what time is it?"                 # calls the current_time tool
bun examples/04-query-reply.ts "please restart the billing service"   # approval arrives as a query: answer yes/no
bun examples/06-chat.ts

# .NET caller
cd client-sdk/dotnet/examples && dotnet run 06-chat.cs
```

The TS examples prompt the *first* discovered agent, so run only this agent (or filter) during the demo.

The model client is [OllamaSharp](https://github.com/awaescher/OllamaSharp). Its `OllamaApiClient` is an `IChatClient`
over Ollama's native `/api/chat`, and it replaces the deprecated `Microsoft.Extensions.AI.Ollama` package. The
environment variables are:

- `OLLAMA_URL` / `OLLAMA_ENDPOINT` (default `http://localhost:11434`). A trailing `/v1` is ignored, so an
  OpenAI-compatible URL still works.
- `OLLAMA_MODEL` (default `qwen3.5:4b`).

qwen3.5 thinks by default, which is slow. The example sets `ChatOptions.Reasoning = new() { Effort = ReasoningEffort.None }`,
and OllamaSharp sends that to Ollama as `think: false`. Pass `--think` to keep thinking; the reasoning then reaches the
caller as `status` chunks.
## Build and test

```sh
cd agent-sdk/dotnet
dotnet test
```

This covers end-to-end tests against a real `nats-server` on a random port:

- discovery, streaming, errors, queries, attachments and heartbeats;
- MEAI seams;
- agent tools;
- sender identity;
- TS interop in both directions (needs `bun` and the built TS SDKs; otherwise skipped with a note).

See [`docs/protocol-mapping.md`](docs/protocol-mapping.md) and [`CHANGELOG.md`](CHANGELOG.md).
