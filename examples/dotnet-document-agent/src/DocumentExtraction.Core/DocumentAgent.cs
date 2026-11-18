using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// Document extraction agent. Two modes behind one <see cref="IDocumentAgentHandler"/>:
/// <list type="bullet">
/// <item><b>Extract</b>: each attachment → provider (PdfPig / vision model / text) → markdown, streamed per page.</item>
/// <item><b>Ask</b> (docs/RAG): attachments → <see cref="DocumentKnowledgeBase"/> (MEDI chunking + MEVD
/// in-memory vectors), then a chat model answers with retrieved passages and a search tool.</item>
/// </list>
/// Mode comes from the request, a leading command (<c>/extract</c>, <c>/ask</c>, <c>/ingest</c>, <c>/reset</c>,
/// <c>/docs</c>, <c>/help</c>), or is inferred: attachments with no question → extract; otherwise ask.
/// </summary>
public sealed partial class DocumentAgent : IDocumentAgentHandler
{
    private static readonly string[] ExtractVerbs = ["extract", "convert", "markdown", "transcribe", "ocr", "parse", "read this", "text of"];

    private readonly DocumentExtractorRouter _router;
    private readonly DocumentKnowledgeBase? _knowledgeBase;
    private readonly IChatClient? _answerClient;
    private readonly DocumentAgentOptions _options;

    /// <param name="router">Extraction providers per media type.</param>
    /// <param name="knowledgeBase">Index for Ask mode; omit for an extraction-only agent.</param>
    /// <param name="answerChatClient">Chat model for Ask mode; wrapped with function invocation for the search tool.</param>
    public DocumentAgent(DocumentExtractorRouter router, DocumentKnowledgeBase? knowledgeBase = null, IChatClient? answerChatClient = null, DocumentAgentOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _knowledgeBase = knowledgeBase;
        _options = options ?? new DocumentAgentOptions();
        _answerClient = answerChatClient is null ? null : new ChatClientBuilder(answerChatClient).UseFunctionInvocation().Build();
    }

    public bool SupportsQuestions => _knowledgeBase is not null && _answerClient is not null;

    public static string HelpText =>
        """
        **Document extraction agent**

        - Attach PDFs, images or text files with no question (or `/extract`) → markdown of each file (text + tables).
        - Attach files and ask a question (or `/ask <question>`) → files are indexed, then answered with citations.
        - `/ingest` + files → index only. `/docs` → list indexed files. `/reset` → forget them.
        """;

    public async IAsyncEnumerable<ChatResponseUpdate> HandleAsync(DocumentAgentRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, prompt, command) = Resolve(request);

        switch (command)
        {
            case "help":
                yield return Text(HelpText);
                yield break;
            case "docs":
                yield return Text(DescribeDocuments());
                yield break;
        }

        IAsyncEnumerable<ChatResponseUpdate> work = mode switch
        {
            DocumentAgentMode.Extract => ExtractAsync(request.Attachments, cancellationToken),
            DocumentAgentMode.Ingest => IngestAllAsync(request.Attachments, summarize: true, cancellationToken),
            DocumentAgentMode.Reset => ResetAsync(cancellationToken),
            _ => AskAsync(prompt, request.Attachments, cancellationToken),
        };

        await foreach (var update in work.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Splits off a leading command and infers the mode for <see cref="DocumentAgentMode.Auto"/>.</summary>
    internal static (DocumentAgentMode Mode, string Prompt, string? Command) Resolve(DocumentAgentRequest request)
    {
        var prompt = (request.Prompt ?? string.Empty).Trim();
        string? command = null;
        if (prompt.StartsWith('/'))
        {
            int end = prompt.IndexOfAny([' ', '\n', '\r', '\t']);
            command = (end < 0 ? prompt[1..] : prompt[1..end]).ToLowerInvariant();
            prompt = end < 0 ? string.Empty : prompt[end..].Trim();
        }

        var mode = request.Mode;
        if (command is not null)
        {
            mode = command switch
            {
                "extract" => DocumentAgentMode.Extract,
                "ask" => DocumentAgentMode.Ask,
                "ingest" or "index" => DocumentAgentMode.Ingest,
                "reset" or "clear" => DocumentAgentMode.Reset,
                _ => mode,
            };
            if (command is not ("help" or "docs" or "extract" or "ask" or "ingest" or "index" or "reset" or "clear"))
            {
                command = "help";
            }
        }

        if (mode == DocumentAgentMode.Auto)
        {
            bool hasAttachments = request.Attachments.Count > 0;
            bool wantsExtraction = prompt.Length == 0 || ExtractVerbs.Any(v => prompt.Contains(v, StringComparison.OrdinalIgnoreCase));
            mode = hasAttachments && wantsExtraction ? DocumentAgentMode.Extract : DocumentAgentMode.Ask;
        }

        return (mode, prompt, command);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ExtractAsync(IReadOnlyList<DataContent> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            yield return Text("Attach one or more PDF, image or text files to extract.");
            yield break;
        }

        for (int i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            var name = NameOf(attachment, i);
            var mediaType = MediaTypes.Resolve(attachment.MediaType, name);

            if (i > 0)
            {
                yield return Text("\n\n");
            }

            yield return Text($"# {name}\n\n");

            if (Reject(attachment, name, mediaType) is { } rejection)
            {
                yield return Text($"> ⚠️ {rejection}\n");
                continue;
            }

            string extractor = _router.Describe(mediaType);
            yield return Status($"extracting {name} ({PayloadLimits.Format(attachment.Data.Length)}) with {extractor}", name);

            var (document, error) = await TryExtractAsync(attachment, name, mediaType, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                yield return Text($"> ⚠️ Could not extract {name}: {error}\n");
                continue;
            }

            int pages = document.Sections.Count;
            bool any = false;
            if (pages == 0)
            {
                var md = ExtractionMarkdown.Render(document);
                any = md.Length > 0;
                if (any)
                {
                    yield return Text(md + "\n");
                }
            }

            for (int p = 0; p < pages; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Status($"{name}: page {p + 1}/{pages}", name, p + 1, pages);
                var md = ExtractionMarkdown.RenderSection(document.Sections[p]);
                if (md.Length == 0)
                {
                    continue;
                }

                yield return Text((any ? "\n\n" : string.Empty) + (pages > 1 ? $"<!-- page {p + 1} -->\n" : string.Empty) + md + "\n");
                any = true;
            }

            if (!any)
            {
                yield return Text(MediaTypes.IsPdf(mediaType)
                    ? "> ⚠️ No text layer found (scanned PDF?). PdfPig does not OCR; send the pages as images for vision-model extraction.\n"
                    : "> ⚠️ No text found.\n");
            }
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> IngestAllAsync(IReadOnlyList<DataContent> attachments, bool summarize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_knowledgeBase is null)
        {
            yield return Text("> ⚠️ This agent has no document index configured; use `/extract` instead.\n");
            yield break;
        }

        if (attachments.Count == 0 && summarize)
        {
            yield return Text("Attach one or more files to index.");
            yield break;
        }

        var lines = new StringBuilder();
        for (int i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            var name = NameOf(attachment, i);
            var mediaType = MediaTypes.Resolve(attachment.MediaType, name);
            if (Reject(attachment, name, mediaType) is { } rejection)
            {
                lines.Append("- ⚠️ ").Append(rejection).Append('\n');
                continue;
            }

            yield return Status($"indexing {name} with {_router.Describe(mediaType)}", name);
            var (ingested, error) = await TryIngestAsync(attachment, name, mediaType, cancellationToken).ConfigureAwait(false);
            if (ingested is null)
            {
                lines.Append("- ⚠️ Could not index ").Append(name).Append(": ").Append(error).Append('\n');
                continue;
            }

            yield return Status($"indexed {name}: {ingested.Pages} page(s), {ingested.Chunks} chunk(s)", name);
            lines.Append("- ").Append(name).Append(": ").Append(ingested.Pages).Append(" page(s), ").Append(ingested.Chunks).Append(" chunk(s)\n");
        }

        // Problems are always reported; successes only when the caller wants a summary.
        if (lines.Length > 0 && (summarize || lines.ToString().Contains('⚠')))
        {
            yield return Text((summarize ? "Indexed:\n" : string.Empty) + lines + (summarize ? string.Empty : "\n"));
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> AskAsync(string question, IReadOnlyList<DataContent> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!SupportsQuestions)
        {
            yield return Text(attachments.Count > 0
                ? "This agent is configured for extraction only; send files without a question (or `/extract`)."
                : HelpText);
            yield break;
        }

        await foreach (var update in IngestAllAsync(attachments, summarize: question.Length == 0, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        if (question.Length == 0)
        {
            yield return Text(attachments.Count > 0 ? "\nAsk me a question about these documents." : HelpText);
            yield break;
        }

        if (_knowledgeBase!.Documents.Count == 0)
        {
            yield return Text("No documents are indexed yet. Attach files with your question (or `/ingest` them first).");
            yield break;
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, _options.AnswerSystemPrompt) };
        IReadOnlyList<SearchHit> hits = [];
        if (_options.PrefetchPassages)
        {
            yield return Status("searching documents");
            hits = await _knowledgeBase.SearchAsync(question, cancellationToken: cancellationToken).ConfigureAwait(false);
            messages.Add(new(ChatRole.User, $"Passages from the shared documents:\n\n{DocumentKnowledgeBase.FormatHits(hits)}\n\nQuestion: {question}"));
        }
        else
        {
            messages.Add(new(ChatRole.User, question));
        }

        var chatOptions = _options.AnswerChatOptions?.Clone() ?? new ChatOptions();
        if (_options.UseSearchTool)
        {
            chatOptions.Tools = [.. chatOptions.Tools ?? [], _knowledgeBase.SearchTool];
        }

        yield return Status("answering");
        var pending = new StringBuilder();
        await foreach (var update in _answerClient!.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        var query = call.Arguments?.TryGetValue("query", out var q) == true ? q?.ToString() : null;
                        yield return Status(query is null ? "searching documents" : $"searching documents: {query}");
                        break;
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        // Stream whole lines so invented citation links can be scrubbed before they leave.
                        pending.Append(text.Text);
                        int newline = pending.ToString().LastIndexOf('\n');
                        if (newline >= 0)
                        {
                            yield return Text(ScrubCitationLinks(pending.ToString(0, newline + 1)));
                            pending.Remove(0, newline + 1);
                        }

                        break;

                    // TextReasoningContent (thinking), FunctionResultContent, usage: not user-facing.
                }
            }
        }

        if (pending.Length > 0)
        {
            yield return Text(ScrubCitationLinks(pending.ToString()));
        }

        var sources = hits.Select(h => h.DocumentId).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length > 0)
        {
            yield return Text($"\n\n_Sources searched: {string.Join(", ", sources)}_");
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ResetAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_knowledgeBase is not null)
        {
            await _knowledgeBase.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        yield return Text("Forgot all indexed documents.");
    }

    /// <summary>
    /// Replaces markdown links that don't point at an http(s) URL with their label. Small models like to dress
    /// file-name citations up as <c>[report.pdf](file://...)</c> links to places that don't exist.
    /// </summary>
    public static string ScrubCitationLinks(string markdown) => NonWebLink().Replace(markdown, "$1");

    [GeneratedRegex(@"\[([^\]\n]+)\]\((?!https?://)[^)\s]*\)")]
    private static partial Regex NonWebLink();

    private string DescribeDocuments()
    {
        var docs = _knowledgeBase?.Documents ?? [];
        if (docs.Count == 0)
        {
            return "No documents are indexed.";
        }

        var sb = new StringBuilder("Indexed documents:\n");
        foreach (var d in docs.OrderBy(d => d.Identifier, StringComparer.Ordinal))
        {
            sb.Append("- ").Append(d.Identifier).Append(" (").Append(d.MediaType).Append(", ").Append(d.Pages).Append(" page(s), ")
              .Append(d.Chunks).Append(" chunk(s), via ").Append(d.Extractor).Append(")\n");
        }

        return sb.ToString().TrimEnd();
    }

    private string? Reject(DataContent attachment, string name, string mediaType)
    {
        long size = attachment.Data.Length;
        if (size > _options.MaxAttachmentBytes)
        {
            return $"{name} is {PayloadLimits.Format(size)}, over the {PayloadLimits.Format(_options.MaxAttachmentBytes)} attachment limit " +
                   $"(base64 inside the NATS max_payload). Split the file, or send it by reference (see FINDINGS.md: Object Store attachments).";
        }

        if (size == 0)
        {
            return $"{name} is empty.";
        }

        return _router.CanExtract(mediaType, out var reason) ? null : $"{name}: {reason}.";
    }

    private async Task<(ExtractionDocument? Document, string? Error)> TryExtractAsync(DataContent attachment, string name, string mediaType, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(attachment.Data.ToArray(), writable: false);
            var document = await _router.GetClient(mediaType).ExtractAsync(stream, name, mediaType, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (document, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(IngestedDocument? Document, string? Error)> TryIngestAsync(DataContent attachment, string name, string mediaType, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(attachment.Data.ToArray(), writable: false);
            return (await _knowledgeBase!.IngestAsync(stream, name, mediaType, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private static string NameOf(DataContent attachment, int index) =>
        string.IsNullOrWhiteSpace(attachment.Name) ? $"attachment-{index + 1}" : Path.GetFileName(attachment.Name);

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Status(string message, string? attachment = null, int? page = null, int? pageCount = null) =>
        new DocumentAgentStatus(message, attachment, page, pageCount).ToUpdate();
}
