# One-time setup for the aspire-agent-fleet example (Windows / PowerShell 7+).
# Builds the TypeScript SDKs (the agents and web UI consume them via file: links, which bun
# copies rather than symlinks — so order matters), builds the web UI bundle, and creates the
# Python virtualenv the Python reference agent runs from.
#
#   pwsh examples/aspire-agent-fleet/scripts/prepare.ps1
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

function Step($title, $dir, [scriptblock]$body) {
    Write-Host "==> $title ($dir)" -ForegroundColor Cyan
    Push-Location (Join-Path $repo $dir)
    try {
        & $body
        if ($LASTEXITCODE) { throw "$title failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}

foreach ($tool in 'bun', 'docker', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' is required but was not found on PATH." }
}

# client-sdk <-> agent-sdk reference each other via file: links; build caller first, then host,
# then reinstall the caller so it picks up the freshly built host package.
Step 'client SDK: install + build' 'client-sdk/typescript' { bun install; bun run build }
Step 'agent SDK: install + build'  'agent-sdk/typescript'  { bun install; bun run build }
Step 'client SDK: relink'          'client-sdk/typescript' { bun install }
Step 'web UI: install + build'     'examples/agent-web-ui' { bun install; bun run build }

# Restore the document-extraction agent from its own directory so its isolated settings apply
# and its packages land in the global cache the AppHost graph restores from.
Step 'doc agent: restore' 'examples/dotnet-document-agent' {
    dotnet restore src/DocumentExtraction.Host/DocumentExtraction.Host.csproj
}

Step 'Python venv' 'agent-sdk/python' {
    if (Get-Command uv -ErrorAction SilentlyContinue) {
        uv sync --no-dev
        if ($LASTEXITCODE -eq 0) { return }
        Write-Warning 'uv sync failed; falling back to python -m venv + pip.'
    }
    $py = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command py -ErrorAction Stop)
    & $py.Source -m venv .venv
    & .\.venv\Scripts\python.exe -m pip install --quiet -e ..\..\client-sdk\python -e .
}

# Optional pre-warm: the AppHost's Ollama container image is ~5.5 GB. Keep the tag in sync with
# CommunityToolkit.Aspire.Hosting.Ollama (Directory.Build.props: CommunityToolkitAspireVersion).
if (Get-Command docker -ErrorAction SilentlyContinue) {
    Write-Host "`n==> Ollama image (pre-pull)" -ForegroundColor Cyan
    docker pull ollama/ollama:0.34.4
    if ($LASTEXITCODE -ne 0) { Write-Warning 'docker pull failed; Aspire will pull the image on first run.' }
}

Write-Host "`nReady. Next:`n  cd examples/aspire-agent-fleet/AgentFleet.AppHost`n  aspire run" -ForegroundColor Green
