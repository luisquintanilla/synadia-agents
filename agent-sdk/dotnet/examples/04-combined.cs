// 04 · LLM agent (combined) — answers prompts with OpenRouter, failing over to local Ollama (.NET).
//
// Step 4 of the ladder. The TypeScript and Python versions factor model access into llm.ts / llm.py, a small client
// abstraction that reduces both backends to one chat shape. In .NET that abstraction already exists — both backends are
// just Microsoft.Extensions.AI IChatClients — and so does routing between them: OrderedFailoverChatClient is itself an
// IChatClient that tries its routes in order. .NET mirror of 04-combined.ts / 04-combined.py.
//
//   OPENROUTER_API_KEY set  → OpenRouter (OPENROUTER_MODEL, default openai/gpt-4o-mini; OPENAI_BASE_URL overrides the
//                             URL), failing over to local Ollama if OpenRouter fails before any output streams
//   otherwise               → local Ollama only (OLLAMA_MODEL, default qwen3.5:4b; OLLAMA_URL, default
//                             http://localhost:11434)
//
// Failover is the one intentional difference from Python/TS, which use OpenRouter alone when the key is set. A bad key,
// a 429 or an outage moves the prompt to Ollama; a failure after output has started streaming ends the prompt with an
// error (a partial answer can't be retracted). Each route keeps its own tuning (Ollama's think:false) on its own client.
//
//   dotnet run 04-combined.cs                 # agents.prompt.llm.<you>.main
//
// Identity/connection flags as in 01-echo (this example registers agent="llm"). Run with -h for every flag.
#:package Microsoft.Extensions.AI@11.0.0
#:package Microsoft.Extensions.AI.OpenAI@11.0.0
#:package OllamaSharp@5.5.2
#:project _shared/ExampleHost.csproj
#:property PublishAot=false

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using Synadia.AI.Agents.Examples;
using Synadia.AI.Agents.Service;

var (llm, label) = CreateChatClient();
using var _ = llm;
var description = $"LLM agent — answers prompts via {label}";

await using var host = await ExampleHost.ConnectAsync(args, agent: "llm", description);
await using var service = new AgentService(host.Connection, host.ServiceOptions(description));

// Same call whether llm is one backend or a failover chain: routing is just another IChatClient.
service.ServeChatClient(llm);

await service.StartAsync();
Console.WriteLine($"llm agent listening on {service.Subject.Prompt}");
Console.WriteLine($"backend: {label}");
Console.WriteLine("press Ctrl+C to stop");
await ExampleHost.WaitForShutdownAsync();

// The whole "llm" layer: Ollama alone, or OpenRouter → Ollama when an OpenRouter key is configured.
static (IChatClient Client, string Label) CreateChatClient()
{
    var (ollama, ollamaName) = CreateOllama();
    if (Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is not { Length: > 0 } apiKey)
        return (ollama, ollamaName);

    var (openRouter, openRouterName) = CreateOpenRouter(apiKey);
#pragma warning disable MEAI001 // Routing/failover clients are [Experimental] in Microsoft.Extensions.AI 11.
    var chain = new OrderedFailoverChatClient(
    [
        Observe(openRouter, openRouterName, isFallback: false),
        Observe(ollama, ollamaName, isFallback: true),
    ]);
#pragma warning restore MEAI001
    return (chain, $"{openRouterName} → {ollamaName} (failover)");
}

static (IChatClient, string) CreateOpenRouter(string apiKey)
{
    var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openai/gpt-4o-mini";
    var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://openrouter.ai/api/v1";
    var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
    {
        Endpoint = new Uri(baseUrl),
        NetworkTimeout = TimeSpan.FromMinutes(10),
        // The failover chain is the retry: hand a 429/outage to the next route instead of backing off here.
        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
    }).GetChatClient(model).AsIChatClient();
    return (client, $"openrouter/{model}");
}

static (IChatClient, string) CreateOllama()
{
    var url = (Environment.GetEnvironmentVariable("OLLAMA_URL") ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
        ?? "http://localhost:11434").TrimEnd('/');
    if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url[..^3];
    var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.5:4b";
    // A cold model load can take well over HttpClient's default 100 s.
    var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(10) };
    // Thinking models (qwen3.5) think by default, which is slow; OllamaSharp maps ReasoningEffort.None to
    // `think: false`. ConfigureOptions sits on this route only, so it never leaks into the OpenRouter request.
    var client = new ChatClientBuilder(new OllamaApiClient(http, model))
        .ConfigureOptions(o => o.Reasoning ??= new ReasoningOptions { Effort = ReasoningEffort.None })
        .Build();
    return (client, $"ollama/{model}");
}

// Makes failover visible on stderr: a route reports its own failure, and the fallback announces when the chain reaches
// it (in the chain, Ollama only runs after OpenRouter failed before streaming anything).
static IChatClient Observe(IChatClient route, string name, bool isFallback) =>
    new ChatClientBuilder(route).Use(async (messages, options, next, cancellationToken) =>
    {
        if (isFallback) Console.Error.WriteLine($"failover: answering with {name}");
        try
        {
            await next(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"route {name} failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }).Build();