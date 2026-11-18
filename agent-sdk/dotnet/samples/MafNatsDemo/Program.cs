// Microsoft Agent Framework <-> Synadia Agent Protocol for NATS demo.
//
//   dotnet run                      all-in-one: embedded nats-server, two remote agents, a local MAF agent on Ollama
//   dotnet run -- serve             serve an Ollama-backed MAF agent ("concierge") on $NATS_URL until Ctrl+C
//   dotnet run -- call "<prompt>"   local MAF agent on Ollama using every agent discovered on $NATS_URL as a tool
//
// Options: --auto answers remote questions automatically (no console input); --owner <o> filters discovery.
// Environment: NATS_URL (default: start an embedded nats-server on a random port), OLLAMA_URL
// (default http://localhost:11434), OLLAMA_MODEL (default qwen3.5:4b), OLLAMA_THINK (default off;
// true | low | medium | high enables thinking).

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NATS.Client.Core;
using OllamaSharp;
using Synadia.AI.Agents;
using Synadia.AI.Agents.AgentFramework;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Testing;

var argList = args.ToList();
var auto = argList.Remove("--auto") || Console.IsInputRedirected;
var ownerFilter = TakeOption(argList, "--owner");
var command = argList.Count > 0 ? argList[0] : "demo";

// Native Ollama API (OllamaSharp's OllamaApiClient is an IChatClient). A trailing /v1 from the
// OpenAI-compatible endpoint is tolerated for backward compatibility.
var ollamaUrl = (Environment.GetEnvironmentVariable("OLLAMA_URL") is { Length: > 0 } u ? u : "http://localhost:11434").TrimEnd('/');
if (ollamaUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) ollamaUrl = ollamaUrl[..^3];
var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.5:4b";
// Thinking models (qwen3.5) can deliberate for minutes on a laptop, so thinking is off unless OLLAMA_THINK opts in.
// OllamaSharp maps ChatOptions.Reasoning to Ollama's `think`: Effort None -> false, Low/Medium/High -> level.
var reasoningOptions = ParseThink(Environment.GetEnvironmentVariable("OLLAMA_THINK"));
IChatClient ollama = ((IChatClient)new OllamaApiClient(new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(5) }, model))
    .AsBuilder()
    .ConfigureOptions(o => o.Reasoning ??= reasoningOptions)
    .Build();

await using var embedded = Environment.GetEnvironmentVariable("NATS_URL") is { Length: > 0 } ? null : await NatsServerProcess.StartAsync();
var natsUrl = embedded?.Url ?? Environment.GetEnvironmentVariable("NATS_URL")!;
await using var nc = new NatsConnection(new NatsOpts { Url = natsUrl, Name = "maf-nats-demo" });
await nc.ConnectAsync();
Step($"NATS {natsUrl}{(embedded is null ? "" : " (embedded)")}; model {model} @ {ollamaUrl}");

switch (command)
{
    case "serve":
        await using (var service = await ServeConciergeAsync(nc, ollama, ownerFilter ?? "demo"))
        {
            Step("serving 'concierge' — Ctrl+C to stop");
            var done = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
            await done.Task;
        }
        break;

    case "call":
        await CallAsync(nc, ollama, argList.Count > 1 ? argList[1] : "Ask the echo agent to repeat 'hello from MAF' and tell me its reply.", ownerFilter, auto);
        break;

    default:
        await DemoAsync(nc, ollama, auto);
        break;
}

static async Task DemoAsync(NatsConnection nc, IChatClient ollama, bool auto)
{
    var owner = $"demo{Environment.ProcessId}";
    await using var echo = new ReferenceAgent(nc, new ReferenceAgentOptions { Owner = owner, Name = "echo", Description = "Echoes text back; 'ask <question>' asks the caller first." });
    await echo.StartAsync();
    await using var concierge = await ServeConciergeAsync(nc, ollama, owner);

    await using var client = new AgentsClient(nc);
    var agents = await client.DiscoverAsync(new AgentFilter { Owner = owner });
    Step($"discovered {agents.Count} agents over NATS:");
    foreach (var a in agents) Console.WriteLine($"   - {a.Info.Name} ({a.Info.Agent}) — {a.Info.Description}");
    var echoAgent = agents.Single(a => a.Info.Name == "echo");
    var conciergeAgent = agents.Single(a => a.Info.Name == "concierge");

    // 1. A local MAF agent (Ollama) uses a remote NATS agent as a tool.
    Step("1) local MAF agent on Ollama calls the remote 'echo' NATS agent as a tool");
    var echoTool = echoAgent.AsAIAgent(new NatsAIAgentOptions { Name = "echo", Description = "Echoes the query text back verbatim." }).AsAIFunction();
    var orchestrator = new ChatClientAgent(ollama, new ChatClientAgentOptions
    {
        Name = "orchestrator",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a concise assistant. Use the available tools when asked to, then report what they returned.",
            Tools = [echoTool],
        },
    });
    var prompt = "Call the echo tool with the text 'hello from Microsoft Agent Framework' and tell me exactly what it returned.";
    Console.WriteLine($"   user> {prompt}");
    Console.WriteLine($"   orchestrator> {(await orchestrator.RunAsync(prompt)).Text.Trim()}");

    // 2. Human in the loop: the remote agent asks a question mid-stream; the MAF run pauses and resumes with the answer.
    Step("2) remote question surfaces as a MAF input request; the run pauses and resumes on the next RunAsync");
    var natsEcho = echoAgent.AsAIAgent();
    var session = await natsEcho.CreateSessionAsync();
    var paused = await natsEcho.RunAsync("ask What is your favorite color?", session);
    var query = paused.Messages.SelectMany(m => m.Contents).OfType<AgentQueryContent>().Single();
    var color = Ask($"   remote asks: {query.Prompt}", auto ? "teal" : null);
    var resumed = await natsEcho.RunAsync(new ChatMessage(ChatRole.User, [query.CreateResponse(color)]), session);
    Console.WriteLine($"   echo> {resumed.Text}");

    // 3. Tool approval across NATS: a served MAF agent's approval-required tool becomes a protocol query.
    Step("3) served MAF agent (Ollama) needs approval for a tool; the approval travels over NATS as a query");
    var natsConcierge = conciergeAgent.AsAIAgent(new NatsAIAgentOptions
    {
        QueryHandler = (q, _) => ValueTask.FromResult<string?>(Ask($"   remote asks: {q.Prompt}", auto ? "yes" : null)),
    });
    var booking = "Book a table for 2 at Luigi's at 7pm.";
    Console.WriteLine($"   user> {booking}");
    Console.WriteLine($"   concierge> {(await natsConcierge.RunAsync(booking)).Text.Trim()}");
}

static async Task<AgentService> ServeConciergeAsync(NatsConnection nc, IChatClient ollama, string owner)
{
    [Description("Book a restaurant table.")]
    static string BookTable([Description("Restaurant name")] string restaurant, [Description("Time, e.g. 7pm")] string time, [Description("Party size")] int people)
        => $"Confirmed: {people} at {restaurant}, {time}. Reference #NATS-{Random.Shared.Next(1000, 9999)}.";

    var concierge = new ChatClientAgent(ollama, new ChatClientAgentOptions
    {
        Name = "concierge",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a restaurant concierge. Use book_table to make bookings, then confirm the booking reference in one sentence.",
            Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(BookTable, "book_table"))],
        },
    });
    var service = new AgentService(nc, new AgentServiceOptions
    {
        Agent = "maf",
        Owner = owner,
        Name = "concierge",
        Description = "Books restaurant tables (Microsoft Agent Framework on Ollama); bookings need approval.",
        Logger = new StderrLogger(),
    }).ServeAIAgent(concierge);
    await service.StartAsync();
    return service;
}

static async Task CallAsync(NatsConnection nc, IChatClient ollama, string prompt, string? owner, bool auto)
{
    await using var client = new AgentsClient(nc);
    var agents = await client.DiscoverAsync(new AgentFilter { Owner = owner });
    if (agents.Count == 0)
    {
        Step("no agents found — start one (e.g. `dotnet run -- serve` or the TS/Python reference agent)");
        return;
    }
    var tools = new List<AITool>();
    foreach (var a in agents)
    {
        var name = Sanitize(a.Info.Name);
        Console.WriteLine($"   tool {name}: {a.Info.Description}");
        tools.Add(a.AsAIAgent(new NatsAIAgentOptions
        {
            Name = name,
            Description = a.Info.Description,
            QueryHandler = (q, _) => ValueTask.FromResult<string?>(Ask($"   {name} asks: {q.Prompt}", auto ? "yes" : null)),
        }).AsAIFunction());
    }
    var orchestrator = new ChatClientAgent(ollama, new ChatClientAgentOptions
    {
        Name = "orchestrator",
        ChatOptions = new ChatOptions { Instructions = "Use the available agent tools to fulfil the request, then answer concisely.", Tools = tools },
    });
    Console.WriteLine($"   user> {prompt}");
    Console.WriteLine($"   orchestrator> {(await orchestrator.RunAsync(prompt)).Text.Trim()}");
}

static string Ask(string question, string? autoAnswer)
{
    Console.Write($"{question}\n   you> ");
    if (autoAnswer is not null)
    {
        Console.WriteLine($"{autoAnswer} (auto)");
        return autoAnswer;
    }
    return Console.ReadLine() ?? "";
}

static void Step(string text)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n== {text}");
    Console.ResetColor();
}

static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

// OLLAMA_THINK: unset/false/none -> think=false; true -> model default; low|medium|high -> that level.
static ReasoningOptions? ParseThink(string? value) => value?.Trim().ToLowerInvariant() switch
{
    null or "" or "false" or "0" or "off" or "none" => new ReasoningOptions { Effort = ReasoningEffort.None },
    "true" or "1" or "on" => null,
    "low" => new ReasoningOptions { Effort = ReasoningEffort.Low },
    "medium" => new ReasoningOptions { Effort = ReasoningEffort.Medium },
    "high" => new ReasoningOptions { Effort = ReasoningEffort.High },
    _ => throw new ArgumentException($"OLLAMA_THINK must be false, true, low, medium or high (got '{value}')."),
};

/// <summary>Prints warnings and errors from the hosted agent service.</summary>
sealed class StderrLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel)) Console.Error.WriteLine($"   [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $"\n{exception}")}");
    }
}
