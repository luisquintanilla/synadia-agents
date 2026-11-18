using CommunityToolkit.DocumentExtraction.PdfPig;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// PdfPig with layout tuned for markdown output. Out of the box the provider uses PdfPig's default
/// segmenter (one block per page, no roles, no tables). This client instead:
/// <list type="number">
/// <item>segments with Docstrum so paragraphs, headings and table columns become separate blocks;</item>
/// <item>marks single-line, large or all-bold blocks as <c>heading</c>;</item>
/// <item>rebuilds side-by-side column blocks with equal line counts into an
/// <see cref="ExtractionDocumentTableElement"/>, so a table keeps its rows (critical for RAG).</item>
/// </list>
/// Heuristics only: born-digital PDFs with simple tables. No OCR (scanned PDFs → vision model).
/// </summary>
public sealed class PdfLayoutExtractionClient : DelegatingDocumentExtractionClient
{
    /// <summary>Single-line blocks at or above this font size are treated as headings.</summary>
    public const double HeadingMinPointSize = 13;

    public PdfLayoutExtractionClient()
        : base(new PdfPigDocumentExtractionClient(DocstrumBoundingBoxes.Instance, null, ResolveRole))
    {
    }

    public override async Task<ExtractionDocument> ExtractAsync(Stream document, string identifier, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await base.ExtractAsync(document, identifier, mediaType, options, cancellationToken).ConfigureAwait(false);
        foreach (var section in result.Sections)
        {
            ReconstructTables(section);
        }

        return result;
    }

    internal static string? ResolveRole(TextBlock block)
    {
        if (block.TextLines.Count != 1)
        {
            return null;
        }

        var letters = block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0 || block.Text.Length > 120)
        {
            return null;
        }

        bool large = letters.Average(l => l.PointSize) >= HeadingMinPointSize;
        bool bold = letters.All(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true);
        return large || bold ? "heading" : null;
    }

    /// <summary>
    /// Replaces runs of ≥2 adjacent body-text blocks that share a line count (≥2), start and end at the
    /// same height, and sit left-to-right, with a table whose columns are those blocks.
    /// </summary>
    internal static void ReconstructTables(ExtractionDocumentSection section)
    {
        var elements = section.Elements;
        for (int start = 0; start < elements.Count; start++)
        {
            if (!TryColumn(elements[start], out var first))
            {
                continue;
            }

            int end = start + 1;
            var columns = new List<Column> { first };
            while (end < elements.Count && TryColumn(elements[end], out var next) && Aligned(columns[^1], next, first.Lines.Length))
            {
                columns.Add(next);
                end++;
            }

            if (columns.Count < 2)
            {
                continue;
            }

            int rows = first.Lines.Length;
            var table = new ExtractionDocumentTableElement(null, rows, columns.Count);
            for (int c = 0; c < columns.Count; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    var cell = new ExtractionDocumentTableCell(
                        new ExtractionDocumentTextElement(new TextContent(columns[c].Lines[r]), null),
                        role: r == 0 ? "columnHeader" : null);
                    table.Cells[r, c] = cell;
                }

                table.Sources.AddRange(columns[c].Element.Sources);
            }

            elements.RemoveRange(start, columns.Count);
            elements.Insert(start, table);
        }
    }

    private static bool TryColumn(ExtractionDocumentElement element, out Column column)
    {
        column = default;
        if (element is not ExtractionDocumentTextElement { Role: null } text || element.Sources.Count == 0)
        {
            return false;
        }

        var lines = (ExtractionMarkdown.GetText(text.Content) ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var polygon = element.Sources[0].Polygon;
        if (lines.Length < 2 || polygon.Count == 0 || lines.Any(l => l.Length > 40))
        {
            return false;
        }

        column = new Column(text, lines, polygon.Min(p => p.X), polygon.Max(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.Y));
        return true;
    }

    private static bool Aligned(Column previous, Column next, int rows)
    {
        if (next.Lines.Length != rows || next.Left <= previous.Left)
        {
            return false;
        }

        double tolerance = (previous.Top - previous.Bottom) / rows * 0.75;
        return Math.Abs(previous.Top - next.Top) <= tolerance && Math.Abs(previous.Bottom - next.Bottom) <= tolerance;
    }

    private readonly record struct Column(ExtractionDocumentTextElement Element, string[] Lines, double Left, double Right, double Bottom, double Top);
}
