# Findings: .NET document extraction on the Synadia Agent Protocol for NATS

Spike results from building this example. All observations come from the code in this folder, run on
Windows with the .NET 10 SDK and a local Ollama (`qwen3.5:4b`, `embeddinggemma`).

## TL;DR

- **It works end to end, locally, with no cloud keys.** A born-digital PDF becomes markdown with headings and
  real GFM tables in about 2 s. An invoice image becomes a correct markdown table through a 4B vision
  model in about 5–15 s. A RAG answer over three mixed files comes back correct, with sources, in about 3 s after indexing.
- **No `ExtractionDocument` → MEDI `IngestionDocument` bridge exists** in either package family today.
  We wrote one: [`ExtractionToIngestion`](src/DocumentExtraction.Core/ExtractionToIngestion.cs), about 130 lines,
  plus an `IngestionDocumentReader` wrapper. This is the obvious thing for upstream to own.
- **Out-of-the-box PdfPig output is weak for markdown and RAG.** The default segmenter returns one block per page,
  with no headings and no tables. A small layout layer (Docstrum segmentation, a heading role resolver and
  column-to-table reconstruction) fixes it for simple born-digital PDFs.
- **`max_payload` is the real constraint.** Base64 inside a 1 MiB message caps raw attachments at about 762 KB. We
  reject oversize files gracefully and propose a JetStream Object Store attachment reference below.
- **It runs over NATS and works across languages.** The host is about 40 lines on the in-repo .NET SDK
  (`ServeChatClient` over `DocumentAgentChatClient`). The **published** TypeScript (`@synadia-ai/agents` 0.5.2)
  and Python (`synadia-ai-agents` from PyPI) SDKs and the .NET `AsIChatClient` caller all send a PDF and get the
  same markdown back, with page progress as `status` chunks (§8).

## 1. Package availability (as of this spike)

| Package | Version | Source |
| --- | --- | --- |
| `Microsoft.Extensions.DocumentExtraction(.Abstractions)`, `Microsoft.Extensions.Documents.Abstractions` | `11.0.0-preview.1.26560.3` | NuGet.org (preview) |
| `Microsoft.Extensions.DataIngestion` (MEDI) | `11.0.0-preview.1.26560.3` | NuGet.org (preview) |
| `Microsoft.Extensions.VectorData.Abstractions` (MEVD) | `11.0.0` | NuGet.org |
| `CommunityToolkit.VectorData.InMemory` | `1.0.1` | NuGet.org |
| `CommunityToolkit.DocumentExtraction.PdfPig` / `.VisionLanguageModel` | `1.0.0-preview` | NuGet.org (preview) |
| `Microsoft.Extensions.AI` | `11.0.0` | NuGet.org |

- Experimental diagnostics: the extraction and ingestion APIs are marked experimental (`MEDE0001`, `CTDE0001`,
  `EXTEXP0019`). They are suppressed project-wide in `Directory.Build.props`.
- The Azure providers (Document Intelligence and Content Understanding) exist but were not exercised.
  This spike deliberately needs no secrets.

## 2. Extraction → MEDI: there is no bridge, so we wrote an adapter

DocumentExtraction produces `Microsoft.Extensions.Documents.ExtractionDocument`: sections, then elements
(text with a role, tables with an addressable cell grid, images), each with page and polygon sources. MEDI
consumes `Microsoft.Extensions.DataIngestion.IngestionDocument`: sections, then headers, paragraphs, tables, images and footers.
They are parallel models and neither references the other.

`ExtractionToIngestion.Convert` maps them as follows:

| Extraction element | Ingestion element |
| --- | --- |
| section (one per page for PdfPig and the VLM) | `IngestionDocumentSection` with `PageNumber` |
| text, role `heading` | `IngestionDocumentHeader` (`Level` from `HeadingLevel`, default 2) |
| text, role `pageFooter` | `IngestionDocumentFooter` |
| text, role `pageHeader` or `pageNumber` | dropped |
| other text | `IngestionDocumentParagraph` |
| table | `IngestionDocumentTable` (GFM markdown plus a converted cell grid) |
| image with alt text | `IngestionDocumentImage` (bytes and media type carried over) |

`ExtractionDocumentReader : IngestionDocumentReader` wraps any `DocumentExtractionClient` (or the router),
so the rest of MEDI (chunkers, enrichers and writers) works unchanged.

**Proposal for upstream:** ship this mapping in MEDI, for example `ExtractionDocumentReader` in
`Microsoft.Extensions.DataIngestion`, so that `IngestionDocumentReader` providers come for free from every
DocumentExtraction provider.

Other MEDI notes:

- `IngestionPipeline` only accepts `FileInfo`/`DirectoryInfo` inputs. For bytes arriving over the wire we compose
  the stages directly: reader → `HeaderChunker` → `VectorStoreWriter`. A `Stream` or `DataContent` overload would make
  agent scenarios first-class.
- `VectorStoreWriter.Dispose()` disposes the **vector store** it was given, not just its collection. The
  knowledge base therefore owns the store and the writer together. The writer's collection only exists after the
  first write, and its dimensions come from probing the embedding generator once.
- `HeaderChunker` with an o200k tokenizer, 400 tokens and 40 overlap gives sensible chunks once the document has real
  headings (4 chunks for the 2-page fixture PDF). With the default PdfPig segmenter it produced 1 chunk per document.

## 3. PdfPig quality, and the layout layer

- **Out of the box:** `PdfPigDocumentExtractionClient()` uses PdfPig's `DefaultPageSegmenter`, which produces one text
  block for the whole page. There are no roles and no tables, and every table flattens into lines of words.
- **What we tried:**
  - `RecursiveXYCut` got reading order wrong: the "Outlook" heading ended up out of place.
  - `DocstrumBoundingBoxes` gave correct paragraphs and reading order.
  - Both segmenters split a column-aligned table into **one block per column**.
- **[`PdfLayoutExtractionClient`](src/DocumentExtraction.Core/PdfLayoutExtractionClient.cs)** is a
  `DelegatingDocumentExtractionClient`. It:
  1. configures the provider with Docstrum and a role resolver: a single-line block that is ≥ 13 pt or all bold is a `heading`;
  2. rebuilds tables. A run of ≥ 2 adjacent body blocks with the same line count (≥ 2), short lines,
     left-to-right order and matching top and bottom becomes an `ExtractionDocumentTableElement` (row 0 is `columnHeader`).

  Result on the fixture: correct `##` headings, the "Regional sales" table as a 5-row × 4-column GFM table, and correct
  order across pages. **These are heuristics.** They suit simple born-digital PDFs; complex layouts (merged
  cells, multi-line cells, multi-column articles) need Azure Document Intelligence or a VLM.
- **No OCR.** A scanned PDF has no text layer, and the agent says so ("send the pages as images").

## 4. Vision language model (VLM) provider over Ollama

- `VisionLanguageModelDocumentExtractionClient` works with any `IChatClient`. With `qwen3.5:4b` on Ollama, the fixture
  invoice (720×520 PNG) returns the correct item table, totals and terms in about 5–15 s.
- **Ollama cannot take PDFs as VLM input**, so the router sends PDFs to PdfPig and images to the VLM. The
  `usePdfVision` switch exists for models and hosts that do accept PDFs.
- **qwen3.5 "thinks" by default.** Disabling it through OllamaSharp's
  `RawRepresentationFactory = _ => new ChatRequest { Think = false }` cut latency several-fold with no quality
  loss for extraction or lookup. The harness's `--think` flag re-enables it.
- The VLM usually wraps its whole answer in a <code>```markdown</code> fence. `ExtractionMarkdown.Unfence`
  removes a fence that wraps the entire text (only when unlabelled or labelled `markdown`/`md`).
- When the answer model was told to "name the source file", it invented `file:///…` links. The system prompt now asks for
  plain file names and says never to invent URLs; the fixed output is clean.

## 5. Demo results (`dotnet run --project src/DocumentExtraction.Harness -- demo`)

| Step | Result | Time |
| --- | --- | --- |
| PDF → markdown (PdfPig + layout) | 2 pages, 4 headings, 5×4 table; status `page 1/2`, `page 2/2` | ~2 s (first call includes JIT) |
| PNG invoice → markdown (VLM) | correct table; total $950.40 | ~5–15 s |
| 1.2 MB attachment | `⚠️ scan-1200k.pdf is 1.14 MB, over the 762 KB attachment limit …` (no exception; other files still processed) | 0 s |
| Index PDF + PNG + MD, then ask "North revenue and express shipping?" | "$1.20M … 1 business day (fabrikam-q3-report.pdf)" | ~7 s indexing (VLM-bound) + ~3 s answer |
| Follow-up with no attachments: "INV-1042 total and Widget Pro warranty?" | "$950.40 … 3-year limited warranty", citing the PNG and the MD | ~2–3 s |

Tests: `dotnet test --solution DocumentAgent.slnx` runs **34 tests, all passing**:

- renderer, bridge, layout and table reconstruction, unfence, media types and payload math;
- mode resolution, rejection paths, streaming statuses;
- RAG with a deterministic hashing embedder and a scripted chat client that exercises the tool loop and checks reasoning is not leaked;
- the `IChatClient` adapter;
- 2 live Ollama tests, which skip when Ollama is unavailable.

## 6. NATS `max_payload` and large documents

**Today.** The prompt envelope is JSON: `{"prompt": "...", "attachments": [{"filename": "...", "content": "<base64>"}]}`.

- The NATS default `max_payload` is **1 MiB**, and base64 adds 33 %. Leaving 8 KiB for the envelope, prompt and headers,
  the largest raw attachment set is `(1 MiB − 8 KiB) / 4 × 3 ≈ 762 KB`
  ([`PayloadLimits`](src/DocumentExtraction.Core/PayloadLimits.cs)).
- Callers are expected to reject oversize envelopes before publishing (the caller SDKs raise `PayloadTooLargeError`).
  The agent still enforces its own `MaxAttachmentBytes`, defaulting to that budget. An oversize file gets a readable
  `⚠️` line, not a failed request, and the remaining attachments are still processed.
- Replies are less of a problem: responses stream as many small chunks. A host should still split any single
  response delta that approaches `max_payload`. A one-page markdown table is well under 1 KB.

Raising `max_payload` (the server allows up to 64 MB) is a deployment knob, not a design. Real documents (scans,
slide decks, 50-page PDFs) routinely exceed it.

### Proposal: Object Store attachment references (future spec extension)

Let an attachment carry a **reference** instead of inline bytes:

```json
{
  "prompt": "Summarize the contract",
  "attachments": [
    { "filename": "contract.pdf",
      "object_store": { "bucket": "agent-attachments", "name": "7f3c…/contract.pdf",
                        "digest": "SHA-256=K2X…", "size": 18874368 } }
  ]
}
```

- **Caller:** `os.put()` into a JetStream Object Store bucket (chunked, so there is no payload limit), then send
  the reference. The bucket is configured with a TTL (`max_age`) so abandoned uploads expire.
- **Agent:** advertises support in its service metadata (for example `"attachments_ref": "object_store"`) next to
  `max_payload` and `attachments_ok`, so callers can choose inline or by-reference per agent. On receipt it streams
  `os.get()`, verifies `digest` and `size`, and treats the result exactly like an inline attachment. In .NET that is
  `DataContent` or a `Stream`, so nothing above the seam changes.
- **Security:** NATS permissions scope which buckets each agent can read. The digest prevents swaps, and the
  agent never trusts `size` without verifying it. Optionally, the caller deletes the object after the terminator.
- **Compatibility:** this is purely additive. Agents that don't advertise it keep getting inline base64, and inline
  stays the default for small files. Results can use the same mechanism in reverse (an agent puts a large
  artifact and returns a reference in a `response` chunk).
- **Open questions for the spec:** bucket naming and ownership (per caller, per agent, or shared), whether the
  reference should allow a `link` to another account, and whether digest verification is mandatory.

## 7. Seam and protocol mapping

| Agent output | Protocol chunk |
| --- | --- |
| update with text | `{"type":"response","data":"<delta>"}` |
| `DocumentAgentStatus` (content-less update, `AdditionalProperties["synadia.status"]`) | `{"type":"status","data":"<message>"}` (e.g. `"report.pdf: page 2/5"`) |
| end of stream | empty terminator (the SDK handles this) |

- The spec's mandatory leading `status` chunk (`"ack"`) is the host SDK's job. Our status messages are optional
  progress or keep-alive chunks, as other agents in this repo already emit (`dspy-research-agent` streams subtopic
  progress as `status`).
- **Answered by the .NET SDK:** `PromptStream.WriteAsync(ChatResponseUpdate)` maps
  `AdditionalProperties["synadia.status"]` (and `AgentStatusContent`) to `status` chunks, so `ServeChatClient`
  needs no adapter. On the caller side, `AsIChatClient` turns them back into `AgentStatusContent`.
- Use `ChatClientServeOptions.KeepHistory = false` for this kind of agent. The default history would resend
  every earlier attachment's bytes on each turn. This agent keeps its knowledge in the vector index instead,
  and `DocumentAgentChatClient` reads only the newest user message.
- The protocol rejects an empty prompt (§5.1) on the client side, so "files only" cannot be expressed. Callers
  send `/extract` (or "convert to markdown"). This is a small UX gap for "just process these files" agents;
  one option is a spec note that recommends a conventional verb.
- Attachments: the wire envelope carries only `filename` and base64, with no media type, so the agent resolves the
  type from the extension (`MediaTypes.Resolve`) whenever the SDK hands over `application/octet-stream`.

## 8. End-to-end evidence over NATS

The host ran with `dotnet run --project src/DocumentExtraction.Host -- --embedded-nats` (nats-server 2.x, 1 MiB
`max_payload`, Ollama `qwen3.5:4b` + `embeddinggemma`). Timings are wall clock as the caller measured them. The
transcripts are trimmed, the status strings show their format, and `[status]` lines are protocol `status` chunks, printed to stderr.

**TypeScript, published `@synadia-ai/agents` 0.5.2: PDF → markdown (1.4 s)**

```text
-> docs/<owner>/main: "/extract" + fixtures/fabrikam-q3-report.pdf (formats=pdf,image,text, modes=extract,ask, max_payload=1048576)
[status] extracting fabrikam-q3-report.pdf (2.5 KB) with PdfPig
[status] fabrikam-q3-report.pdf: page 1/2
[status] fabrikam-q3-report.pdf: page 2/2
# fabrikam-q3-report.pdf
<!-- page 1 -->
## Fabrikam Widgets - Q3 2025 Report
...
| Region | Units | Revenue | Growth |
| --- | --- | --- | --- |
| North | 18,400 | $1.20M | +15% |
...
<!-- page 2 -->
## Shipping and returns policy
...
```

**TypeScript: PNG invoice → vision model → markdown table (16 s)**: `Total due: **$950.40**`, with all three line
items correct.

**TypeScript: RAG question with the PDF attached (3.0 s)**

```text
[status] indexing fabrikam-q3-report.pdf with PdfPig
[status] indexed fabrikam-q3-report.pdf: 2 page(s), 4 chunk(s)
[status] searching documents
[status] answering
Express shipping takes **1 business day** (source [2]), and the North region's Q3 revenue was **$1.20M** (source [1]).
_Sources searched: fabrikam-q3-report.pdf_
```

**Python, published `synadia-ai-agents` from PyPI (`uv run extract_pdf.py`): PDF → the same markdown (0.7 s)**. A RAG
question over `support-notes.md` returned "Widget Pro carries a 3-year limited warranty". When asked about express
shipping, which was not in that file, it correctly said the documents do not cover it.

**.NET `AsIChatClient`.** `tests/DocumentExtraction.Host.Tests` covers this path in three tests against a real
nats-server:
- extraction, with page statuses received as `AgentStatusContent`;
- `/ingest` followed by a question with no attachment, which shows the index survives across prompts and that the
  answer model never sees the earlier PDF bytes;
- an oversize attachment refused with `PayloadTooLargeException` before it is sent.

One fix came out of this run. The 4B model sometimes wrapped file-name citations in invented links, e.g.
`[report.pdf](file://localhost/...)`. The answer stream is now line-buffered, and any link that isn't http(s) is
reduced to its label (`DocumentAgent.ScrubCitationLinks`).

Environment note: npm and files.pythonhosted.org fail TLS on the dev box. `bun install` works for npm packages, and
for Python a pip proxy index works (set `UV_INDEX_URL` for the run). Nothing about this is committed.

Credentialed servers: the host (NATS.Net) accepts `nats://user:pass@host` as is. nats.js rejects credentials in the
server URL, and nats-py did not authenticate with a percent-encoded password. Both callers therefore split the user and password out
of `NATS_URL` before connecting. This was checked against a nats-server started with `--user/--pass`.
