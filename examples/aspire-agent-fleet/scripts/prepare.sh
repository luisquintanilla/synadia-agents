#!/usr/bin/env bash
# One-time setup for the aspire-agent-fleet example (macOS / Linux).
# Builds the TypeScript SDKs (the agents and web UI consume them via file: links, which bun
# copies rather than symlinks — so order matters), builds the web UI bundle, and creates the
# Python virtualenv the Python reference agent runs from.
#
#   ./examples/aspire-agent-fleet/scripts/prepare.sh
set -euo pipefail
repo="$(cd "$(dirname "$0")/../../.." && pwd)"

for tool in bun docker dotnet; do
  command -v "$tool" >/dev/null || { echo "'$tool' is required but was not found on PATH." >&2; exit 1; }
done

step() { echo "==> $1 ($2)"; }

step 'client SDK: install + build' client-sdk/typescript
(cd "$repo/client-sdk/typescript" && bun install && bun run build)
step 'agent SDK: install + build' agent-sdk/typescript
(cd "$repo/agent-sdk/typescript" && bun install && bun run build)
step 'client SDK: relink' client-sdk/typescript
(cd "$repo/client-sdk/typescript" && bun install)
step 'web UI: install + build' examples/agent-web-ui
(cd "$repo/examples/agent-web-ui" && bun install && bun run build)

# Restore the document-extraction agent from its own directory so its isolated settings apply
# and its packages land in the global cache the AppHost graph restores from.
step 'doc agent: restore' examples/dotnet-document-agent
(cd "$repo/examples/dotnet-document-agent" && dotnet restore src/DocumentExtraction.Host/DocumentExtraction.Host.csproj)

step 'Python venv' agent-sdk/python
cd "$repo/agent-sdk/python"
if command -v uv >/dev/null && uv sync --no-dev; then
  :
else
  echo "uv unavailable or failed; falling back to python3 -m venv + pip." >&2
  python3 -m venv .venv
  .venv/bin/python -m pip install --quiet -e ../../client-sdk/python -e .
fi

# Optional pre-warm: the AppHost's Ollama container image is ~5.5 GB. Keep the tag in sync with
# CommunityToolkit.Aspire.Hosting.Ollama (Directory.Build.props: CommunityToolkitAspireVersion).
if command -v docker >/dev/null; then
  printf '\n==> Ollama image (pre-pull)\n'
  docker pull ollama/ollama:0.34.4 || echo "docker pull failed; Aspire will pull the image on first run." >&2
fi

printf '\nReady. Next:\n  cd examples/aspire-agent-fleet/AgentFleet.AppHost\n  aspire run\n'
