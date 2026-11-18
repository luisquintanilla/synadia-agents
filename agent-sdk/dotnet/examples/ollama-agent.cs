// Serve a local Ollama model as a Synadia agent: any Microsoft.Extensions.AI IChatClient becomes discoverable and
// promptable from every SDK (TypeScript, Python, .NET) with one call, ServeChatClient.
//
//   ollama pull qwen3.5:4b                           # once
//   dotnet run ollama-agent.cs                      # serves agents.prompt.ollama.<owner>.qwen
//   dotnet run ollama-agent.cs -- --agent-tools     # also let the model discover/prompt other agents
//   dotnet run ollama-agent.cs -- --think           # keep qwen's (slow) thinking; streamed as status chunks
//
// Env: NATS_URL (default nats://127.0.0.1:4222), OLLAMA_ENDPOINT (default http://localhost:11434; a trailing /v1 from
//      the OpenAI-compatible URL is stripped), OLLAMA_MODEL (default qwen3.5:4b).
//
// Demo tools: current_time (runs freely) and restart_service, which needs approval — the approval request travels to
// the caller as a §7 mid-stream query ("Allow tool 'restart_service' …? (yes/no)") and the answer resumes the model.
//
// The model client is OllamaSharp: OllamaApiClient implements IChatClient directly against Ollama's native /api/chat
// (it replaces the deprecated Microsoft.Extensions.AI.Ollama package). OllamaSharp 5.5.2 is compiled against MEAI 10.8
// and runs unchanged on MEAI 11.
#:package Microsoft.Extensions.AI@11.0.0
#:package OllamaSharp@5.5.2
#:project ../src/Synadia.AI.Agents.Service/Synadia.AI.Agents.Service.csproj
#:property PublishAot=false

using System.ComponentModel;
using Microsoft.Extensions.AI;
using NATS.Client.Core;
using OllamaSharp;
using Synadia.AI.Agents;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Tools;

var endpoint = (Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434").TrimEnd('/');
if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[..^3];
var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.5:4b";
var useAgentTools = args.Contains("--agent-tools");
var think = args.Contains("--think");

// A cold model load can take well over HttpClient's default 100 s.
var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(10) };
IChatClient ollama = new OllamaApiClient(http, model);

IChatClient chatClient = ollama.AsBuilder().UseFunctionInvocation().Build();

[Description("Returns the current local date and time.")]
static string CurrentTime() => DateTimeOffset.Now.ToString("F");

[Description("Restarts a named service. Destructive: requires the caller's approval.")]
static string RestartService([Description("The service name")] string name) => $"service '{name}' restarted";

List<AITool> tools =
[
    AIFunctionFactory.Create(CurrentTime, "current_time"),
    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(RestartService, "restart_service")),
];

await using var nc = new NatsConnection(new NatsOpts { Url = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://127.0.0.1:4222" });

var owner = Environment.GetEnvironmentVariable("USER") ?? Environment.GetEnvironmentVariable("USERNAME") ?? "anon";
owner = AgentSubject.IsValidToken(owner) ? owner : "anon";

await using var service = new AgentService(nc, new AgentServiceOptions
{
    Agent = "ollama",
    Owner = owner,
    Name = "qwen",
    Description = $"{model} via Ollama, served with Microsoft.Extensions.AI",
});

await using var agentsClient = new AgentsClient(nc);
await using AgentTools? agentTools = useAgentTools
    ? new AgentTools(agentsClient, new AgentToolsOptions { SelfAddress = $"agents.prompt.ollama.{owner}.qwen" })
    : null;

service.ServeChatClient(chatClient, new ChatClientServeOptions
{
    Instructions = "You are a concise, friendly assistant reachable over NATS. Use tools only when they help. "
        + (useAgentTools ? "You can discover other agents with discover_agents and delegate with prompt_agent." : ""),
    ChatOptions = new ChatOptions
    {
        Tools = tools,
        // qwen3.5 thinks by default (slow). OllamaSharp maps Effort = None to Ollama's `think: false`; --think keeps
        // thinking and streams the reasoning as status chunks.
        Reasoning = think ? null : new ReasoningOptions { Effort = ReasoningEffort.None },
    },
    IncludeReasoning = think,
    AgentTools = agentTools,
});

await service.StartAsync();
Console.WriteLine($"ollama agent ({model}) listening on {service.Subject} — Ctrl-C to stop");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
await done.Task;
