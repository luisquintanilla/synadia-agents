using System.Diagnostics;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Synadia.Examples.DocumentAgent;
using Synadia.Examples.DocumentAgent.Harness;

// Console harness for the NATS-independent document agent. Usage:
//   document-agent gen-fixtures [dir]
//   document-agent extract <file>...
//   document-agent ask "<question>" <file>...
//   document-agent demo                      (fixtures → extract → ask → oversize; the default)
// Options: --ollama <url> (default http://localhost:11434), --chat-model <m> (qwen3.5:4b),
//          --embed-model <m> (embeddinggemma), --no-vision, --think

var argList = args.ToList();
string ollamaUrl = TakeOption(argList, "--ollama") ?? Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
string chatModel = TakeOption(argList, "--chat-model") ?? "qwen3.5:4b";
string embedModel = TakeOption(argList, "--embed-model") ?? "embeddinggemma";
bool noVision = TakeFlag(argList, "--no-vision");
bool think = TakeFlag(argList, "--think");
string command = argList.Count > 0 ? argList[0] : "demo";
var rest = argList.Skip(1).ToList();
string fixturesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (command == "gen-fixtures")
{
    foreach (var path in Fixtures.Generate(rest.Count > 0 ? rest[0] : fixturesDir))
    {
        Console.WriteLine($"wrote {path} ({PayloadLimits.Format(new FileInfo(path).Length)})");
    }

    return 0;
}

using var ollamaChat = new OllamaApiClient(new Uri(ollamaUrl), chatModel);
using var ollamaEmbed = new OllamaApiClient(new Uri(ollamaUrl), embedModel);
IChatClient chat = ollamaChat;
IEmbeddingGenerator<string, Embedding<float>> embeddings = ollamaEmbed;

// qwen3.5 thinks by default; the answers here are extraction/lookup, so skip it unless asked.
var answerOptions = new ChatOptions();
if (!think)
{
    answerOptions.RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = false };
}

var visionClient = noVision ? null : new ChatClientBuilder(chat).ConfigureOptions(o =>
{
    o.RawRepresentationFactory ??= answerOptions.RawRepresentationFactory;
}).Build();

using var router = new DocumentExtractorRouter(visionClient);
using var knowledgeBase = new DocumentKnowledgeBase(router, embeddings);
var agent = new DocumentAgent(router, knowledgeBase, chat, new DocumentAgentOptions { AnswerChatOptions = answerOptions });

switch (command)
{
    case "extract":
        await RunAsync("/extract", Load(rest));
        break;
    case "ask" when rest.Count > 0:
        await RunAsync(rest[0], Load(rest.Skip(1)));
        break;
    case "demo":
        await DemoAsync();
        break;
    default:
        Console.Error.WriteLine("usage: document-agent [demo | gen-fixtures [dir] | extract <file>... | ask \"<question>\" <file>...] [--ollama url] [--chat-model m] [--embed-model m] [--no-vision] [--think]");
        return 2;
}

return 0;

async Task DemoAsync()
{
    if (!File.Exists(Path.Combine(fixturesDir, Fixtures.ReportPdf)))
    {
        Fixtures.Generate(fixturesDir);
    }

    var pdf = Load([Path.Combine(fixturesDir, Fixtures.ReportPdf)]);
    var png = Load([Path.Combine(fixturesDir, Fixtures.InvoicePng)]);
    var notes = Load([Path.Combine(fixturesDir, Fixtures.NotesText)]);

    Banner("1. Extract a PDF (PdfPig, local, no model)");
    await RunAsync(string.Empty, pdf);

    if (!noVision)
    {
        Banner($"2. Extract an image (vision model: {chatModel})");
        await RunAsync("extract this invoice", png);
    }

    Banner("3. Oversize attachment (over the NATS max_payload budget)");
    var big = new DataContent(new byte[1_200_000], "application/pdf") { Name = "scan-1200k.pdf" };
    await RunAsync(string.Empty, [big]);

    Banner($"4. Docs/RAG: index all three, then ask ({embedModel} + {chatModel})");
    await RunAsync("What was the revenue in the North region, and how long does express shipping take?", [.. pdf, .. png, .. notes]);

    Banner("5. Follow-up question against the same index (no attachments)");
    await RunAsync("What is the total due on invoice INV-1042, and what warranty does Widget Pro have?", []);

    Banner("6. /docs");
    await RunAsync("/docs", []);
}

async Task RunAsync(string prompt, IReadOnlyList<DataContent> attachments)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"> {(prompt.Length == 0 ? "(no prompt)" : prompt)}{(attachments.Count > 0 ? $"  [{string.Join(", ", attachments.Select(a => a.Name))}]" : string.Empty)}");
    Console.ResetColor();
    var sw = Stopwatch.StartNew();
    await foreach (var update in agent.HandleAsync(new DocumentAgentRequest(prompt, attachments), cts.Token))
    {
        if (DocumentAgentStatus.TryGet(update, out var status))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine($"  [status {sw.Elapsed.TotalSeconds,5:0.0}s] {status.Message}");
            Console.ResetColor();
        }
        else
        {
            Console.Write(update.Text);
        }
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Error.WriteLine($"  [done in {sw.Elapsed.TotalSeconds:0.0}s]");
    Console.ResetColor();
}

static IReadOnlyList<DataContent> Load(IEnumerable<string> paths) =>
    paths.Select(p => new DataContent(File.ReadAllBytes(p), MediaTypes.Resolve(null, p)) { Name = Path.GetFileName(p) }).ToArray();

static void Banner(string title)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n=== {title} ===");
    Console.ResetColor();
}

static string? TakeOption(List<string> list, string name)
{
    int i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count)
    {
        return null;
    }

    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> list, string name) => list.Remove(name);
