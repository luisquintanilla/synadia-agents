# MafNatsDemo — Microsoft Agent Framework ⇄ NATS agents

A local [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) agent running on Ollama
(`qwen3.5:4b`) composes with remote agents over the Synadia Agent Protocol for NATS, using
[`Synadia.AI.Agents.AgentFramework`](../../src/Synadia.AI.Agents.AgentFramework).

Prerequisites: .NET 10 SDK, `nats-server` on `PATH` (for the embedded server), and
[Ollama](https://ollama.com) with `ollama pull qwen3.5:4b`. No API keys are needed.

## All-in-one

```sh
cd integrations/agent-framework/samples/MafNatsDemo
dotnet run            # interactive: you answer the remote agents' questions
dotnet run -- --auto  # unattended: answers "teal" / "yes" automatically
```

This starts an embedded `nats-server` on a random port, plus two remote agents: the .NET reference `echo` agent and
`concierge`, a MAF `ChatClientAgent` on Ollama served with `ServeAIAgent`. It then runs three scenes:

1. **NATS agent as a MAF tool.** A local `ChatClientAgent` on Ollama calls the remote `echo` agent through
   `remote.AsAIAgent().AsAIFunction()`.
2. **Remote question → MAF input request.** `ask What is your favorite color?` makes the remote agent ask
   mid-stream; the MAF run pauses with an `AgentQueryContent`, and the next `RunAsync` answers it and resumes.
3. **Tool approval across NATS.** The served concierge's `book_table` is an `ApprovalRequiredAIFunction`; its MAF
   approval request travels to the caller as a protocol query (`Allow tool 'book_table' …? (yes/no)`).

Sample output (`--auto`, ~10 s on a laptop):

```text
== discovered 2 agents over NATS:
   - echo (demo-agent) — Echoes text back; 'ask <question>' asks the caller first.
   - concierge (maf) — Books restaurant tables (Microsoft Agent Framework on Ollama); bookings need approval.

== 1) local MAF agent on Ollama calls the remote 'echo' NATS agent as a tool
   user> Call the echo tool with the text 'hello from Microsoft Agent Framework' and tell me exactly what it returned.
   orchestrator> The echo tool returned "hello from Microsoft Agent Framework", prefixed with 'echo: '.

== 2) remote question surfaces as a MAF input request; the run pauses and resumes on the next RunAsync
   remote asks: What is your favorite color?
   you> teal (auto)
   echo> you answered: teal

== 3) served MAF agent (Ollama) needs approval for a tool; the approval travels over NATS as a query
   user> Book a table for 2 at Luigi's at 7pm.
   remote asks: Allow tool 'book_table' with arguments {"restaurant":"Luigi's","time":"7pm","people":2}? (yes/no)
   you> yes (auto)
   concierge> Your table for two people at Luigi's on 7pm has been booked with reference number NATS-1546.
```

## Across processes and languages

Start a NATS server on a non-default port, then point every process at it with `NATS_URL`:

```sh
nats-server -a 127.0.0.1 -p 14222
export NATS_URL=nats://127.0.0.1:14222      # PowerShell: $env:NATS_URL = "nats://127.0.0.1:14222"

# terminal 1: serve the Ollama-backed MAF concierge on NATS
dotnet run -- serve

# terminal 2: a local MAF orchestrator uses every discovered agent as a tool
dotnet run -- call "Book a table for 2 at Luigi's at 7pm using the concierge."
```

The TypeScript reference agent works the same way:

```sh
cd client-sdk/typescript && bun install && NATS_URL=nats://127.0.0.1:14222 bun run examples/_run-reference-agent.ts
# back in this directory:
dotnet run -- call "Send 'hello from .NET MAF' to the example agent and tell me exactly what it replied." --owner anon
#   orchestrator> The example agent replied: "demo agent received your prompt."
```

`call` uses a `QueryHandler`, so remote questions are asked on the console (or answered `yes` with `--auto`).

## Configuration

| Variable | Default | |
| --- | --- | --- |
| `NATS_URL` | *(embedded server on a random port)* | NATS server to use |
| `OLLAMA_URL` | `http://localhost:11434` | Ollama server (native API; a trailing `/v1` is ignored) |
| `OLLAMA_MODEL` | `qwen3.5:4b` | Model (must support tool calling) |
| `OLLAMA_THINK` | `false` | `true`, `low`, `medium` or `high` enables thinking; off by default because thinking models can deliberate for minutes |

The model is reached through [OllamaSharp](https://github.com/awaescher/OllamaSharp)'s `OllamaApiClient`, a native
`Microsoft.Extensions.AI` `IChatClient` (the older `Microsoft.Extensions.AI.Ollama` package is deprecated in its
favour). OllamaSharp maps `ChatOptions.Reasoning` to Ollama's `think` field, so the demo sets
`Reasoning.Effort = ReasoningEffort.None` (`think: false`) unless `OLLAMA_THINK` opts in.

Flags: `--auto` (no console input; implied when stdin is redirected), `--owner <owner>` (owner registered by `serve`,
default `demo`; discovery filter for `call`).
