# /// script
# requires-python = ">=3.11"
# dependencies = ["synadia-ai-agents>=0.7", "nats-py>=2.9"]
# ///
"""Send a document to the .NET document agent over NATS and print the markdown it streams back.

    uv run extract_pdf.py [file] [prompt]

Defaults: file = ../../fixtures/fabrikam-q3-report.pdf, prompt = "/extract".
NATS_URL selects the server (default nats://127.0.0.1:4222); the host prints it on start.
Credentialed URLs (nats://user:pass@host, as Aspire hands out) are accepted.
Uses the published synadia-ai-agents package from PyPI: Python caller -> NATS -> .NET host.
"""

import asyncio
import os
import sys
import time
from pathlib import Path
from urllib.parse import unquote, urlsplit

import nats
from synadia_ai.agents import (
    Agents,
    Attachment,
    DiscoverFilter,
    PayloadTooLargeError,
    ResponseChunk,
    StatusChunk,
)


async def main() -> int:
    default_file = Path(__file__).resolve().parent.parent.parent / "fixtures" / "fabrikam-q3-report.pdf"
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else default_file
    prompt = sys.argv[2] if len(sys.argv) > 2 else "/extract"
    url = urlsplit(os.environ.get("NATS_URL", "nats://127.0.0.1:4222"))
    # Split out nats://user:pass@host credentials (percent-decoded) and keep them out of log lines.
    servers = f"{url.scheme}://{url.hostname}:{url.port or 4222}"
    user = unquote(url.username) if url.username else None
    password = unquote(url.password) if url.password else None

    nc = await nats.connect(servers=servers, user=user, password=password, name="document-agent-py-caller")
    agents = Agents(nc=nc)
    try:
        found = await agents.discover(filter=DiscoverFilter(agent="docs"))
        if not found:
            print(f'no "docs" agent on {servers} - start the .NET host first', file=sys.stderr)
            return 2

        agent = found[0]
        print(
            f'-> {agent.agent}/{agent.owner}/{agent.session_name}: "{prompt}" + {path.name} '
            f"(formats={agent.metadata.get('document_formats')}, "
            f"max_payload={agent.prompt_endpoint.max_payload_bytes})",
            file=sys.stderr,
        )

        started = time.perf_counter()
        try:
            async for msg in agent.prompt(prompt, attachments=[Attachment.from_path(path)]):
                if isinstance(msg, ResponseChunk):
                    sys.stdout.write(msg.text)
                    sys.stdout.flush()
                elif isinstance(msg, StatusChunk) and msg.status not in ("ack", "done"):
                    print(f"[status] {msg.status}", file=sys.stderr)
        except PayloadTooLargeError as err:
            print(f"too large for one NATS message: {err}", file=sys.stderr)
            return 3

        print(f"\n[done in {(time.perf_counter() - started) * 1000:.0f} ms]", file=sys.stderr)
        return 0
    finally:
        await agents.close()
        await nc.close()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
