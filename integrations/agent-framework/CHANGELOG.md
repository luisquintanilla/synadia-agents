# Changelog

All notable changes to `Synadia.AI.Agents.AgentFramework` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Initial Microsoft Agent Framework adapter (`0.1.0-preview.1`, targeting net8.0 and net10.0, on Microsoft Agent
  Framework 1.28.0), built on `Synadia.AI.Agents` and `Synadia.AI.Agents.Service`.
  - **`NatsAIAgent`** (`agent.AsAIAgent()`) wraps a NATS agent as an `AIAgent`. It is a custom `AIAgent` over
    `Agent.AsIChatClient()`; `NatsAgentSession` carries the conversation id. Mid-stream queries pause the run and
    surface as input-request, function-call or tool-approval content (`NatsQueryMode`), or a `QueryHandler` answers
    them inline.
  - **`ServeAIAgent`** serves any `AIAgent` on NATS: the MAF twin of `ServeChatClient`. Tool approvals round-trip
    as §7 queries through `PromptStream.AskApprovalAsync`. `AIAgentServeOptions` shares `ConversationKey`,
    `IncludeReasoning`, `QueryTimeout`, `MaxInputRounds` and `Approval` with `ChatClientServeOptions`, and adds
    `RunOptions` and an `OnRun` hook.
  - **`AIAgentServeOptions.SessionStore`** (experimental,
    [`SYNAGENT001`](https://github.com/synadia-ai/synadia-agents/blob/main/docs/dotnet-diagnostics.md#synagent001)):
    an `AgentSessionStore` that loads the conversation's `AgentSession` before each run and saves it after, so
    conversations survive a restart. Without it, sessions stay in memory.
  - **`AddNatsAgentServer`** serves a keyed or factory-built `AIAgent` on NATS for the lifetime of a generic host.
  - **Packaging.** A consumer `PACKAGE.md` README, SourceLink, `.snupkg` symbols and deterministic CI builds. Public
    API baselines are enforced by PublicApiAnalyzers; RS0016/RS0017 fail the build.
  - Solution file `Synadia.AI.Agents.AgentFramework.slnx`, with the core SDK projects under `/deps/`.
  - **Samples.** `MafNatsDemo` (a MAF agent on Ollama that calls NATS agents as tools and serves an approval-gated
    agent) and `CopilotNatsAgent` (GitHub Copilot served as agent `copilot`, permission requests asked as queries).
