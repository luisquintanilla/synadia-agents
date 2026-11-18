using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// A MEDI <see cref="IngestionDocumentReader"/> backed by any <see cref="DocumentExtractionClient"/>,
/// so PdfPig / VLM / Azure / Mistral extraction plugs straight into an <see cref="IngestionPipeline{T}"/>.
/// </summary>
public sealed class ExtractionDocumentReader : IngestionDocumentReader
{
    private readonly Func<string, DocumentExtractionClient> _selectClient;

    /// <summary>Uses one extraction client for every media type.</summary>
    public ExtractionDocumentReader(DocumentExtractionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _selectClient = _ => client;
    }

    /// <summary>Routes each media type to an extraction client (see <see cref="DocumentExtractorRouter"/>).</summary>
    public ExtractionDocumentReader(DocumentExtractorRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _selectClient = router.GetClient;
    }

    /// <summary>The last extraction result, for callers that also want the raw extraction graph.</summary>
    public ExtractionDocument? LastExtraction { get; private set; }

    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        var client = _selectClient(mediaType);
        var extraction = await client.ExtractAsync(source, identifier, mediaType, cancellationToken: cancellationToken).ConfigureAwait(false);
        LastExtraction = extraction;
        return ExtractionToIngestion.Convert(extraction, identifier);
    }
}
