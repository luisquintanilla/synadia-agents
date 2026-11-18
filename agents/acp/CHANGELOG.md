# Changelog

All notable changes to `@synadia-ai/acp-nats-channel` will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Optional signed host identity and independent signed-only prompt admission.
  Both reuse the one SDK connection bundle; existing identity-free operation
  remains the default.
- `copilot` preset: GitHub Copilot CLI via `copilot --acp` (public preview),
  with ephemeral `COPILOT_HOME` isolation by default and `SYNADIA_COPILOT_*`
  per-agent env vars. An inherited `COPILOT_ALLOW_ALL` is stripped from the
  agent environment so it cannot bypass the channel's permission policy.
- Model / reasoning-effort pass-through (`--model`, `--reasoning-effort`;
  `acp.model`, `acp.reasoning_effort`; `SYNADIA_<AGENT>_MODEL`,
  `SYNADIA_<AGENT>_REASONING_EFFORT`). Each value is appended via the preset's
  own CLI flag and advertised as `acp_model` / `acp_reasoning_effort` service
  metadata. Presets without such a flag reject the setting.

### Fixed

- The default owner falls back to `USERNAME` when `USER` is unset (Windows),
  instead of `unknown`.
- The "agent-side prompt errors propagate" managed-runtime test no longer
  times out under Bun 1.4. The runtime always rejected promptly; Bun 1.4's
  `expect(...).toThrow` hung on the async-generator rejection.

## [0.1.0] - 2026-07-18

### Added

- Initial generic ACP (Agent Client Protocol) Synadia Agent Protocol adapter package:
  one channel for every ACP-speaking coding agent, driven over `session/prompt` +
  `session/update` via the official `@agentclientprotocol/sdk`.
- Presets: `grok` (Grok Build via `grok agent stdio`, isolated `GROK_HOME` by
  default) plus a `custom` escape hatch for any other ACP-speaking agent or
  adapter (e.g. Google Antigravity via a community ACP adapter until `agy`
  ships native ACP). A `gemini` preset existed briefly during development and
  was removed before release — Gemini CLI was superseded by Antigravity.
- Managed mode (adapter-owned agent subprocess, one long-lived ACP session) and fake
  mode for deterministic protocol smoke tests.
- Permission mapping: ACP `session/request_permission` -> protocol §7 query chunks
  (`query` policy), with `reject` (default) and `allow` policies.
- Protocol and fake-runtime validation harnesses (no real agent binary required).
- README setup, presets, configuration, permissions, and auth documentation.
