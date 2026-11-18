using Microsoft.Extensions.AI;

namespace Synadia.Examples.DocumentAgent.Tests;

public class DocumentAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", true, DocumentAgentMode.Extract, null)]
    [InlineData("Please extract the tables", true, DocumentAgentMode.Extract, null)]
    [InlineData("What was North revenue?", true, DocumentAgentMode.Ask, null)]
    [InlineData("What was North revenue?", false, DocumentAgentMode.Ask, null)]
    [InlineData("/extract", false, DocumentAgentMode.Extract, "extract")]
    [InlineData("/ask what is the total?", true, DocumentAgentMode.Ask, "ask")]
    [InlineData("/index", true, DocumentAgentMode.Ingest, "index")]
    [InlineData("/clear", false, DocumentAgentMode.Reset, "clear")]
    [InlineData("/bogus hi", false, DocumentAgentMode.Ask, "help")]
    public void Resolve_InfersModeFromCommandAndAttachments(string prompt, bool withAttachment, DocumentAgentMode expected, string? command)
    {
        DataContent[] attachments = withAttachment ? [new DataContent(new byte[] { 1 }, MediaTypes.Pdf)] : [];
        var (mode, _, cmd) = DocumentAgent.Resolve(new DocumentAgentRequest(prompt, attachments));

        Assert.Equal(expected, mode);
        Assert.Equal(command, cmd);
    }

    [Fact]
    public void Resolve_StripsCommandFromPrompt() =>
        Assert.Equal("what is the total?", DocumentAgent.Resolve(new DocumentAgentRequest("/ask   what is the total?", [])).Prompt);

    [Theory]
    [InlineData("Takes 1 day ([report.pdf](file://localhost/data/report.pdf)).", "Takes 1 day (report.pdf).")]
    [InlineData("See [notes](support-notes.md) and [docs](https://example.com/a).", "See notes and [docs](https://example.com/a).")]
    public void ScrubCitationLinks_KeepsOnlyWebLinks(string input, string expected) =>
        Assert.Equal(expected, DocumentAgent.ScrubCitationLinks(input));

    [Fact]
    public async Task Extract_StreamsMarkdownWithPageStatuses()
    {
        var agent = new DocumentAgent(new DocumentExtractorRouter());

        var (text, statuses) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("", [TestFiles.Load("fabrikam-q3-report.pdf")]), Ct));

        Assert.StartsWith("# fabrikam-q3-report.pdf", text);
        Assert.Contains("| North | 18,400 | $1.20M | +15% |", text);
        Assert.Contains("<!-- page 2 -->", text);
        Assert.Contains(statuses, s => s is { Page: 2, PageCount: 2 } && s.Message.EndsWith("page 2/2", StringComparison.Ordinal));
        Assert.All(statuses, s => Assert.Equal("fabrikam-q3-report.pdf", s.Attachment));
    }

    [Fact]
    public async Task Extract_RejectsOversizeEmptyAndUnsupportedButContinues()
    {
        var agent = new DocumentAgent(new DocumentExtractorRouter(), options: new DocumentAgentOptions { MaxAttachmentBytes = 4096 });
        DataContent[] attachments =
        [
            new(new byte[5000], MediaTypes.Pdf) { Name = "big.pdf" },
            new(Array.Empty<byte>(), MediaTypes.Pdf) { Name = "empty.pdf" },
            new(new byte[10], "image/png") { Name = "photo.png" },
            new(new byte[10], MediaTypes.OctetStream) { Name = "blob.bin" },
            TestFiles.Load("support-notes.md"),
        ];

        var (text, _) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("/extract", attachments), Ct));

        Assert.Contains("big.pdf is 4.9 KB, over the 4 KB attachment limit", text);
        Assert.Contains("empty.pdf is empty.", text);
        Assert.Contains("# photo.png", text);
        Assert.Contains("# blob.bin", text);
        Assert.Contains("Widget Pro", text); // the good file is still extracted
        Assert.Equal(4, text.Split("> ⚠️").Length - 1);
    }

    [Fact]
    public async Task Extract_DefaultLimitRejectsFilesThatCannotFitInOneNatsMessage()
    {
        var agent = new DocumentAgent(new DocumentExtractorRouter());
        var tooBig = new DataContent(new byte[PayloadLimits.MaxRawAttachmentBytes() + 1], MediaTypes.Pdf) { Name = "huge.pdf" };

        var (text, statuses) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("", [tooBig]), Ct));

        Assert.Contains("over the", text);
        Assert.Contains("max_payload", text);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Ask_IndexesAttachmentsSearchesAndAnswersWithoutLeakingReasoning()
    {
        using var router = new DocumentExtractorRouter();
        using var kb = new DocumentKnowledgeBase(router, new HashingEmbeddingGenerator());
        var chat = new ScriptedChatClient("North revenue was $1.20M.", toolQuery: "north region revenue");
        var agent = new DocumentAgent(router, kb, chat);

        var request = new DocumentAgentRequest("What was North revenue?", [TestFiles.Load("fabrikam-q3-report.pdf"), TestFiles.Load("support-notes.md")]);
        var (text, statuses) = await TestFiles.CollectAsync(agent.HandleAsync(request, Ct));

        Assert.Contains("North revenue was $1.20M.", text);
        Assert.DoesNotContain("thinking", text);
        Assert.Contains("_Sources searched:", text);
        Assert.Contains(statuses, s => s.Message.StartsWith("indexed fabrikam-q3-report.pdf", StringComparison.Ordinal));
        Assert.Contains(statuses, s => s.Message == "searching documents: north region revenue");
        Assert.Equal(2, kb.Documents.Count);

        // Prefetched passages reach the model, and the tool result was fed back on the second turn.
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("$1.20M", chat.Requests[0].Messages.Last().Text);
        Assert.Contains(chat.Requests[1].Messages, m => m.Contents.OfType<FunctionResultContent>().Any());

        var (docs, _) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("/docs", []), Ct));
        Assert.Contains("fabrikam-q3-report.pdf (application/pdf, 2 page(s)", docs);

        await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("/reset", []), Ct));
        Assert.Empty(kb.Documents);
    }

    [Fact]
    public async Task KnowledgeBase_FindsTheRelevantChunk()
    {
        using var router = new DocumentExtractorRouter();
        using var kb = new DocumentKnowledgeBase(router, new HashingEmbeddingGenerator(), new DocumentKnowledgeBaseOptions { MaxTokensPerChunk = 60, OverlapTokens = 0 });
        await using (var pdf = File.OpenRead(TestFiles.PathOf("fabrikam-q3-report.pdf")))
        {
            var ingested = await kb.IngestAsync(pdf, "report.pdf", MediaTypes.Pdf, Ct);
            Assert.Equal(2, ingested.Pages);
            Assert.True(ingested.Chunks >= 2, $"expected several chunks, got {ingested.Chunks}");
        }

        var hits = await kb.SearchAsync("express shipping business day", top: 1, cancellationToken: Ct);

        var hit = Assert.Single(hits);
        Assert.Equal("report.pdf", hit.DocumentId);
        Assert.Contains("express shipping takes 1 business day", hit.Content);
    }

    [Fact]
    public async Task Ask_WithoutKnowledgeBaseExplainsExtractOnly()
    {
        var agent = new DocumentAgent(new DocumentExtractorRouter());
        var (text, _) = await TestFiles.CollectAsync(agent.HandleAsync(new DocumentAgentRequest("what is this?", [TestFiles.Load("support-notes.md")]), Ct));
        Assert.Contains("extraction only", text);
    }

    [Fact]
    public async Task ChatClientAdapter_RoutesLastUserMessageAndExposesHandler()
    {
        var agent = new DocumentAgent(new DocumentExtractorRouter());
        using var client = new DocumentAgentChatClient(agent);
        var message = new ChatMessage(ChatRole.User, [new TextContent("/extract"), TestFiles.Load("support-notes.md")]);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "earlier"), message], cancellationToken: Ct);

        Assert.Contains("# support-notes.md", response.Text);
        Assert.Equal("document-agent", response.ModelId);
        Assert.Same(agent, client.GetService<IDocumentAgentHandler>());
    }
}
