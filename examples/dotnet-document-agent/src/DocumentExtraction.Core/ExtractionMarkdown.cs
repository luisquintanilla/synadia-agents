using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent;

/// <summary>Renders a provider-neutral <see cref="ExtractionDocument"/> as GitHub-flavored Markdown.</summary>
public static class ExtractionMarkdown
{
    /// <summary>Renders the whole document; sections are separated by a blank line.</summary>
    public static string Render(ExtractionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Sections.Count == 0)
        {
            return GetText(document.Content)?.Trim() ?? string.Empty;
        }

        return string.Join("\n\n", document.Sections.Select(RenderSection).Where(s => s.Length > 0));
    }

    /// <summary>Renders one section (PdfPig and VLM providers emit one section per page).</summary>
    public static string RenderSection(ExtractionDocumentSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var parts = new List<string>();
        foreach (var element in section.Elements)
        {
            var md = RenderElement(element);
            if (!string.IsNullOrWhiteSpace(md))
            {
                parts.Add(md.Trim());
            }
        }

        if (parts.Count == 0)
        {
            return GetText(section.Content)?.Trim() ?? string.Empty;
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Renders one element, or <see langword="null"/> when it carries no content.</summary>
    public static string? RenderElement(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentSection nested => RenderSection(nested),
        ExtractionDocumentTableElement table => RenderTable(table),
        ExtractionDocumentImageElement image => RenderImage(image),
        ExtractionDocumentTextElement text => RenderText(text),
        _ => GetText(element.Content),
    };

    /// <summary>
    /// Renders a table as a GFM pipe table from its addressed cells, falling back to the
    /// provider's table-local Markdown when no cell grid is available.
    /// </summary>
    public static string? RenderTable(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        if (cells.RowCount == 0 || cells.ColumnCount == 0)
        {
            return GetText(table.Content);
        }

        var sb = new StringBuilder();
        for (int r = 0; r < cells.RowCount; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cells.ColumnCount; c++)
            {
                var cell = cells[r, c];
                var text = cell?.Content is { } content ? RenderElement(content) : null;
                sb.Append(' ').Append(EscapeCell(text)).Append(" |");
            }

            sb.Append('\n');
            if (r == 0)
            {
                sb.Append('|');
                for (int c = 0; c < cells.ColumnCount; c++)
                {
                    sb.Append(" --- |");
                }

                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>First page number referenced by an element's sources, if any.</summary>
    public static int? GetPageNumber(ExtractionDocumentElement element) =>
        element.Sources.Select(s => s.Page?.PageNumber).FirstOrDefault(p => p is not null);

    internal static string? GetText(AIContent? content) => content switch
    {
        TextContent t => t.Text,
        null => null,
        _ => content.ToString(),
    };

    private static string? RenderText(ExtractionDocumentTextElement text)
    {
        var value = Unfence(GetText(text.Content)?.Trim());
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return text.Role switch
        {
            "heading" => $"{new string('#', Math.Clamp(text.HeadingLevel ?? 2, 1, 6))} {CollapseLines(value)}",
            "pageHeader" or "pageFooter" or "pageNumber" => null, // running headers/footers are noise in extracted text
            _ => value,
        };
    }

    /// <summary>
    /// Vision models often wrap their whole answer in a <c>```markdown</c> fence; unwrap it so the
    /// result is markdown rather than a code block. Other fenced content (real code) is left alone.
    /// </summary>
    internal static string? Unfence(string? text)
    {
        if (text is null || !text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal) || text.Length < 6)
        {
            return text;
        }

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        var info = text[3..firstNewline].Trim();
        if (info.Length != 0 && !info.Equals("markdown", StringComparison.OrdinalIgnoreCase) && !info.Equals("md", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var inner = text[(firstNewline + 1)..^3];
        return inner.Contains("```", StringComparison.Ordinal) ? text : inner.Trim();
    }

    private static string? RenderImage(ExtractionDocumentImageElement image)
    {
        var description = image.AlternativeText ?? image.Text;
        return string.IsNullOrWhiteSpace(description) ? null : $"> _Figure:_ {CollapseLines(description)}";
    }

    private static string EscapeCell(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : CollapseLines(text).Replace("|", "\\|", StringComparison.Ordinal);

    private static string CollapseLines(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
