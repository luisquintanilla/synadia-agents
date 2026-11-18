using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Synadia.Examples.DocumentAgent.Harness;

/// <summary>
/// Generates the small, license-clean fixtures committed under <c>fixtures/</c>. All content is invented
/// ("Fabrikam Widgets" is a Microsoft fictitious company name) and rendered with standard-14 / default fonts.
/// </summary>
internal static class Fixtures
{
    public const string ReportPdf = "fabrikam-q3-report.pdf";
    public const string InvoicePng = "invoice-1042.png";
    public const string NotesText = "support-notes.md";

    public static IReadOnlyList<string> Generate(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>
        {
            Write(directory, ReportPdf, BuildReportPdf()),
            Write(directory, InvoicePng, BuildInvoicePng()),
            Write(directory, NotesText, System.Text.Encoding.UTF8.GetBytes(SupportNotes)),
        };
        return written;
    }

    private static string Write(string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal const string SupportNotes =
        """
        # Fabrikam support notes

        ## Warranty
        Widget Pro units carry a 3-year limited warranty. Widget Lite units carry a 1-year warranty.

        ## Returns
        Unopened items can be returned within 30 days for a full refund. Opened items incur a 15% restocking fee.

        ## Escalation
        Priority-1 incidents page the on-call engineer within 15 minutes, 24x7.
        """;

    /// <summary>Two-page report: headings, prose, and a column-aligned table (PdfPig sees it as text lines).</summary>
    internal static byte[] BuildReportPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "Fabrikam Widgets Q3 2025 Report";
        builder.DocumentInformation.Producer = "synadia-agents document-agent fixture generator";
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        // Page 1
        var page = builder.AddPage(PageSize.A4);
        double y = 780;
        page.AddText("Fabrikam Widgets - Q3 2025 Report", 20, new PdfPoint(60, y), bold);
        y -= 40;
        y = Paragraph(page, regular, y,
            "Fabrikam Widgets sold 48,200 units in Q3 2025, up 12% from Q2.",
            "Revenue reached $3.1 million. The Widget Pro line drove most of the growth,",
            "while Widget Lite held steady in the education market.");
        y -= 20;
        page.AddText("Regional sales", 14, new PdfPoint(60, y), bold);
        y -= 24;
        string[][] rows =
        [
            ["Region", "Units", "Revenue", "Growth"],
            ["North", "18,400", "$1.20M", "+15%"],
            ["South", "12,100", "$0.78M", "+9%"],
            ["East", "10,300", "$0.66M", "+11%"],
            ["West", "7,400", "$0.46M", "+8%"],
        ];
        double[] columns = [60, 180, 290, 400];
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                page.AddText(row[c], 11, new PdfPoint(columns[c], y), row == rows[0] ? bold : regular);
            }

            y -= 18;
        }

        // Page 2
        page = builder.AddPage(PageSize.A4);
        y = 780;
        page.AddText("Shipping and returns policy", 16, new PdfPoint(60, y), bold);
        y -= 32;
        y = Paragraph(page, regular, y,
            "Orders over $50 ship free within the continental US.",
            "Standard shipping takes 3 to 5 business days; express shipping takes 1 business day.",
            "International orders ship from the Rotterdam warehouse.");
        y -= 20;
        page.AddText("Outlook", 16, new PdfPoint(60, y), bold);
        y -= 32;
        Paragraph(page, regular, y,
            "For Q4 2025 Fabrikam expects 55,000 units, driven by the holiday season",
            "and the launch of Widget Pro 2 on November 3.");

        return builder.Build();
    }

    private static double Paragraph(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, double y, params string[] lines)
    {
        foreach (var line in lines)
        {
            page.AddText(line, 11, new PdfPoint(60, y), font);
            y -= 16;
        }

        return y;
    }

    /// <summary>A small invoice image with a ruled table, for the vision-model extraction path.</summary>
    internal static byte[] BuildInvoicePng()
    {
        const int width = 720, height = 520;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        using var boldTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? typeface;
        using var title = new SKFont(boldTypeface, 30);
        using var head = new SKFont(boldTypeface, 18);
        using var body = new SKFont(typeface, 18);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var rule = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, IsAntialias = true };

        canvas.DrawText("Fabrikam Widgets - Invoice INV-1042", 30, 55, SKTextAlign.Left, title, ink);
        canvas.DrawText("Date: 2025-10-14    Customer: Tailspin Toys", 30, 95, SKTextAlign.Left, body, ink);

        string[][] rows =
        [
            ["Item", "Qty", "Unit price", "Amount"],
            ["Widget Pro", "4", "$120.00", "$480.00"],
            ["Widget Lite", "10", "$35.00", "$350.00"],
            ["Mounting kit", "4", "$12.50", "$50.00"],
        ];
        float[] columns = [30, 300, 400, 560];
        float top = 130, rowHeight = 44;
        for (int r = 0; r < rows.Length; r++)
        {
            float baseline = top + (r * rowHeight) + 29;
            for (int c = 0; c < rows[r].Length; c++)
            {
                canvas.DrawText(rows[r][c], columns[c] + 8, baseline, SKTextAlign.Left, r == 0 ? head : body, ink);
            }

            canvas.DrawLine(30, top + (r * rowHeight), 690, top + (r * rowHeight), rule);
        }

        float bottom = top + (rows.Length * rowHeight);
        canvas.DrawLine(30, bottom, 690, bottom, rule);
        foreach (var x in columns.Append(690f))
        {
            canvas.DrawLine(x, top, x, bottom, rule);
        }

        canvas.DrawText("Subtotal: $880.00", 470, bottom + 45, SKTextAlign.Left, body, ink);
        canvas.DrawText("Tax (8%): $70.40", 470, bottom + 75, SKTextAlign.Left, body, ink);
        canvas.DrawText("Total due: $950.40", 470, bottom + 110, SKTextAlign.Left, head, ink);
        canvas.DrawText("Payment terms: net 30", 30, bottom + 110, SKTextAlign.Left, body, ink);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
