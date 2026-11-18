using System.ComponentModel;
using System.Text;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

namespace Synadia.Examples.DocumentAgent;

public sealed class DocumentKnowledgeBaseOptions
{
    public string CollectionName { get; set; } = "documents";

    /// <summary>Chunk budget. Small chunks suit small local models (qwen3.5:4b) and short fixtures.</summary>
    public int MaxTokensPerChunk { get; set; } = 400;

    public int OverlapTokens { get; set; } = 40;

    /// <summary>Tokenizer used for chunk sizing. Defaults to the o200k (gpt-4o) BPE.</summary>
    public Tokenizer? Tokenizer { get; set; }

    /// <summary>Default number of chunks returned per search.</summary>
    public int TopK { get; set; } = 4;
}

public sealed record IngestedDocument(string Identifier, string MediaType, int Pages, int Chunks, string Extractor);

public sealed record SearchHit(string DocumentId, string Content, string? Context, double? Score);

/// <summary>
/// In-memory RAG index: any extractable attachment → <see cref="ExtractionDocumentReader"/> (the
/// ExtractionDocument → IngestionDocument bridge) → MEDI <see cref="HeaderChunker"/> → MEDI
/// <see cref="VectorStoreWriter{T}"/> → MEVD <see cref="InMemoryVectorStore"/> embedding with the supplied
/// generator (e.g. Ollama <c>embeddinggemma</c>).
/// </summary>
/// <remarks>
/// MEDI's <see cref="IngestionPipeline{T}"/> only accepts <see cref="FileInfo"/>/<see cref="DirectoryInfo"/>
/// inputs, but NATS attachments arrive as in-memory bytes, so the reader → chunker → writer stages are
/// composed directly here. Re-ingesting the same identifier replaces its previous chunks
/// (<see cref="VectorStoreWriterOptions.IncrementalIngestion"/>).
/// </remarks>
public sealed class DocumentKnowledgeBase : IDisposable
{
    private readonly DocumentExtractorRouter _router;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly DocumentKnowledgeBaseOptions _options;
    private readonly HeaderChunker _chunker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IngestedDocument> _documents = new(StringComparer.Ordinal);
    private VectorStoreWriter<string>? _writer;

    public DocumentKnowledgeBase(DocumentExtractorRouter router, IEmbeddingGenerator<string, Embedding<float>> embeddings, DocumentKnowledgeBaseOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options ?? new DocumentKnowledgeBaseOptions();
        var tokenizer = _options.Tokenizer ?? TiktokenTokenizer.CreateForModel("gpt-4o");
        _chunker = new HeaderChunker(new IngestionChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = _options.MaxTokensPerChunk,
            OverlapTokens = _options.OverlapTokens,
        });

        SearchTool = AIFunctionFactory.Create(SearchForToolAsync, new AIFunctionFactoryOptions
        {
            Name = "search_documents",
            Description = "Searches the documents the user has shared and returns the most relevant passages with their source file names.",
        });
    }

    /// <summary>Documents currently indexed, keyed by identifier (the attachment filename).</summary>
    public IReadOnlyCollection<IngestedDocument> Documents
    {
        get
        {
            lock (_documents)
            {
                return _documents.Values.ToArray();
            }
        }
    }

    /// <summary>A <c>search_documents(query)</c> tool for a function-invoking chat client.</summary>
    public AIFunction SearchTool { get; }

    public async Task<IngestedDocument> IngestAsync(Stream content, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var reader = new ExtractionDocumentReader(_router);
        var document = await reader.ReadAsync(content, identifier, mediaType, cancellationToken).ConfigureAwait(false);
        int pages = reader.LastExtraction?.Sections.Count ?? document.Sections.Count;

        if (!document.EnumerateContent().Any())
        {
            throw new InvalidOperationException($"no text could be extracted from '{identifier}'");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = await GetWriterAsync(cancellationToken).ConfigureAwait(false);
            int chunks = 0;
            await writer.WriteAsync(Count(_chunker.ProcessAsync(document, cancellationToken), () => chunks++), cancellationToken).ConfigureAwait(false);

            var ingested = new IngestedDocument(identifier, mediaType, pages, chunks, _router.Describe(mediaType));
            lock (_documents)
            {
                _documents[identifier] = ingested;
            }

            return ingested;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int? top = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var writer = _writer;
        if (writer is null)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        await foreach (var result in writer.VectorStoreCollection.SearchAsync(query, top ?? _options.TopK, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var r = result.Record;
            hits.Add(new SearchHit(
                r.TryGetValue("documentid", out var id) ? id?.ToString() ?? "?" : "?",
                r.TryGetValue("content", out var c) ? c?.ToString() ?? string.Empty : string.Empty,
                r.TryGetValue("context", out var ctx) ? ctx?.ToString() : null,
                result.Score));
        }

        return hits;
    }

    /// <summary>Formats hits as numbered passages for a model prompt or tool result.</summary>
    public static string FormatHits(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            return "No matching passages.";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] source: ").Append(hits[i].DocumentId).Append('\n')
              .Append(hits[i].Content.Trim()).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Drops all indexed documents.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _writer?.Dispose(); // also disposes its vector store
            _writer = null;
            lock (_documents)
            {
                _documents.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _gate.Dispose();
    }

    [Description("Searches the shared documents.")]
    private async Task<string> SearchForToolAsync(
        [Description("What to look for, phrased as a search query.")] string query,
        CancellationToken cancellationToken)
        => FormatHits(await SearchAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false));

    private async Task<VectorStoreWriter<string>> GetWriterAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            return _writer;
        }

        // Probe once for the embedding dimension so any embedding model works without configuration.
        var probe = await _embeddings.GenerateAsync(["dimension probe"], cancellationToken: cancellationToken).ConfigureAwait(false);
        int dimensions = probe[0].Vector.Length;

        var store = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = _embeddings });
        _writer = new VectorStoreWriter<string>(store, dimensions, new VectorStoreWriterOptions
        {
            CollectionName = _options.CollectionName,
            IncrementalIngestion = true,
        });
        return _writer;
    }

    private static async IAsyncEnumerable<T> Count<T>(IAsyncEnumerable<T> source, Action onItem)
    {
        await foreach (var item in source.ConfigureAwait(false))
        {
            onItem();
            yield return item;
        }
    }
}
