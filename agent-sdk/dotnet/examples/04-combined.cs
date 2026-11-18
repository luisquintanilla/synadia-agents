// 04 · LLM agent (combined) — answers prompts with Ollama OR OpenRouter (.NET).
//
// Step 4 of the ladder. The TypeScript and Python versions factor model access into llm.ts / llm.py, a small client
// abstraction that reduces both backends to one chat shape. In .NET that abstraction already exists — both backends are
// just Microsoft.Extensions.AI IChatClients — so the "reusable base" shrinks to one selector function below.
// .NET mirror of 04-combined.ts / 04-combined.py.
//
//   OPENROUTER_API_KEY set  → OpenRouter (OPENROUTER_MODEL, default openai/gpt-4o-mini; OPENAI_BASE_URL overrides the URL)
//   otherwise               → local Ollama (OLLAMA_MODEL, default qwen3.5:4b; OLLAMA_URL, default http://localhost:11434)
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

// Same call whichever backend was picked; any per-backend tuning lives in the selector's client pipeline.
service.ServeChatClient(llm);

await service.StartAsync();
Console.WriteLine($"llm agent listening on {service.Subject.Prompt}");
Console.WriteLine($"backend: {label}");
Console.WriteLine("press Ctrl+C to stop");
await ExampleHost.WaitForShutdownAsync();

// The whole "llm" layer: pick a backend from the environment, hand back an IChatClient and a label for logs.
static (IChatClient Client, string Label) CreateChatClient()
{
    if (Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } apiKey)
    {
        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openai/gpt-4o-mini";
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://openrouter.ai/api/v1";
        var client = new OpenAIClient(new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl), NetworkTimeout = TimeSpan.FromMinutes(10) })
            .GetChatClient(model).AsIChatClient();
        return (client, $"openrouter/{model}");
    }
    else
    {
        var url = (Environment.GetEnvironmentVariable("OLLAMA_URL") ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
            ?? "http://localhost:11434").TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url[..^3];
        var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.5:4b";
        // A cold model load can take well over HttpClient's default 100 s.
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(10) };
        // Thinking models (qwen3.5) think by default, which is slow; OllamaSharp maps ReasoningEffort.None to
        // `think: false`. ConfigureOptions is standard MEAI middleware, so the agent code above never needs to know.
        var client = new ChatClientBuilder(new OllamaApiClient(http, model))
            .ConfigureOptions(o => o.Reasoning ??= new ReasoningOptions { Effort = ReasoningEffort.None })
            .Build();
        return (client, $"ollama/{model}");
    }
}
