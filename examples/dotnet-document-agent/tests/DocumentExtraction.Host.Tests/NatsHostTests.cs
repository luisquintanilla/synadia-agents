using Microsoft.Extensions.AI;
using NATS.Client.Core;
using Synadia.AI.Agents;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Testing;
using Synadia.Examples.DocumentAgent.Host;

namespace Synadia.Examples.DocumentAgent.Tests;

/// <summary>
/// End to end over a real nats-server: the document agent hosted with <c>ServeChatClient</c>, called through the
/// .NET client SDK's <c>Agent.AsIChatClient()</c>. Skipped when nats-server is not installed.
/// </summary>
public sealed class NatsHostTests : IAsyncLifetime
{
    private NatsServerProcess? _server;
    private NatsConnection? _host;
    private NatsConnection? _caller;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!NatsServerProcess.IsAvailable())
        {
            return;
        }

        _server = await NatsServerProcess.StartAsync(cancellationToken: Ct);
        _host = new NatsConnection(new NatsOpts { Url = _server.Url });
        _caller = new NatsConnection(new NatsOpts { Url = _server.Url });
        await _host.ConnectAsync();
        await _caller.ConnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_caller is not null) await _caller.DisposeAsync();
        if (_host is not null) await _host.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }

    [Fact]
    public async Task Extract_PdfAttachmentOverNatsReturnsMarkdownAndPageStatuses()
    {
        var host = RequireNats();
        var agent = new DocumentAgent(new DocumentExtractorRouter(), options: new DocumentAgentOptions { MaxAttachmentBytes = DocumentAgentHosting.MaxAttachmentBytesFor(host) });
        await using var service = await StartAsync(agent, "extract");

        var remote = await DiscoverAsync("extract");
        Assert.Equal("pdf,text", remote.Info.Metadata["document_formats"]);
        Assert.Equal("extract", remote.Info.Metadata["document_modes"]);

        using var chat = remote.AsIChatClient();
        var (markdown, statuses) = await CollectAsync(chat, new ChatMessage(ChatRole.User, [new TextContent("/extract"), TestFiles.Load("fabrikam-q3-report.pdf")]));

        Assert.StartsWith("# fabrikam-q3-report.pdf", markdown);
        Assert.Contains("| North | 18,400 | $1.20M | +15% |", markdown);
        Assert.Contains("<!-- page 2 -->", markdown);
        Assert.Contains("fabrikam-q3-report.pdf: page 1/2", statuses);
        Assert.Contains("fabrikam-q3-report.pdf: page 2/2", statuses);
    }

    [Fact]
    public async Task Ask_IndexIsKeptAcrossPromptsSoAQuestionWithoutAttachmentsIsAnswered()
    {
        RequireNats();
        using var router = new DocumentExtractorRouter();
        using var kb = new DocumentKnowledgeBase(router, new HashingEmbeddingGenerator());
        var answer = new ScriptedChatClient("North revenue was $1.20M.", toolQuery: "north region revenue");
        await using var service = await StartAsync(new DocumentAgent(router, kb, answer), "ask");
        using var chat = (await DiscoverAsync("ask")).AsIChatClient();

        var (indexed, _) = await CollectAsync(chat, new ChatMessage(ChatRole.User, [new TextContent("/ingest"), TestFiles.Load("fabrikam-q3-report.pdf")]));
        Assert.Contains("fabrikam-q3-report.pdf", indexed);

        var (reply, statuses) = await CollectAsync(chat, new ChatMessage(ChatRole.User, "What was North revenue?"));
        Assert.Contains("North revenue was $1.20M.", reply);
        Assert.DoesNotContain("thinking", reply);
        Assert.Contains("searching documents: north region revenue", statuses);
        Assert.Single(kb.Documents);

        // KeepHistory=false: the answer model saw only this turn, never the earlier PDF bytes.
        Assert.DoesNotContain(answer.Requests[0].Messages, m => m.Contents.OfType<DataContent>().Any());
    }

    [Fact]
    public async Task Oversize_AttachmentIsRefusedByTheClientBeforeItReachesNats()
    {
        var host = RequireNats();
        await using var service = await StartAsync(new DocumentAgent(new DocumentExtractorRouter()), "oversize");
        using var chat = (await DiscoverAsync("oversize")).AsIChatClient();
        long maxPayload = host.ServerInfo!.MaxPayload;
        var tooBig = new DataContent(new byte[maxPayload], MediaTypes.Pdf) { Name = "huge.pdf" };

        var error = await Record.ExceptionAsync(() => CollectAsync(chat, new ChatMessage(ChatRole.User, [new TextContent("/extract"), tooBig])));

        var tooLarge = Assert.IsType<PayloadTooLargeException>(error);
        Assert.Contains("max_payload", tooLarge.Message, StringComparison.OrdinalIgnoreCase);
    }

    private NatsConnection RequireNats()
    {
        Assert.SkipWhen(_host is null, "nats-server is not on PATH (set NATS_SERVER_BIN to run the NATS end-to-end tests).");
        return _host!;
    }

    private async Task<AgentService> StartAsync(DocumentAgent agent, string name)
    {
        var service = DocumentAgentHosting.Create(_host!, agent, supportsImages: false, owner: "test", name: name);
        await service.StartAsync(Ct);
        return service;
    }

    private async Task<Agent> DiscoverAsync(string name)
    {
        var client = new AgentsClient(_caller!);
        var found = await client.DiscoverAsync(new AgentFilter { Agent = DocumentAgentHosting.AgentType, Owner = "test", Name = name }, cancellationToken: Ct);
        return Assert.Single(found);
    }

    private static async Task<(string Text, List<string> Statuses)> CollectAsync(IChatClient chat, ChatMessage message)
    {
        var text = new System.Text.StringBuilder();
        var statuses = new List<string>();
        await foreach (var update in chat.GetStreamingResponseAsync([message], cancellationToken: Ct))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case AgentStatusContent { IsAck: false } status:
                        statuses.Add(status.Status);
                        break;
                    case TextContent t:
                        text.Append(t.Text);
                        break;
                }
            }
        }

        return (text.ToString(), statuses);
    }
}
