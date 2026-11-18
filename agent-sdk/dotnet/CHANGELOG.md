# Changelog — Synadia.AI.Agents.Service / Synadia.AI.Agents.Testing (.NET host SDK)

All notable changes to these packages are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added

- Initial .NET host SDK for wire protocol v0.3 (`0.1.0-preview.1`, targeting net8.0 and net10.0).
  - **`AgentService`.**
    - Registration: the `agents` micro service with spec metadata, and a prompt endpoint (queue group `agents`)
      advertising `max_payload`, `attachments_ok` and `min_sender_trust`.
    - Endpoints and liveness: a status endpoint, heartbeats, and keep-alive acks.
    - Errors: error frames via `RequestRejectedException`.
    - Shutdown: a graceful drain.
  - **`PromptStream`** streams `response` and `status` chunks, asks mid-stream queries with `AskAsync`, and writes the
    terminator for you.
  - **Microsoft.Extensions.AI seams.**
    - `PromptContext.ToChatMessage()`.
    - `PromptStream.WriteAsync(ChatResponseUpdate)`, which maps `AgentStatusContent` and
      `AdditionalProperties["synadia.status"]` to status.
    - `AgentService.ServeChatClient(IChatClient)`, which hosts any chat client: tool approvals round-trip as §7
      queries, and the optional `AgentTools` lets the model delegate to other agents.
  - **Sender identity (§13).** Inbound `Agent-Sender` is verified and classified into `PromptContext.Sender`, and
    `min_sender_trust` is enforced (400/401/403/500 error frames), with `ReplayWindow`, an `AcceptSender` hook,
    `OperatorAttested` and `AccountTokenPosition`. `AgentServiceOptions.Identity` signs the agent's `id_sig` metadata.
  - Empty prompts and malformed envelopes are answered with a 400 error frame before the handler runs.
  - **`Synadia.AI.Agents.Testing`.**
    - `ReferenceAgent`, which supports `ask`, `fail`, `slow` and echo-with-history.
    - `NatsServerProcess`, a `nats-server` on a random free port.
  - **Tests.** End-to-end tests and TS interop in both directions (.NET caller ↔ TS reference agent, TS caller ↔ .NET
    reference agent, including queries and attachments).
  - **Examples.** `run-reference-agent.cs`, and `ollama-agent.cs` (`ServeChatClient` over a local Ollama model through
    OllamaSharp's `IChatClient`).
