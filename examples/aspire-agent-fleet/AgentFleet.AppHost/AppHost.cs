// Synadia Agent Protocol for NATS — agent fleet orchestrated by Aspire.
//
//   aspire run            (or: dotnet run)  from this directory
//
// Starts NATS (container, JetStream, random host port), Ollama with its models as
// resources (CommunityToolkit.Aspire.Hosting.Ollama; Ollama:Mode=host reuses the local
// Ollama), the TypeScript and Python reference agents, the .NET chat and document agents
// (Synadia .NET SDK ServeChatClient), and the browser test client, all wired to NATS.
// The "agents" resource (nested under nats) shows every agent instance that is
// heartbeating on NATS — including ones Aspire did not launch — and offers
// Discover / Status / Prompt (smoke) commands in the dashboard, CLI, and MCP.
//
// Prerequisites: run ../scripts/prepare.ps1 (or prepare.sh) once to build the
// TypeScript SDKs + web UI and create the Python virtualenv.

using Microsoft.Extensions.Configuration;
using Synadia.AI.Agents.Hosting.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
string Repo(params string[] parts) => Path.Combine([repoRoot, .. parts]);

// Dependencies are installed by scripts/prepare.*; set Fleet:InstallPackages=true to let
// Aspire run `bun install` / `uv sync` itself on every start instead.
var installPackages = builder.Configuration.GetValue("Fleet:InstallPackages", false);

var nats = builder.AddNats("nats")
    .WithJetStream();

// TypeScript reference agent (agent-sdk ReferenceAgent): registers demo-agent/<USER>/example.
var tsAgent = builder.AddTypeScriptAgent("ts-agent", Repo("client-sdk", "typescript"), "examples/_run-reference-agent.ts", nats, installPackages)
    .WithEnvironment("USER", "aspire");

// Python reference agent (synadia-ai-agent-service): registers py-demo-agent/aspire/python.
var pyAgent = builder.AddPythonAgent("py-agent", Repo("agent-sdk", "python"), "examples/_reference_agent.py", nats, installPackages)
    .WithArgs("--agent", "py-demo-agent", "--owner", "aspire", "--session-name", "python");

// Browser test client: discover + chat with any agent on this NATS server.
builder.AddBunApp("web-ui", Repo("examples", "agent-web-ui"), "server/index.ts")
    .WithBun(install: installPackages)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .WithNatsUrl(nats)
    .WaitFor(tsAgent)
    .WaitFor(pyAgent);

var expectedAgents = 2;
var dotNetAgentEnabled = builder.Configuration.GetValue("Fleet:DotNetAgent", true);
var documentAgentEnabled = builder.Configuration.GetValue("Fleet:DocumentAgent", true);

// Ollama for the .NET agents, as Aspire resources: "ollama" (server) with "chat" and "embed" model children.
// Both modes expose the same contract — model connection strings "Endpoint=<url>;Model=<model>" — so
// consumers just .WithReference(chat).WaitFor(chat):
//   Ollama:Mode=host (default)  the Ollama already running on this machine (Ollama:Url, default
//                               http://localhost:11434): native GPU, models pulled once with `ollama pull`.
//                               "chat"/"embed" turn healthy only once their model is pulled there.
//   Ollama:Mode=container       CommunityToolkit.Aspire.Hosting.Ollama: ollama/ollama container with a data
//                               volume (models survive restarts), NVIDIA GPU (Ollama:Gpu=false to run on CPU),
//                               models pulled on first start with progress in the dashboard.
IResourceBuilder<IResourceWithConnectionString>? chat = null;
IResourceBuilder<IResourceWithConnectionString>? embed = null;
ReferenceExpression? ollamaUri = null;
var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "qwen3.5:4b";
var embedModel = builder.Configuration["Ollama:EmbedModel"] ?? "embeddinggemma";
if (dotNetAgentEnabled || documentAgentEnabled)
{
    var ollamaMode = builder.Configuration["Ollama:Mode"] ?? "container";
    if (string.Equals(ollamaMode, "container", StringComparison.OrdinalIgnoreCase))
    {
        var server = builder.AddOllama("ollama").WithDataVolume();
        if (builder.Configuration.GetValue("Ollama:Gpu", true))
        {
            server.WithGPUSupport();
        }

        ollamaUri = server.Resource.UriExpression;
        chat = server.AddModel("chat", chatModel);
        embed = documentAgentEnabled ? server.AddModel("embed", embedModel) : null;
    }
    else if (string.Equals(ollamaMode, "host", StringComparison.OrdinalIgnoreCase))
    {
        var url = (builder.Configuration["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/');
        var server = builder.AddHostOllama("ollama", url);

        ollamaUri = ReferenceExpression.Create($"{url}");
        chat = server.AddHostModel("chat", url, chatModel);
        embed = documentAgentEnabled ? server.AddHostModel("embed", url, embedModel) : null;
    }
    else
    {
        throw new InvalidOperationException($"Ollama:Mode must be 'host' or 'container', not '{ollamaMode}'.");
    }
}

// .NET agent: AgentService.ServeChatClient(IChatClient) where the IChatClient comes from the "chat" model
// connection string (CommunityToolkit.Aspire.OllamaSharp AddOllamaApiClient). Disable with Fleet:DotNetAgent=false.
if (dotNetAgentEnabled)
{
    builder.AddDotNetAgent<Projects.AgentFleet_DotNetAgent>("dotnet-agent", nats)
        .WithReference(chat!)
        .WithEnvironment("GENAI_CAPTURE_CONTENT", "true")
        .WaitFor(chat!);
    expectedAgents++;
}

// .NET document-extraction agent (examples/dotnet-document-agent, DocumentExtraction.Host):
// ServeChatClient(DocumentAgentChatClient) — PDF/image/text attachments → markdown, plus questions over
// an in-memory index (chat + embed models). Registers docs/aspire/main. Disable with
// Fleet:DocumentAgent=false; Fleet:DocumentAgentProject=<csproj> runs a different host project instead.
// The host is used unchanged: its --chat-model/--embed-model/OLLAMA_URL contract is fed from the Ollama resources.
if (documentAgentEnabled)
{
    IResourceBuilder<ProjectResource> docAgent;
    if (builder.Configuration["Fleet:DocumentAgentProject"] is { Length: > 0 } docAgentProject)
    {
        var docAgentPath = Path.IsPathRooted(docAgentProject) ? docAgentProject : Repo(docAgentProject);
        if (!File.Exists(docAgentPath))
        {
            throw new FileNotFoundException($"Fleet:DocumentAgentProject '{docAgentPath}' does not exist.", docAgentPath);
        }

        docAgent = builder.AddDotNetAgent("doc-agent", docAgentPath, nats);
    }
    else
    {
        docAgent = builder.AddDotNetAgent<Projects.DocumentExtraction_Host>("doc-agent", nats);
    }

    docAgent
        .WithArgs("--owner", "aspire", "--name", "main",
            "--chat-model", chatModel,
            "--embed-model", embedModel)
        .WithEnvironment("OLLAMA_URL", ollamaUri!)
        .WaitFor(chat!)
        .WaitFor(embed!);
    expectedAgents++;
}

// Protocol-level view of the fleet: healthy once every agent above heartbeats.
// Discovery / status / prompt go through the Synadia .NET SDK (AgentsClient, Agent.AsIChatClient);
// set Fleet:RawNatsClient=true to use the SDK-independent NATS.Net implementation instead.
nats.AddSynadiaAgents("agents", o =>
{
    o.MinimumAgents = expectedAgents;
    // Prompt command attachments like examples/dotnet-document-agent/fixtures/fabrikam-q3-report.pdf resolve from the repo root.
    o.AttachmentBaseDirectory = repoRoot;
    if (builder.Configuration.GetValue("Fleet:RawNatsClient", false))
    {
        o.Client = AgentFleetClientKind.RawNats;
    }
});

builder.Build().Run();
