# Synadia Agents

**SDKs and ready-to-run agent plugins for the [Synadia Agent Protocol for NATS](https://github.com/synadia-ai/synadia-agent-sdk-docs).**

The Synadia Agent Protocol for NATS lets any AI agent — Codex, OpenCode, Claude Code, OpenClaw, PI, Hermes, Flue, or your own — register itself as a NATS micro service named `agents`, and be discovered, prompted, and streamed from by any caller speaking the same wire format. This repo is the home of the official **caller** and **host** SDKs (TypeScript and Python, plus a preview .NET SDK — see [SDKs](#sdks) below), plus pre-built channel plugins that put popular AI harnesses on NATS without writing code.

## Get started — pick your path

| You want to… | Go to | Install |
| --- | --- | --- |
| **Put an existing AI agent on NATS** (Codex, OpenCode, Claude Code, OpenClaw, PI, Hermes, DeerFlow, Flue, DSPy ReAct) | [`agents/`](agents/) — pick the agent | per-agent README |
| **Build a caller** that discovers and prompts agents | [`client-sdk/typescript/`](client-sdk/typescript/) · [`client-sdk/python/`](client-sdk/python/) · [`client-sdk/dotnet/`](client-sdk/dotnet/) | `npm i @synadia-ai/agents` · `pip install synadia-ai-agents` · .NET: project reference (preview, not on NuGet) |
| **Host a brand-new agent** built from scratch | [`agent-sdk/typescript/`](agent-sdk/typescript/) · [`agent-sdk/python/`](agent-sdk/python/) · [`agent-sdk/dotnet/`](agent-sdk/dotnet/) | `npm i @synadia-ai/agent-service` · `pip install synadia-ai-agent-service` · .NET: project reference (preview, not on NuGet) |
| **See full end-to-end demos** — browser UI, session controllers, from-scratch agents | [`examples/`](examples/) | per-example README |

## Agents

Pre-built channel plugins that put existing AI harnesses on NATS. Each registers as an `agents` micro service and serves the protocol's `prompt`, `status`, and `hb` endpoints out of the box.

| Agent | Token | Package |
| --- | --- | --- |
| [Claude Code](agents/claude-code/) | `cc` | `claude-channel-nats` |
| [OpenClaw](agents/openclaw/) | `oc` | `@synadia-ai/nats-channel` |
| [PI Agent](agents/pi/) | `pi` | `@synadia-ai/nats-pi-channel` |
| [Hermes](agents/hermes/) | `hermes` | upstream fork — see [`agents/hermes/`](agents/hermes/) (work in progress) |
| [DeerFlow](agents/deerflow/) | `df` | `synadia-ai-nats-deerflow-channel` — external Python wrapper for a running DeerFlow Gateway |
| [Flue](agents/flue/) | `flue` | `@synadia-ai/flue-nats-channel` — sidecar for a running Flue app / agent |
| [Eve](agents/eve/) | `eve` | `@synadia-ai/eve-nats-channel` — sidecar for a running [Vercel Eve](https://github.com/vercel/eve) agent |
| [open-agent](agents/open-agent/) | `open-agent` | `@synadia-ai/open-agent` (private) — inbound bridge for [`vercel-labs/open-agents`](https://github.com/vercel-labs/open-agents); LocalSandbox + companion [`examples/open-agent-vercel/`](examples/open-agent-vercel/) |
| [OpenCode](agents/opencode/) | `opencode` | `@synadia-ai/opencode-nats-channel` — OpenCode plugin that registers projects as NATS agents |
| [Codex](agents/codex/) | `codex` | `@synadia-ai/codex-nats-channel` — Codex app-server-backed channel for managed or attached sessions |
| [ACP](agents/acp/) | `grok`, `copilot`, … | `@synadia-ai/acp-nats-channel` — generic channel for [ACP](https://agentclientprotocol.com)-speaking agents (Grok Build and GitHub Copilot CLI presets + custom for adapters, e.g. Antigravity) |
| [Grok Build](agents/grok/) | `grok` | `@synadia-ai/grok-nats-channel` — grok-pinned front door to the ACP channel (`grok-agent start`) |
| [GitHub Copilot](agents/copilot/) | `copilot` | `@synadia-ai/copilot-nats-channel` (private) — headless Copilot fleet agent on the Copilot SDK (`serve`) + in-session Copilot CLI/App extension (outbound fleet tools, opt-in inbound); generic ACP route: `agents/acp` `copilot` preset |
| [DSPy ReAct](examples/dspy/) | `dspy` | standalone example (not published) — built from scratch with ax-llm ReAct |

Subjects follow a verb-first pattern: `agents.{verb}.{token}.{owner}.{session}` where `verb` is `prompt`, `hb`, or `status`.

Two from-scratch agents built on the host SDK — DSPy **ReAct** (`dspy`) and DSPy **deep-research** (`research`) — live under [Examples](#examples) rather than here, since they're built from scratch rather than wrapping an existing harness.

## SDKs

Two halves per language. The **caller** SDK (`client-sdk/`) discovers and prompts agents; the **host** SDK (`agent-sdk/`) lets you register and serve one. Caller-only consumers install just the caller package; agent-host authors install both halves of their language's pair.

| Side | Folder | TypeScript | Python | .NET (preview) |
| --- | --- | --- | --- | --- |
| **Caller** | [`client-sdk/`](client-sdk/) | [`@synadia-ai/agents`](client-sdk/typescript/) | [`synadia-ai-agents`](client-sdk/python/) | [`Synadia.AI.Agents`](client-sdk/dotnet/) |
| **Host** | [`agent-sdk/`](agent-sdk/) | [`@synadia-ai/agent-service`](agent-sdk/typescript/) | [`synadia-ai-agent-service`](agent-sdk/python/) | [`Synadia.AI.Agents.Service`](agent-sdk/dotnet/) |

All languages stay in lockstep on the wire format, validated by cross-SDK interop tests: [`tests/test_interop_e2e.py`](client-sdk/python/tests/test_interop_e2e.py) runs the TS reference agent against the Python client, and the .NET host test suite runs the .NET caller against the TS reference agent and the TS caller against the .NET reference agent. The .NET SDK also plugs agents into [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai): `Agent.AsIChatClient()` on the caller side, `ServeChatClient(IChatClient)` on the host side.

The active [SDK release rollout roadmap](docs/sdk-release-rollout-roadmap.md) tracks sender
identity, the extension hooks, compatibility, integration adoption, dependency controls, and release
promotion. Tracing is not part of this release: the SDKs carry generic extension hooks (prompt and
request interceptors, extra envelope fields, heartbeat extras), and extensions such as tracing build
on them in separate packages.

**Microsoft Agent Framework (.NET):** [`Synadia.AI.Agents.AgentFramework`](integrations/agent-framework/) (unpublished) wraps any NATS agent as a MAF `AIAgent` (`agent.AsAIAgent()`) and serves any MAF `AIAgent` on NATS (`agentService.ServeAIAgent(aiAgent)`).

## Examples

End-to-end apps built on the SDKs — browser clients, controllers that spawn ephemeral agents, and agents built from scratch. Full write-ups in [`examples/README.md`](examples/README.md).

| Example | Kind | What it shows |
| --- | --- | --- |
| [`agent-web-ui/`](examples/agent-web-ui/) | caller | Vue 3 browser client — discovery, prompting with attachments, streaming, and inline mid-stream `query` allow/deny. Doubles as a **control plane**: when a headless controller is discovered it spawns sessions and fans a single prompt across N working directories in parallel. |
| [`claude-code-headless/`](examples/claude-code-headless/) | agent | One process spawns and manages **headless Claude Code sessions** — each registers as its own first-class agent at `agents.prompt.cc-headless.<owner>.<session>`, alongside a `spawn`/`stop`/`list` controller. Per-token streaming, tool cards, §7 permission queries, per-turn cost tracking. |
| [`pi-headless/`](examples/pi-headless/) | agent | The same **dynamic-session** pattern for the PI coding agent — many sessions, each its own agent at `agents.prompt.pi-headless.<owner>.<session>`, plus a `spawn`/`stop`/`list` controller. Pairs naturally with `agent-web-ui/`. |
| [`dspy/`](examples/dspy/) | agent | From-scratch DSPy-style **ReAct** agent on `AgentService` with four sandboxed filesystem tools. Registers as token `dspy`. |
| [`dspy-research-agent/`](examples/dspy-research-agent/) | agent | From-scratch DSPy-style **deep-research** agent on ax-llm's RLM — a sandboxed JS REPL with recursive `llmQuery()` sub-calls and pluggable web search (Tavily/Exa + neural `findSimilar`). Registers as token `research`. |
| [`open-agent-vercel/`](examples/open-agent-vercel/) | agent | Runs the [`agents/open-agent/`](agents/open-agent/) bridge against `@vercel/sandbox` instead of its built-in LocalSandbox — same `runBridge`, same wire behaviour, only the sandbox factory changes. |
| [`MafNatsDemo/`](integrations/agent-framework/samples/MafNatsDemo/) | both | .NET **Microsoft Agent Framework** agent on Ollama (`qwen3.5:4b`) that calls NATS agents as tools, pauses and resumes on remote questions, and serves a MAF agent whose tool approvals travel over NATS as queries. |
| [`CopilotNatsAgent/`](integrations/agent-framework/samples/CopilotNatsAgent/) | agent | .NET: **GitHub Copilot** served as agent token `copilot` through Microsoft Agent Framework; Copilot's shell/write/read permission requests become §7 queries to the caller (deny by default). TS counterpart: [`agents/copilot`](agents/copilot/). |
| [`dotnet-document-agent/`](examples/dotnet-document-agent/) | agent | .NET 10 **document extraction agent** served with the in-repo .NET SDK (`ServeChatClient`). Turns PDF/image/text attachments into markdown (headings + GFM tables) or indexes them for RAG answers, using `Microsoft.Extensions.AI`/DocumentExtraction/DataIngestion/VectorData and local Ollama models. Registers as token `docs`; TS and Python callers included. Not published. |
| [`aspire-agent-fleet/`](examples/aspire-agent-fleet/) | fleet | .NET **Aspire** AppHost: one `aspire run` starts NATS (JetStream), Ollama with its models, the TS and Python reference agents, a .NET chat agent, the document agent and the browser client, plus a dashboard resource that discovers, monitors and prompts every agent on the bus. Not published. |

**Aspire.** [`integrations/aspire/`](integrations/aspire/) is an (unpublished) Aspire hosting library: an `AddSynadiaAgents` dashboard resource that discovers, monitors and prompts every agent on a NATS server, plus helpers that start TS / Python / .NET agents wired to it. [`examples/aspire-agent-fleet/`](examples/aspire-agent-fleet/) uses it to run the whole fleet with one `aspire run`.

## Wire protocol at a glance

```
  caller (SDK)  ──▶  NATS  ──▶  agent host
       ▲                           │
       └─── streamed chunks ───────┘
```

A request is plain UTF-8 text or a JSON envelope `{"prompt": "...", "attachments": [{"filename": "...", "content": "<base64>"}]}`. The agent streams typed JSON chunks on the reply subject — `{"type":"response","data":"..."}` for content, `{"type":"status","data":"ack"}` as the mandatory §6.4 leading chunk (and optionally again as periodic keep-alive), `{"type":"query","data":{...}}` for mid-stream questions — and ends with an **empty-body, no-headers** terminator. Errors use the `Nats-Service-Error-Code` header (`400` client, `500` server).

Discovery is standard NATS micro:

```bash
nats req '$SRV.INFO.agents' '' --replies=0 --timeout=2s
nats sub 'agents.hb.*.*.*'
```

To prompt an agent directly from the CLI (no SDK), pass three flags — `--replies=0 --reply-timeout=30s --timeout=60s`. The full CLI cookbook (prompts, attachments, status, control-plane, gotchas) lives at [`docs/using-nats-cli.md`](docs/using-nats-cli.md).

Full spec: <https://github.com/synadia-ai/synadia-agent-sdk-docs>.

## Quickstart

Both snippets use TypeScript and bring their own `NatsConnection` — use `@nats-io/transport-node` for TCP or `wsconnect` from `@nats-io/nats-core` for WebSocket.

### Caller side — discover and prompt an agent

Uses the **caller SDK** ([`client-sdk/typescript/`](client-sdk/typescript/) → `@synadia-ai/agents`).

```ts
import { connect } from "@nats-io/transport-node";
import { Agents } from "@synadia-ai/agents";

const nc = await connect({ servers: "nats://localhost:4222" });
const agents = new Agents({ nc });

const [agent] = await agents.discover();

for await (const msg of await agent!.prompt("hello")) {
  if (msg.type === "response") process.stdout.write(msg.text);
}

await agents.close();
await nc.close();
```

### Host side — serve an agent on NATS

Uses the **host SDK** ([`agent-sdk/typescript/`](agent-sdk/typescript/) → `@synadia-ai/agent-service`). Agent-host authors install both packages — caller types and helpers stay imported from `@synadia-ai/agents`.

```ts
import { connect } from "@nats-io/transport-node";
import { AgentService } from "@synadia-ai/agent-service";

const nc = await connect({ servers: "nats://localhost:4222" });

const service = new AgentService({
  nc,
  agent: "echo", // metadata.agent — canonical harness identifier
  owner: "demo", // metadata.owner — operator / account namespace
  name: "main", // 5th subject token — instance name
  description: "Echo agent demo",
});

service.onPrompt(async (envelope, response) => {
  await response.send(`echo: ${envelope.prompt}`);
});

await service.start();
console.log(`listening on ${service.subject.prompt}`);

// on shutdown:
await service.stop();
await nc.close();
```

**Try it now:** [`agent-sdk/typescript/examples/01-echo.ts`](agent-sdk/typescript/examples/01-echo.ts) is this code packaged as a runnable script — `bun agent-sdk/typescript/examples/01-echo.ts` (with `$NATS_CONTEXT`, `$NATS_URL`, or localhost fallback). The TS, Python and .NET SDKs ship a parallel **agent ladder** (`01-echo` → `05-tools`: echo, Ollama, OpenRouter, combined, tool-calling) — TS in [`agent-sdk/typescript/examples/`](agent-sdk/typescript/examples/), Python in [`agent-sdk/python/examples/`](agent-sdk/python/examples/), .NET in [`agent-sdk/dotnet/examples/`](agent-sdk/dotnet/examples/) (plus `06-approvals`: an approval-gated tool as a mid-stream query).

For full install, error handling, and longer examples see the per-package READMEs: caller — [`client-sdk/typescript/`](client-sdk/typescript/) · [`client-sdk/python/`](client-sdk/python/) · [`client-sdk/dotnet/`](client-sdk/dotnet/); host — [`agent-sdk/typescript/`](agent-sdk/typescript/) · [`agent-sdk/python/`](agent-sdk/python/) · [`agent-sdk/dotnet/`](agent-sdk/dotnet/). C# quickstarts are in [`client-sdk/README.md`](client-sdk/README.md) and [`agent-sdk/README.md`](agent-sdk/README.md).

## For protocol implementers

Every SDK ships a **spec-compliant reference agent** that implements the full §12 agent checklist (service registration, prompt endpoint, status endpoint, heartbeats, terminator semantics). When reasoning about wire shape, testing a third SDK, or validating AI/LLM-generated tooling, read these first — they are the authoritative on-the-wire counterpart to the spec.

| SDK | Reference agent | Demo scripts |
| --- | --- | --- |
| TypeScript | [`ReferenceAgent`](agent-sdk/typescript/src/testing/reference-agent.ts) — importable as `@synadia-ai/agent-service/testing`. Runnable: [`_run-reference-agent.ts`](client-sdk/typescript/examples/_run-reference-agent.ts). | [`client-sdk/typescript/examples/`](client-sdk/typescript/examples/) — `01-discover.ts` … `05-liveness.ts`, plus `06-chat.ts` (interactive REPL). |
| Python | [`_reference_agent.py`](agent-sdk/python/examples/_reference_agent.py) — runnable echo agent with conversation memory. | [`client-sdk/python/examples/`](client-sdk/python/examples/) — `01-discover.py` … `05-liveness.py`, plus `06-chat.py` (interactive REPL). |
| .NET (preview) | [`ReferenceAgent`](agent-sdk/dotnet/src/Synadia.AI.Agents.Testing/ReferenceAgent.cs) in `Synadia.AI.Agents.Testing`. Runnable: [`run-reference-agent.cs`](agent-sdk/dotnet/examples/run-reference-agent.cs). | [`client-sdk/dotnet/examples/`](client-sdk/dotnet/examples/) — `01-discover.cs` … `05-liveness.cs`, plus `06-chat.cs` (interactive REPL via `IChatClient`). |

<details>
<summary><strong>Repository layout</strong></summary>

```
synadia-agents/
├── README.md              ← you are here
├── README-DEV.md          ← local-development build / install recipes
├── client-sdk/            ← caller-side language SDKs (discover · prompt · stream)
│   ├── typescript/        ← @synadia-ai/agents
│   ├── python/            ← synadia-ai-agents
│   └── dotnet/            ← Synadia.AI.Agents (preview)
├── agent-sdk/             ← host-side language SDKs (host an agent)
│   ├── typescript/        ← @synadia-ai/agent-service
│   ├── python/            ← synadia-ai-agent-service
│   └── dotnet/            ← Synadia.AI.Agents.Service + .Testing (preview)
├── agents/                ← plugins that put existing AI harnesses on NATS
│   ├── claude-code/
│   ├── openclaw/
│   ├── pi/
│   ├── hermes/
│   ├── deerflow/
│   ├── flue/
│   ├── eve/                     ← sidecar for Vercel Eve agents
│   ├── open-agent/              ← inbound bridge for vercel-labs/open-agents
│   ├── opencode/                ← OpenCode plugin channel
│   ├── acp/                     ← generic ACP channel (grok + copilot presets + custom)
│   ├── copilot/                 ← GitHub Copilot: headless agent on the Copilot SDK + CLI/App extension
│   └── codex/                   ← Codex app-server-backed channel
├── integrations/
│   └── aspire/                  ← Aspire hosting library: AddSynadiaAgents dashboard resource + agent helpers (not published)
└── examples/              ← apps built with the SDKs (callers and agents)
    ├── agent-web-ui/             ← Vue 3 + Bun browser client
    ├── claude-code-headless/     ← spawn/stop many Claude Code sessions
    ├── pi-headless/              ← spawn/stop many PI sessions
    ├── dspy/                     ← from-scratch agent (ax-llm ReAct, token `dspy`)
    ├── dspy-research-agent/      ← from-scratch deep-research agent (ax-llm RLM + web search, token `research`)
    ├── open-agent-vercel/        ← runs the open-agent bridge against @vercel/sandbox
    ├── dotnet-document-agent/    ← .NET document-extraction agent (markdown + RAG, .NET SDK)
    └── aspire-agent-fleet/       ← Aspire AppHost: NATS + Ollama + TS/Python/.NET agents + web UI in one `aspire run`
```

Each subtree has its own `README.md` and the index READMEs (`client-sdk/README.md`, `agent-sdk/README.md`, `agents/README.md`, `examples/README.md`) describe what lives at each level. See [`README-DEV.md`](README-DEV.md) for local-dev build and install recipes.

</details>

## License

Apache-2.0 across this monorepo — see each package's `LICENSE` file.
