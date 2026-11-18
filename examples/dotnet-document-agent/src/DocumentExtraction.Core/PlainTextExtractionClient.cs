using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent;

/// <summary>Trivial extractor for <c>text/*</c> attachments: the whole file becomes one text element.</summary>
public sealed class PlainTextExtractionClient : DocumentExtractionClient
{
    public override async Task<ExtractionDocument> ExtractAsync(Stream source, string identifier, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!MediaTypes.IsText(mediaType))
        {
            throw new NotSupportedException($"PlainTextExtractionClient supports text/* only, not '{mediaType}'.");
        }

        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var document = new ExtractionDocument(identifier);
        var section = new ExtractionDocumentSection();
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(text)));
        document.Sections.Add(section);
        return document;
    }
}
