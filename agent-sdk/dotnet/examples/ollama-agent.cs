// Serve a local Ollama model as a Synadia agent: any Microsoft.Extensions.AI IChatClient becomes discoverable and
// promptable from every SDK (TypeScript, Python, .NET) with one call, ServeChatClient.
//
//   ollama pull qwen3.5:4b                           # once
//   dotnet run ollama-agent.cs                      # serves agents.prompt.ollama.<owner>.qwen
//   dotnet run ollama-agent.cs -- --agent-tools     # also let the model discover/prompt other agents
//   dotnet run ollama-agent.cs -- --think           # keep qwen's (slow) thinking; streamed as status chunks
//
// Env: NATS_URL (default nats://127.0.0.1:4222), OLLAMA_ENDPOINT (default http://localhost:11434/v1),
//      OLLAMA_MODEL (default qwen3.5:4b).
//
// Demo tools: current_time (runs freely) and restart_service, which needs approval — the approval request travels to
// the caller as a §7 mid-stream query ("Allow tool 'restart_service' …? (yes/no)") and the answer resumes the model.
#:package Microsoft.Extensions.AI@11.0.0
#:package Microsoft.Extensions.AI.OpenAI@11.0.0
#:project ../src/Synadia.AI.Agents.Service/Synadia.AI.Agents.Service.csproj
#:property PublishAot=false

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using NATS.Client.Core;
using OpenAI;
using Synadia.AI.Agents;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Tools;

var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434/v1";
var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.5:4b";
var useAgentTools = args.Contains("--agent-tools");
var think = args.Contains("--think");

IChatClient ollama = new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
    .GetChatClient(model)
    .AsIChatClient();

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
        // qwen3.5 thinks for minutes per turn by default; --think keeps it and streams the reasoning as status chunks.
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
