using Microsoft.Extensions.AI;
using NATS.Client.Core;
using OllamaSharp;
using Synadia.AI.Agents.Testing;
using Synadia.Examples.DocumentAgent;
using Synadia.Examples.DocumentAgent.Host;

// NATS host for the document agent. Usage:
//   document-agent-host [--nats <url>] [--embedded-nats] [--owner <o>] [--name <n>]
//                       [--extract-only] [--no-vision] [--think]
//                       [--ollama <url>] [--chat-model <m>] [--embed-model <m>]
// --embedded-nats starts a throw-away nats-server (must be on PATH) on a random port and prints its URL.
// --nats / NATS_URL may carry credentials (nats://user:pass@host); the printed URL masks the password.
// --extract-only needs no model at all: PDFs (PdfPig) and text only, no questions.

var argList = args.ToList();
string? natsUrl = TakeOption(argList, "--nats") ?? Environment.GetEnvironmentVariable("NATS_URL");
bool embeddedNats = TakeFlag(argList, "--embedded-nats");
string owner = DocumentAgentHosting.ToToken(TakeOption(argList, "--owner") ?? Environment.UserName);
string name = DocumentAgentHosting.ToToken(TakeOption(argList, "--name") ?? "main");
string ollamaUrl = TakeOption(argList, "--ollama") ?? Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
string chatModel = TakeOption(argList, "--chat-model") ?? "qwen3.5:4b";
string embedModel = TakeOption(argList, "--embed-model") ?? "embeddinggemma";
bool extractOnly = TakeFlag(argList, "--extract-only");
bool noVision = extractOnly || TakeFlag(argList, "--no-vision");
bool think = TakeFlag(argList, "--think");
if (argList.Count > 0)
{
    Console.Error.WriteLine($"unknown argument(s): {string.Join(' ', argList)}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var server = embeddedNats ? await NatsServerProcess.StartAsync(cancellationToken: cts.Token) : null;
string url = server?.Url ?? natsUrl ?? "nats://127.0.0.1:4222";
await using var nc = new NatsConnection(new NatsOpts { Url = url, Name = "document-agent-host" });
await nc.ConnectAsync();

using var ollamaChat = new OllamaApiClient(new Uri(ollamaUrl), chatModel);
using var ollamaEmbed = new OllamaApiClient(new Uri(ollamaUrl), embedModel);

// qwen3.5 thinks by default; extraction and lookup answers don't need it.
var answerOptions = new ChatOptions();
if (!think)
{
    answerOptions.RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = false };
}

IChatClient? visionClient = noVision ? null : new ChatClientBuilder(ollamaChat)
    .ConfigureOptions(o => o.RawRepresentationFactory ??= answerOptions.RawRepresentationFactory)
    .Build();

using var router = new DocumentExtractorRouter(visionClient);
using var knowledgeBase = extractOnly ? null : new DocumentKnowledgeBase(router, ollamaEmbed);
long maxAttachmentBytes = DocumentAgentHosting.MaxAttachmentBytesFor(nc);
var agent = new DocumentAgent(router, knowledgeBase, extractOnly ? null : ollamaChat, new DocumentAgentOptions
{
    AnswerChatOptions = answerOptions,
    MaxAttachmentBytes = maxAttachmentBytes,
});

await using var service = DocumentAgentHosting.Create(nc, agent, router.SupportsImages, owner, name);
await service.StartAsync(cts.Token);

Console.WriteLine($"NATS_URL={RedactPassword(url)}");
Console.WriteLine($"listening on {service.Subject.Prompt} (max_payload={service.AdvertisedMaxPayload}, attachments up to {PayloadLimits.Format(maxAttachmentBytes)}; " +
    $"{(extractOnly ? "extract only" : $"vision={(router.SupportsImages ? chatModel : "off")}, embeddings={embedModel}")})");
Console.WriteLine("Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

await service.StopAsync();
return 0;

static string? TakeOption(List<string> list, string name)
{
    int i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count)
    {
        return null;
    }

    string value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> list, string name) => list.Remove(name);

static string RedactPassword(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Contains(':')
        ? url.Replace(uri.UserInfo, uri.UserInfo[..uri.UserInfo.IndexOf(':')] + ":***", StringComparison.Ordinal)
        : url;
