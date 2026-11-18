# Changelog — Synadia.AI.Agents (.NET caller SDK)

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Changed

- Renamed `Agent.AsChatClient()` to `Agent.AsIChatClient()`, following the MEAI naming for conversions to an interface.

### Added

- Packaging: a consumer `PACKAGE.md` README per package (`PackageReadmeFile`), SourceLink, `.snupkg` symbols and
  deterministic CI builds. Public API baselines (`PublicAPI.Shipped.txt` empty, `PublicAPI.Unshipped.txt` full) are
  enforced by PublicApiAnalyzers; RS0016/RS0017 fail the build.
- `AgentQueryResponseContent` (an `InputResponseContent`) and `AgentQueryContent.CreateResponse(text, attachments)`,
  moved here from the Agent Framework package so hosts can answer relayed queries without depending on it.
- `AsIChatClient()` honors `ChatOptions.ConversationId`: a caller-set id is sent as the `session_id` envelope extra
  (`PromptEnvelope.SessionIdExtra`, read back with `PromptEnvelope.SessionId`) and echoed as the response
  `ConversationId`. A .NET convention, not a protocol change.
- Solution file `Synadia.AI.Agents.slnx` (XML `.slnx` format; needs .NET SDK 9.0.200 or later, and the repo pins 10.0.100). A bare
  `dotnet build` or `dotnet test` in this folder picks it up.
- Initial .NET caller SDK for wire protocol v0.3 (`0.1.0-preview.1`, targeting net8.0 and net10.0).
  - **Discovery and liveness.** Discovery uses `$SRV.PING/INFO.agents` (`AgentsClient.DiscoverAsync`,
    `EnumerateAsync`, `LookupAsync`, `PingAsync`). Liveness comes from `agents.hb.*.*.*` (`HeartbeatTracker`).
    Status is available through `Agent.GetStatusAsync`.
  - **Prompting.** `Agent.PromptAsync` returns `IAsyncEnumerable<AgentChunk>`, with inactivity and max-wait timeouts.
    Error frames become `AgentServiceException`. The reply inbox is subscribed in `Wait` full mode, so no chunk is
    dropped. `GetResponseAsync` and `GetResponseTextAsync` are aggregating helpers.
  - **Attachments.** The JSON envelope (`PromptEnvelope`) carries attachments as MEAI `DataContent`, with strict
    base64. The §5.4 pre-publish checks raise `AttachmentsNotSupportedException` and `PayloadTooLargeException`.
  - **Mid-stream queries (§7).** `AgentQueryChunk.ReplyAsync` answers them.
  - **Microsoft.Extensions.AI seams.** `Agent.AsIChatClient()` returns an `IChatClient`. Status arrives as
    `AgentStatusContent` and queries as `AgentQueryContent : InputRequestContent`.
  - **Sender identity (§13).** `Agent-Sender` is signed and verified with Ed25519 via NATS.NKeys and checked against
    `test-fixtures/identity` vectors. Callers sign with `AgentsClientOptions.Identity` (`SenderSigner.FromCredsFile`,
    `FromSeed`, …), read their own id with `AgentsClient.GetSelfIdAsync`, and verify an agent's `id_sig` with
    `AgentIdSignature.Verify`.
  - **Agent tools.** `AgentTools` ports the TS/Python agent-tools helper, and `AgentTools.AsAITools()` exposes its
    six tools as `AIFunction`s. Their schemas are byte-equal to `test-fixtures/agent-tools`. `AttachmentSaver` saves
    returned attachments. `discover_agents` reports `identity` and `identity_verified`.
  - Empty prompts are rejected locally with `PromptEmptyException`.
  - Numbered examples `01-discover` through `06-chat`, as .NET 10 file-based apps.
