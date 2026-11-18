using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Synadia.Examples.DocumentAgent.Tests;

/// <summary>
/// End-to-end checks against a local Ollama (<c>OLLAMA_URL</c>, default http://localhost:11434) with
/// <c>qwen3.5:4b</c> and <c>embeddinggemma</c> pulled. Skipped when Ollama is not reachable.
/// </summary>
public class OllamaIntegrationTests
{
    private static readonly string Url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var tags = http.GetStringAsync(new Uri(new Uri(Url), "/api/tags")).GetAwaiter().GetResult();
            return tags.Contains("qwen3.5:4b", StringComparison.Ordinal) && tags.Contains("embeddinggemma", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions NoThinking => new() { RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = false } };

    [Fact]
    public async Task VisionModel_ExtractsInvoiceTableFromImage()
    {
        Assert.SkipUnless(Available.Value, $"Ollama with qwen3.5:4b + embeddinggemma not reachable at {Url}");
        using var ollama = new OllamaApiClient(new Uri(Url), "qwen3.5:4b");
        var vision = new ChatClientBuilder(ollama).ConfigureOptions(o => o.RawRepresentationFactory ??= NoThinking.RawRepresentationFactory).Build();
        var agent = new DocumentAgent(new DocumentExtractorRouter(vision));

        var (text, _) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("", [TestFiles.Load("invoice-1042.png")]), Ct));

        Assert.DoesNotContain("```", text);
        Assert.Contains("|", text);
        Assert.Contains("950.40", text);
    }

    [Fact]
    public async Task Rag_AnswersFromIndexedPdf()
    {
        Assert.SkipUnless(Available.Value, $"Ollama with qwen3.5:4b + embeddinggemma not reachable at {Url}");
        using var chat = new OllamaApiClient(new Uri(Url), "qwen3.5:4b");
        using var embed = new OllamaApiClient(new Uri(Url), "embeddinggemma");
        using var router = new DocumentExtractorRouter();
        using var kb = new DocumentKnowledgeBase(router, embed);
        var agent = new DocumentAgent(router, kb, chat, new DocumentAgentOptions { AnswerChatOptions = NoThinking });

        var (text, _) = await TestFiles.CollectAsync(agent.HandleAsync(
            new DocumentAgentRequest("How many business days does express shipping take?", [TestFiles.Load("fabrikam-q3-report.pdf")]), Ct));

        Assert.Matches(@"\b(1|one)\b", text);
        Assert.Contains("fabrikam-q3-report.pdf", text);
    }
}
