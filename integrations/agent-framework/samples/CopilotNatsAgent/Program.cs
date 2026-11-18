// GitHub Copilot as a Synadia Agent Protocol fleet agent, via Microsoft Agent Framework.
//
//   dotnet run -- serve [--owner o] [--name copilot] [--model claude-haiku-4.5] [--reasoning low] [--workdir dir] [--instructions "..."]
//   dotnet run -- call "<prompt>" [--owner o] [--name copilot] [--auto]
//
// `serve` registers agent type "copilot" and stays up until Ctrl+C. Copilot's permission requests (shell, write, read,
// url, mcp, ...) and ask_user questions become protocol queries to the caller; anything unanswered is denied.
// `call` discovers the agent and talks to it through the adapter's AsAIAgent(); --auto answers every query "no".
// Environment: NATS_URL (default: start an embedded nats-server on a random port and print its URL).

using System.Collections.Concurrent;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using NATS.Client.Core;
using Synadia.AI.Agents;
using Synadia.AI.Agents.AgentFramework;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Testing;

var argList = args.ToList();
var auto = argList.Remove("--auto");
var owner = TakeOption(argList, "--owner");
var name = TakeOption(argList, "--name") ?? "copilot";
var model = TakeOption(argList, "--model") ?? "claude-haiku-4.5";
var reasoning = TakeOption(argList, "--reasoning");
var workdir = TakeOption(argList, "--workdir") ?? Directory.GetCurrentDirectory();
var instructions = TakeOption(argList, "--instructions")
    ?? "You are GitHub Copilot, reachable over NATS as a fleet agent. Answer concisely in plain text.";
var command = argList.Count > 0 ? argList[0] : "serve";

await using var embedded = Environment.GetEnvironmentVariable("NATS_URL") is { Length: > 0 } ? null : await NatsServerProcess.StartAsync();
var natsUrl = embedded?.Url ?? Environment.GetEnvironmentVariable("NATS_URL")!;
await using var nc = new NatsConnection(new NatsOpts { Url = natsUrl, Name = "copilot-nats-agent" });
await nc.ConnectAsync();
Console.Error.WriteLine($"NATS {natsUrl}{(embedded is null ? "" : " (embedded; set NATS_URL to this for callers)")}");

switch (command)
{
    case "serve":
        await ServeAsync(nc, owner ?? "demo", name, model, reasoning, workdir, instructions);
        break;
    case "call":
        await CallAsync(nc, owner, name, argList.Count > 1 ? argList[1] : "Reply with just: pong", auto);
        break;
    default:
        Console.Error.WriteLine($"unknown command '{command}' (use serve or call)");
        Environment.ExitCode = 2;
        break;
}

static async Task ServeAsync(NatsConnection nc, string owner, string name, string model, string? reasoning, string workdir, string instructions)
{
    // Prompts currently being served; Copilot's callbacks find theirs by Copilot session id.
    var runs = new ConcurrentDictionary<ServedRun, byte>();
    ServedRun? Find(string sessionId) =>
        runs.Keys.FirstOrDefault(r => r.Session is GitHubCopilotAgentSession s && s.SessionId == sessionId);

    var sessionConfig = new SessionConfig
    {
        Model = model,
        ReasoningEffort = reasoning, // leave unset for models that reject it (e.g. claude-haiku-4.5)
        WorkingDirectory = workdir,
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = instructions },
        OnPermissionRequest = async (request, invocation) =>
        {
            // Deny by default: only an explicit "yes" from the NATS caller approves, and only once.
            if (Find(invocation.SessionId) is not { } run) return PermissionDecision.Reject("no NATS caller is attached to approve this");
            var reply = await run.AskAsync($"Allow Copilot to {Describe(request)}? (yes/no)");
            if (reply is null) return PermissionDecision.Reject("approval timed out");
            var approved = IsYes(reply.Prompt);
            Console.Error.WriteLine($"   permission {request.Kind}: {(approved ? "approved" : "denied")} by caller");
            return approved ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject("denied by the NATS caller");
        },
        OnUserInputRequest = async (request, invocation) =>
        {
            var prompt = request.Choices is { Count: > 0 } choices ? $"{request.Question} ({string.Join(" / ", choices)})" : request.Question;
            var reply = Find(invocation.SessionId) is { } run ? await run.AskAsync(prompt) : null;
            return new UserInputResponse { Answer = reply?.Prompt ?? "", WasFreeform = true };
        },
    };

    await using var copilot = new CopilotClient(new CopilotClientOptions { WorkingDirectory = workdir });
    await copilot.StartAsync();
    // AsAIAgent(instructions: ...) cannot set the model or permission handler, so pass a full SessionConfig instead.
    AIAgent agent = copilot.AsAIAgent(sessionConfig, ownsClient: false, name: name, description: $"GitHub Copilot ({model}) via Microsoft Agent Framework");

    await using var service = new AgentService(nc, new AgentServiceOptions
    {
        Agent = "copilot",
        Owner = owner,
        Name = name,
        Description = $"GitHub Copilot ({model}) served via Microsoft Agent Framework; tool permissions are asked of the caller.",
        Logger = new StderrLogger(),
    }).ServeAIAgent(agent, new AIAgentServeOptions
    {
        OnRun = run =>
        {
            runs[run] = 0;
            return new Release(() => runs.TryRemove(run, out _));
        },
    });
    await service.StartAsync();
    Console.Error.WriteLine($"serving agents.prompt.copilot.{owner}.{name} (model {model}, workdir {workdir}) — Ctrl+C to stop");

    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    await done.Task;
}

static async Task CallAsync(NatsConnection nc, string? owner, string name, string prompt, bool auto)
{
    await using var client = new AgentsClient(nc);
    var found = await client.DiscoverAsync(new AgentFilter { Agent = "copilot", Owner = owner, Name = name });
    if (found.Count == 0)
    {
        Console.Error.WriteLine("no copilot agent found — start one with `dotnet run -- serve`");
        Environment.ExitCode = 2;
        return;
    }
    var agent = found[0].AsAIAgent(new NatsAIAgentOptions
    {
        QueryHandler = (q, _) =>
        {
            Console.Write($"\n[copilot asks] {q.Prompt}\nyou> ");
            if (auto)
            {
                Console.WriteLine("no (auto)");
                return ValueTask.FromResult<string?>("no");
            }
            return ValueTask.FromResult<string?>(Console.ReadLine() ?? "no");
        },
    });
    Console.WriteLine($"user> {prompt}");
    Console.Write("copilot> ");
    await foreach (var update in agent.RunStreamingAsync(prompt))
    {
        Console.Write(update.Text);
    }
    Console.WriteLine();
}

static string Describe(PermissionRequest request) => request switch
{
    PermissionRequestShell shell => $"run shell command `{shell.FullCommandText}`" + (shell.Intention is { Length: > 0 } i ? $" ({i})" : ""),
    PermissionRequestWrite write => $"write file {write.FileName}" + (write.Intention is { Length: > 0 } i ? $" ({i})" : ""),
    PermissionRequestRead read => $"read {read.Path}",
    PermissionRequestUrl url => $"fetch {url.Url}",
    PermissionRequestMcp mcp => $"call MCP tool {mcp.ServerName}/{mcp.ToolName}",
    PermissionRequestCustomTool tool => $"call tool {tool.ToolName}",
    PermissionRequestHook hook => $"call tool {hook.ToolName}" + (hook.HookMessage is { Length: > 0 } m ? $" ({m})" : ""),
    _ => $"perform a '{request.Kind}' action",
};

static bool IsYes(string text) => text.Trim().TrimEnd('.', '!').ToLowerInvariant() is "y" or "yes" or "approve" or "approved" or "allow" or "ok" or "true";

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

sealed class Release(Action action) : IDisposable
{
    public void Dispose() => action();
}

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
