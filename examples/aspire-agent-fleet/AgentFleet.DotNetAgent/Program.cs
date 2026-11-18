using AgentFleet.DotNetAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A Synadia agent written in .NET: any Microsoft.Extensions.AI IChatClient (here Ollama) served on NATS
// with AgentService.ServeChatClient. Both dependencies come from Aspire references:
//   ConnectionStrings__nats  → AddNatsClient("nats")                     (Aspire.NATS.Net)
//   ConnectionStrings__chat  → AddOllamaApiClient("chat").AddChatClient()  (CommunityToolkit.Aspire.OllamaSharp)
// "chat" is "Endpoint=<ollama url>;Model=<model>", from the AppHost's Ollama model resource.
// Traces/metrics/logs flow to the Aspire dashboard through ServiceDefaults.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNatsClient("nats");

builder.AddOllamaApiClient("chat")
    // Wraps the client in OpenTelemetryChatClient: GenAI spans ("chat qwen3.5:4b") + token metrics.
    // Prompt/response text is only recorded when GENAI_CAPTURE_CONTENT=true (the AgentFleet AppHost sets it
    // so the demo can show it in Traces).
    .AddChatClient(otel => otel.EnableSensitiveData = builder.Configuration.GetValue<bool>("GENAI_CAPTURE_CONTENT"))
    .UseLogging();

// ServiceDefaults gives every HttpClient the standard resilience handler (10 s attempt / 30 s total, retries).
// That suits short service calls, not LLM generations: a cold model load or a long answer would time out,
// and retries would re-queue generations on a single-slot Ollama. The model call gets one attempt, 5 minutes.
// (RemoveAllResilienceHandlers is the documented per-client opt-out; it is still marked experimental.)
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient("chat_httpClient")
    .RemoveAllResilienceHandlers()
    .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromMinutes(5));
#pragma warning restore EXTEXP0001

builder.Services.AddOptions<OllamaAgentOptions>()
    .Bind(builder.Configuration.GetSection("Agent"))
    .PostConfigure<IChatClient>((o, chat) =>
        o.Model = chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? o.Model);
builder.Services.AddHostedService<OllamaAgentWorker>();

builder.Build().Run();