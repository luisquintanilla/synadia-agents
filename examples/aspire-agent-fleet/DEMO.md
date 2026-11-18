# Demo script: Synadia agent fleet on .NET Aspire (~10 minutes)

## Before the call (once; first time about 15 min, mostly downloads)

```powershell
cd <repo>                                   # luisquintanilla/synadia-agents, branch luisquintanilla-aspire-apphost-agent-fleet
docker info > $null                         # Docker Desktop running (and docker on PATH)
./examples/aspire-agent-fleet/scripts/prepare.ps1   # builds TS SDKs + web UI, restores the doc agent, creates the Python venv, pre-pulls ollama/ollama
```

Then do one full `aspire run --isolated` and wait until `chat` and `embed` are Healthy. That pulls `qwen3.5:4b` and `embeddinggemma` (about 4 GB) into the Ollama data volume, so the live run starts in seconds. Send one prompt to `dotnet-agent` too, so the model has been loaded once.

No Docker GPU? Use the Ollama already installed on this machine instead: `ollama pull qwen3.5:4b; ollama pull embeddinggemma`, then add `-- --Ollama:Mode=host` to every `aspire run` below.

## 1. One command starts the fleet (1 min)

```powershell
cd examples/aspire-agent-fleet/AgentFleet.AppHost
aspire run --isolated          # --isolated = random ports; other nats-servers on this box won't clash
```

Ctrl+click the dashboard URL.

> "One `aspire run`: a NATS server with JetStream in a container, Ollama in a GPU container with its models as resources, the TypeScript and Python reference agents, two .NET agents built on the Synadia .NET SDK (a chat agent and a document-extraction agent), and the browser test client. Every process gets NATS injected and waits for it; the .NET agents also wait for their models."

Point at the dependency graph (Graph tab), then the `nats` → `agents` → agent rows tree. Then the `ollama` → `chat` / `embed` rows: the models are Aspire resources (Community Toolkit Ollama integration) with health, download progress and commands (**⋯ → Model info**). `dotnet-agent` gets `ConnectionStrings__chat` and builds its `IChatClient` from it.

## 2. The `agents` resource: protocol-aware dashboard (3 min)

1. Expand **agents**. It shows four child rows, `demo-agent-aspire-example`, `docs-aspire-main`, `dotnet-agent-aspire-ollama` and `py-demo-agent-aspire-python`, all **Alive**.
   - The **Source** column shows the agent key.
   - Click a row: the properties include the instance id, protocol `0.3`, harness version, prompt subject and last heartbeat.
   > "These rows aren't Aspire processes. They come from the protocol itself, built from heartbeats on `agents.hb.>`, three languages and one protocol."
2. **agents → ⋯ → Discover.** You get a markdown table from `$SRV.INFO.agents`: owner, name, session, protocol, prompt subject.
   > "The dashboard itself is a client of the .NET SDK: `AgentsClient.DiscoverAsync`, and prompts go through `Agent.AsIChatClient()`."
3. **agents → ⋯ → Prompt (smoke).** Type `Say hi to the team`.
   - All four agents stream back until the protocol terminator.
   - Each shows ✅ terminated, with chunk count and latency.
   - The TS and Python agents echo; the .NET agent gives a real answer from qwen. The doc agent says it has no documents yet, which sets up the next step.
4. On the **dotnet-agent-aspire-ollama** row, **⋯ → Prompt** prompts only that instance. Ask `In one sentence, what is NATS?`.
5. **agents → Console logs** shows the heartbeat log and discovery events.

## 2a. Document agent: a PDF over NATS (2 min)

1. On the **docs-aspire-main** row, open **⋯ → Prompt**.
   - Prompt: `/extract`
   - Attachment: `examples/dotnet-document-agent/fixtures/fabrikam-q3-report.pdf` (relative to the repo root)
   - The result is about 1.5 s: status `extracting … with PdfPig → page 1/2 → page 2/2`, then the report as **markdown**, with headings and the regional sales table.
   > "The attachment is an MEAI `DataContent` on the SDK's `AsIChatClient`. It crosses NATS as a protocol attachment and lands in the doc agent's `ServeChatClient`. Same SDK, both ends."
2. Same row, same attachment, prompt `Which region grew fastest in Q3, and what is the Q4 unit forecast?`.
   - The statuses show indexing (2 pages, 4 chunks), then searching, then answering.
   - In about 10 s: North at +15%, and 55,000 units, each with a source citation.
   > "That's RAG with embeddinggemma plus qwen, all local, behind one protocol subject."

## 2b. Traces: one prompt, end to end (2 min)

1. Open **Traces** and filter on `dotnet-agent`. Open the latest `agents.prompt receive` trace.
2. Walk the spans:
   - NATS consumer span `agents.prompt receive`;
   - its child `chat qwen3.5:4b`, a GenAI span with model, input/output tokens, time to first chunk and the actual messages;
   - `POST /api/chat` to the `chat` model (the span's destination is the Aspire resource `chat`);
   - a train of `inbox publish` spans, which are the streamed chunks going back over NATS.
   > "The SDK's `ServeChatClient` turns any `IChatClient` into a protocol agent. Because it's Microsoft.Extensions.AI, `UseOpenTelemetry()` gives us GenAI traces for free, stitched to the NATS spans."
3. Optionally, show **Metrics → dotnet-agent → `gen_ai.client.token.usage`**.

## 3. Same thing from the CLI and MCP (1 min)

In a second terminal, from `examples/aspire-agent-fleet/AgentFleet.AppHost`:

```powershell
aspire describe
aspire resource agents discover --non-interactive
aspire resource agents prompt --prompt "Say hi to the team" --non-interactive
aspire resource agents prompt --prompt "What is NATS?" --agent dotnet-agent/aspire/ollama --non-interactive
aspire resource agents prompt --prompt "/extract" --agent docs --attachment examples/dotnet-document-agent/fixtures/fabrikam-q3-report.pdf --non-interactive
```

> "These commands are also exposed through the Aspire MCP server, so a coding agent can discover and smoke-test the fleet."

## 4. Liveness: an agent Aspire didn't start (2 min)

```powershell
aspire resource agents connect --non-interactive     # prints the $env:NATS_URL = '...' line; paste it
cd ../../../client-sdk/typescript
$env:USER = "guest"; bun run examples/_run-reference-agent.ts
```

- In the dashboard, `demo-agent-guest-example` appears under **agents** within about 5 s, and the `agents` row reports 5 alive.
- Press Ctrl+C on the agent. After about 15 s (3 missed heartbeats) the row turns **Stale** ("last heartbeat Ns ago").

> "Anything that speaks the protocol on this NATS shows up. Aspire is a fleet observer, not just a process launcher."

You can also stop and start **py-agent** from the dashboard: it goes Stale, then Alive again with a new instance id.

## 5. Clients (1 min)

- Open the **web-ui** URL from the dashboard, pick an agent and chat.
- Run the TS SDK example in the terminal that has `NATS_URL` set:

  ```powershell
  bun run examples/01-discover.ts      # found 4 agent(s) ... including docs/aspire/main and dotnet-agent/aspire/ollama
  bun run examples/02-prompt-text.ts
  ```

## 6. Where this goes (1 min)

- **Code.** The `Synadia.AI.Agents.Hosting.Aspire` package (in this repo only, not published) provides `AddSynadiaAgents`, `WithNatsUrl`, `AddTypeScriptAgent`, `AddPythonAgent` and `AddDotNetAgent`.
  - Discovery, status and prompts go through the .NET SDK caller, with raw NATS.Net as a fallback behind the same seam.
  - 27 unit tests.
- **The .NET agent** is about 100 lines: `AddNatsClient("nats")` + `AddOllamaApiClient("chat").AddChatClient()` + `AgentService.ServeChatClient`. Any `IChatClient` (Azure OpenAI, Foundry, a custom pipeline) becomes a protocol agent.
- **The document agent** is the same pattern: `DocumentAgentChatClient` is an `IChatClient`, so `ServeChatClient` makes it a protocol agent. It joins the AppHost with one `AddDotNetAgent<Projects.DocumentExtraction_Host>` line (disable it with `--Fleet:DocumentAgent=false`).
- **NATS.Net finding.** `Aspire.NATS.Net` 13.6.5 depends on NATS.Net ≥ 2.8.2 but works with NATS.Net 3.3.2 (verified live: the .NET agent uses exactly this combination).

## Teardown

Press Ctrl+C in the `aspire run` terminal, or run `aspire stop` if you started it with `--detach`. The NATS and Ollama containers and all agents stop with it. The Ollama data volume (models) stays for next time.

## If something goes wrong

| Symptom | Fix |
| --- | --- |
| `agents` stays Unhealthy | The Python agent takes about 12 s on first start. Wait, then check the `py-agent` console logs |
| py-agent fails with "No module named synadia_ai" | Rerun `scripts/prepare.ps1` (it creates `agent-sdk/python/.venv`) |
| ts-agent or web-ui fails on imports | Rerun `prepare.ps1` (it builds the `dist/` folders of both TS SDKs) |
| Port clash | Use `aspire run --isolated` |
| `nats` is **RuntimeUnhealthy** | Docker isn't running or `docker` isn't on PATH (see the `nats` console log) |
| `chat` / `embed` show "Downloading N percent" | First run on an empty volume; wait (a few minutes), or use `-- --Ollama:Mode=host` with pre-pulled models |
| `ollama` is slow (CPU) or fails to start with a GPU error | Docker has no NVIDIA GPU access: use `-- --Ollama:Mode=host`, or `-- --Ollama:Gpu=false` |
| Host mode: `chat` / `embed` Unhealthy, `dotnet-agent` / `doc-agent` stuck Waiting | Start Ollama (`ollama serve`) and pull the model named in the health message, or run with `-- --Fleet:DotNetAgent=false --Fleet:DocumentAgent=false` |
| `ts-agent` / `py-agent` show Finished (exit code -1) | Something outside Aspire killed the process; **⋯ → Start** on the row |
| AppHost build fails restoring `CommunityToolkit.DocumentExtraction.*` | Rerun `prepare.ps1`; it restores the doc agent from nuget.org |
| Attachment prompt says "file not found" | Relative paths resolve from the repo root; use the full path otherwise |
| First .NET answer is slow (~10–15 s) | That's the cold model load; send one prompt beforehand (see above) |
