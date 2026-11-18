// Sends a document to the .NET document agent over NATS and prints the markdown it streams back.
//
//   bun install
//   bun run extract-pdf.ts [file] [prompt]
//
// Defaults: file = ../../fixtures/fabrikam-q3-report.pdf, prompt = "/extract".
// NATS_URL selects the server (default nats://127.0.0.1:4222); the host prints it on start.
// Credentialed URLs (nats://user:pass@host) are accepted.
// Uses the published @synadia-ai/agents package from npm, so this is a cross-language,
// cross-release interop check: TS caller -> NATS -> .NET host (ServeChatClient).

import { connect } from "@nats-io/transport-node";
import { Agents, PayloadTooLargeError } from "@synadia-ai/agents";
import { fileURLToPath } from "node:url";

const file = process.argv[2] ?? fileURLToPath(new URL("../../fixtures/fabrikam-q3-report.pdf", import.meta.url));
const prompt = process.argv[3] ?? "/extract";
const natsUrl = new URL(process.env.NATS_URL ?? "nats://127.0.0.1:4222");
// nats.js rejects credentials embedded in the server URL (e.g. nats://user:pass@host), so split them out.
const user = natsUrl.username ? decodeURIComponent(natsUrl.username) : undefined;
const pass = natsUrl.password ? decodeURIComponent(natsUrl.password) : undefined;
const servers = `${natsUrl.hostname}:${natsUrl.port || "4222"}`;

const nc = await connect({ servers, user, pass, name: "document-agent-ts-caller" });
const agents = new Agents({ nc });
try {
  const [agent] = await agents.discover({ filter: { agent: "docs" } });
  if (!agent) {
    console.error(`no "docs" agent on ${servers} - start the .NET host first`);
    process.exit(2);
  }

  console.error(
    `-> ${agent.agent}/${agent.owner}/${agent.name}: "${prompt}" + ${file} ` +
      `(formats=${agent.metadata.document_formats}, modes=${agent.metadata.document_modes}, ` +
      `max_payload=${agent.promptEndpoint.maxPayloadBytes})`,
  );

  const started = performance.now();
  const stream = await agent.prompt(prompt, { attachments: [file] });
  for await (const msg of stream) {
    if (msg.type === "response") process.stdout.write(msg.text);
    else if (msg.type === "status" && msg.status !== "ack" && msg.status !== "done") console.error(`[status] ${msg.status}`);
  }
  console.error(`\n[done in ${Math.round(performance.now() - started)} ms]`);
} catch (err) {
  if (err instanceof PayloadTooLargeError) {
    console.error(`too large for one NATS message: ${err.message}`);
    process.exitCode = 3;
  } else {
    throw err;
  }
} finally {
  await agents.close();
  await nc.close();
}
