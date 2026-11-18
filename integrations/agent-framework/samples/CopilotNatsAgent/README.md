# CopilotNatsAgent

Serves **GitHub Copilot** on NATS as a Synadia Agent Protocol fleet agent, through Microsoft Agent Framework.

- `Microsoft.Agents.AI.GitHub.Copilot` 1.28.0 turns a `CopilotClient` into a MAF `AIAgent`. It brings
  `GitHub.Copilot.SDK` 1.0.14.
- `agentService.ServeAIAgent(agent)` (from [`Synadia.AI.Agents.AgentFramework`](../../src/Synadia.AI.Agents.AgentFramework))
  registers it as `agents.prompt.copilot.<owner>.<name>`.

## Prerequisites

- The .NET 10 SDK.
- A GitHub account with Copilot, signed in to the Copilot CLI (`copilot` → `/login`). The Copilot SDK uses that
  logged-in user; the sample reads no tokens.
- Optional: `nats-server` on `PATH`, used when `NATS_URL` is unset (an embedded server on a random port).

## Run

```powershell
cd integrations/agent-framework/samples/CopilotNatsAgent

# Terminal 1: serve. Without NATS_URL it starts an embedded nats-server and prints its URL.
dotnet run -- serve --owner demo --workdir C:\scratch\copilot

# Terminal 2: point callers at the printed URL.
$env:NATS_URL = "nats://127.0.0.1:<port>"
dotnet run -- call "Reply with just: pong" --owner demo                # .NET caller via AsAIAgent()
dotnet run -- call "Use your shell tool to run: echo hi" --owner demo  # answer the permission query with yes/no

# The TypeScript example prompts the first agent it discovers:
cd ../../../../client-sdk/typescript; bun install
bun examples/02-prompt-text.ts "In one short sentence: what is NATS?"
```

`serve` options:

| Flag | Default | Notes |
| --- | --- | --- |
| `--owner` | `demo` | Subject owner token. |
| `--name` | `copilot` | Subject name token and MAF agent name. |
| `--model` | `claude-haiku-4.5` | Any Copilot model id. |
| `--reasoning` | unset | Reasoning effort (`low`/`medium`/`high`). Leave it unset for models that reject it, such as `claude-haiku-4.5`. |
| `--workdir` | current directory | Copilot's working directory. Use a scratch directory. |
| `--instructions` | short system message | Appended to Copilot's system message. |

`call` options: `--owner` and `--name` filter discovery. `--auto` answers every query `no`.

## Permissions

Copilot asks before it runs shell commands, writes or reads files, fetches URLs, or calls MCP tools. The Copilot
provider does not surface these as MAF `ToolApprovalRequestContent`; they arrive on `SessionConfig.OnPermissionRequest`
instead. The sample bridges them to the protocol:

1. `AIAgentServeOptions.OnRun` tracks each prompt being served (`ServedRun`).
2. `OnPermissionRequest` finds the run whose Copilot session id matches and asks the caller a §7 query, for example
   ``Allow Copilot to run shell command `echo hi` (Echo hi)? (yes/no)``.
3. Only `y`, `yes`, `approve(d)`, `allow`, `ok` or `true` approves, and only once. Anything else, a timeout, or a
   request with no attached caller is rejected. Nothing is approved by default.

Copilot's `ask_user` questions (`OnUserInputRequest`) are relayed to the caller the same way.

That is why `copilotClient.AsAIAgent(instructions: ...)` is not used directly: that overload cannot set the model or
the permission handler. The sample passes a full `SessionConfig` (model, appended system message, working directory,
handlers) to `copilotClient.AsAIAgent(sessionConfig, ...)`.

## Notes

- Each caller conversation (the `session_id` extra that `NatsAIAgent` sends) gets its own Copilot session. Callers
  without one, such as the TS example, share a single conversation for the agent address.
- Copilot permission types are marked evaluation-only in the SDK (`GHCP001`, suppressed in the project file).
- Every prompt spends Copilot requests on the signed-in account. Keep test prompts small.
