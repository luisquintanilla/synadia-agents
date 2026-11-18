using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;

namespace Synadia.Examples.DocumentAgent.Tests;

public class ExtractionTests
{
    [Fact]
    public async Task PdfLayout_RecoversHeadingsAndTableFromFixture()
    {
        await using var stream = File.OpenRead(TestFiles.PathOf("fabrikam-q3-report.pdf"));
        var document = await new PdfLayoutExtractionClient().ExtractAsync(stream, "report.pdf", MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, document.Sections.Count);
        var markdown = ExtractionMarkdown.Render(document);
        Assert.Contains("## Fabrikam Widgets - Q3 2025 Report", markdown);
        Assert.Contains("## Regional sales", markdown);
        Assert.Contains("| Region | Units | Revenue | Growth |", markdown);
        Assert.Contains("| North | 18,400 | $1.20M | +15% |", markdown);
        Assert.Contains("## Outlook", markdown);
        Assert.True(markdown.IndexOf("## Shipping", StringComparison.Ordinal) < markdown.IndexOf("## Outlook", StringComparison.Ordinal));

        var table = Assert.Single(document.Sections[0].Elements.OfType<ExtractionDocumentTableElement>());
        Assert.Equal(5, table.Cells.RowCount);
        Assert.Equal(4, table.Cells.ColumnCount);
        Assert.Equal(1, ExtractionMarkdown.GetPageNumber(table));
    }

    [Fact]
    public async Task Bridge_MapsHeadingsTablesAndPagesToIngestionElements()
    {
        await using var stream = File.OpenRead(TestFiles.PathOf("fabrikam-q3-report.pdf"));
        var extraction = await new PdfLayoutExtractionClient().ExtractAsync(stream, "report.pdf", MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);

        var ingestion = ExtractionToIngestion.Convert(extraction, "report.pdf");
        var elements = ingestion.Sections.SelectMany(s => s.Elements).ToList();

        Assert.Equal("report.pdf", ingestion.Identifier);
        Assert.Equal(4, elements.OfType<IngestionDocumentHeader>().Count());
        var table = Assert.Single(elements.OfType<IngestionDocumentTable>());
        Assert.Contains("| West | 7,400 | $0.46M | +8% |", table.GetMarkdown());
        Assert.Equal(1, table.PageNumber);
        Assert.Equal(2, elements.Last().PageNumber);
    }

    [Fact]
    public void Renderer_EscapesPipesAndNewlinesInTableCells()
    {
        var table = new ExtractionDocumentTableElement(null, 2, 2);
        table.Cells[0, 0] = Cell("Name");
        table.Cells[0, 1] = Cell("Notes");
        table.Cells[1, 0] = Cell("a|b");
        table.Cells[1, 1] = Cell("line1\nline2");

        var markdown = ExtractionMarkdown.RenderTable(table);

        Assert.Equal("| Name | Notes |\n| --- | --- |\n| a\\|b | line1 line2 |", markdown);

        static ExtractionDocumentTableCell Cell(string text) => new(new ExtractionDocumentTextElement(new TextContent(text)));
    }

    [Fact]
    public void Renderer_UsesRolesAndDropsPageFurniture()
    {
        var section = new ExtractionDocumentSection();
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Running header"), "pageHeader"));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Title"), "heading"));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Body text."), null));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Page 1"), "pageNumber"));

        var markdown = ExtractionMarkdown.RenderSection(section);

        Assert.DoesNotContain("Running header", markdown);
        Assert.DoesNotContain("Page 1", markdown);
        Assert.Contains("## Title", markdown);
        Assert.Contains("Body text.", markdown);
    }

    [Theory]
    [InlineData("```markdown\n| a | b |\n```", "| a | b |")]
    [InlineData("```md\n# Hi\n```", "# Hi")]
    [InlineData("```\nplain\n```", "plain")]
    [InlineData("```csharp\nvar x = 1;\n```", "```csharp\nvar x = 1;\n```")]
    [InlineData("no fence", "no fence")]
    public void Unfence_RemovesOnlyAWholeMarkdownFence(string input, string expected) =>
        Assert.Equal(expected, ExtractionMarkdown.Unfence(input));

    [Theory]
    [InlineData(null, "a.pdf", MediaTypes.Pdf)]
    [InlineData("application/octet-stream", "scan.PNG", "image/png")]
    [InlineData("", "notes.md", "text/markdown")]
    [InlineData("image/jpeg", "whatever.bin", "image/jpeg")]
    [InlineData(null, "mystery", MediaTypes.OctetStream)]
    public void MediaTypes_PreferDeclaredThenExtension(string? declared, string fileName, string expected) =>
        Assert.Equal(expected, MediaTypes.Resolve(declared, fileName));

    [Fact]
    public void PayloadLimits_AccountForBase64Expansion()
    {
        long max = PayloadLimits.MaxRawAttachmentBytes();
        Assert.True(PayloadLimits.Base64Length(max) <= PayloadLimits.DefaultMaxPayloadBytes - PayloadLimits.DefaultEnvelopeReserveBytes);
        Assert.True(PayloadLimits.Base64Length(max + 3) > PayloadLimits.DefaultMaxPayloadBytes - PayloadLimits.DefaultEnvelopeReserveBytes);
        Assert.Equal(4, PayloadLimits.Base64Length(1));
        Assert.Equal(8L * 1024 * 1024 / 4 * 3 - 6 * 1024, PayloadLimits.MaxRawAttachmentBytes(8L * 1024 * 1024));
    }
}
