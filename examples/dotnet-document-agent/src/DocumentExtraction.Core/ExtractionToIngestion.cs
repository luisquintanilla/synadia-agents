using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// Maps the extraction graph (<c>Microsoft.Extensions.Documents.ExtractionDocument</c>, produced
/// by any <c>DocumentExtractionClient</c>) to the ingestion graph
/// (<c>Microsoft.Extensions.DataIngestion.IngestionDocument</c>, consumed by MEDI chunkers).
/// </summary>
/// <remarks>
/// As of <c>11.0.0-preview.1.26560.3</c> no such bridge ships in either package family, so this
/// adapter is the glue. Mapping rules:
/// <list type="bullet">
/// <item>Each extraction section → one <see cref="IngestionDocumentSection"/> (nested sections recurse).</item>
/// <item>Text with role <c>heading</c> → <see cref="IngestionDocumentHeader"/> (keeps <c>HeadingLevel</c>), so <see cref="HeaderChunker"/> sees structure.</item>
/// <item>Text with role <c>pageFooter</c> → <see cref="IngestionDocumentFooter"/>; <c>pageHeader</c> is dropped (running header noise).</item>
/// <item>Other text → <see cref="IngestionDocumentParagraph"/>.</item>
/// <item>Tables → <see cref="IngestionDocumentTable"/> with GFM Markdown plus a mapped cell grid.</item>
/// <item>Images → <see cref="IngestionDocumentImage"/> with the alt text / caption as Markdown.</item>
/// <item>The first source page number becomes <see cref="IngestionDocumentElement.PageNumber"/>.</item>
/// </list>
/// Empty elements are skipped because MEDI elements require non-empty Markdown.
/// </remarks>
public static class ExtractionToIngestion
{
    public static IngestionDocument Convert(ExtractionDocument extraction, string? identifier = null)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var document = new IngestionDocument(identifier ?? extraction.Identifier);

        foreach (var section in extraction.Sections)
        {
            var mapped = ConvertSection(section);
            if (mapped.Elements.Count > 0)
            {
                document.Sections.Add(mapped);
            }
        }

        // Providers that return only document-level content (no sections) still produce one section.
        if (document.Sections.Count == 0 && ExtractionMarkdown.GetText(extraction.Content) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            var section = new IngestionDocumentSection();
            section.Elements.Add(new IngestionDocumentParagraph(text.Trim()));
            document.Sections.Add(section);
        }

        return document;
    }

    private static IngestionDocumentSection ConvertSection(ExtractionDocumentSection section)
    {
        var mapped = new IngestionDocumentSection { PageNumber = ExtractionMarkdown.GetPageNumber(section) };
        foreach (var element in section.Elements)
        {
            if (ConvertElement(element) is { } converted)
            {
                converted.PageNumber ??= ExtractionMarkdown.GetPageNumber(element) ?? mapped.PageNumber;
                mapped.Elements.Add(converted);
            }
        }

        return mapped;
    }

    internal static IngestionDocumentElement? ConvertElement(ExtractionDocumentElement element)
    {
        switch (element)
        {
            case ExtractionDocumentSection nested:
                var s = ConvertSection(nested);
                return s.Elements.Count > 0 ? s : null;

            case ExtractionDocumentTableElement table:
                var markdown = ExtractionMarkdown.RenderTable(table);
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    return null;
                }

                var cells = new IngestionDocumentElement?[table.Cells.RowCount, table.Cells.ColumnCount];
                for (int r = 0; r < table.Cells.RowCount; r++)
                {
                    for (int c = 0; c < table.Cells.ColumnCount; c++)
                    {
                        if (table.Cells[r, c]?.Content is { } content)
                        {
                            cells[r, c] = ConvertElement(content);
                        }
                    }
                }

                return new IngestionDocumentTable(markdown, cells!);

            case ExtractionDocumentImageElement image:
                var alt = image.AlternativeText ?? image.Text;
                if (string.IsNullOrWhiteSpace(alt))
                {
                    return null;
                }

                var img = new IngestionDocumentImage(ExtractionMarkdown.RenderElement(image)!) { AlternativeText = alt };
                if (image.Content is Microsoft.Extensions.AI.DataContent data)
                {
                    img.Content = data.Data;
                    img.MediaType = data.MediaType;
                }

                return img;

            case ExtractionDocumentTextElement text:
                var value = ExtractionMarkdown.Unfence(ExtractionMarkdown.GetText(text.Content)?.Trim());
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                return text.Role switch
                {
                    "heading" => new IngestionDocumentHeader(ExtractionMarkdown.RenderElement(text)!) { Level = text.HeadingLevel ?? 2 },
                    "pageFooter" => new IngestionDocumentFooter(value),
                    "pageHeader" or "pageNumber" => null,
                    _ => new IngestionDocumentParagraph(value),
                };

            default:
                var other = ExtractionMarkdown.GetText(element.Content)?.Trim();
                return string.IsNullOrEmpty(other) ? null : new IngestionDocumentParagraph(other);
        }
    }
}
