using CommunityToolkit.DocumentExtraction.PdfPig;
using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// Picks a <see cref="DocumentExtractionClient"/> per media type:
/// PDFs → PdfPig (local, no model; see <see cref="PdfLayoutExtractionClient"/>), images → vision language model over any <see cref="IChatClient"/>,
/// text → passthrough. Because every provider returns the same <c>ExtractionDocument</c> graph,
/// swapping in Azure Document Intelligence / Mistral is a one-line change here.
/// </summary>
public sealed class DocumentExtractorRouter : IDisposable
{
    private readonly DocumentExtractionClient _pdf;
    private readonly DocumentExtractionClient? _vision;
    private readonly DocumentExtractionClient _text = new PlainTextExtractionClient();
    private readonly bool _ownsClients;

    /// <param name="visionChatClient">
    /// Optional vision-capable chat client (e.g. Ollama <c>qwen3.5:4b</c>). Without it, images are rejected.
    /// The client is borrowed, not disposed.
    /// </param>
    /// <param name="usePdfVision">
    /// Route PDFs to the VLM instead of PdfPig. Only works when the adapter, API and model accept
    /// <c>application/pdf</c> file inputs (Ollama does not; hosted OpenAI/Gemini-class APIs do).
    /// </param>
    public DocumentExtractorRouter(IChatClient? visionChatClient = null, bool usePdfVision = false, string? visionPrompt = null)
    {
        if (visionChatClient is not null)
        {
            var options = new VisionLanguageModelDocumentExtractionClientOptions();
            if (visionPrompt is not null)
            {
                options.Prompt = visionPrompt;
            }

            _vision = new VisionLanguageModelDocumentExtractionClient(visionChatClient, options);
        }

        _pdf = usePdfVision && _vision is not null ? _vision : new PdfLayoutExtractionClient();
        _ownsClients = true;
    }

    /// <summary>Explicit clients, e.g. for tests or other providers. Clients are not disposed.</summary>
    public DocumentExtractorRouter(DocumentExtractionClient pdf, DocumentExtractionClient? vision)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        _vision = vision;
    }

    public bool SupportsImages => _vision is not null;

    /// <summary>Returns whether <paramref name="mediaType"/> can be extracted, with a reason when it cannot.</summary>
    public bool CanExtract(string mediaType, out string? reason)
    {
        reason = null;
        if (MediaTypes.IsPdf(mediaType) || MediaTypes.IsText(mediaType))
        {
            return true;
        }

        if (MediaTypes.IsImage(mediaType))
        {
            if (_vision is not null)
            {
                return true;
            }

            reason = "image extraction needs a vision model, and none is configured";
            return false;
        }

        reason = $"unsupported media type '{mediaType}' (send PDF, image or text files)";
        return false;
    }

    public DocumentExtractionClient GetClient(string mediaType)
    {
        if (!CanExtract(mediaType, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        if (MediaTypes.IsPdf(mediaType))
        {
            return _pdf;
        }

        return MediaTypes.IsText(mediaType) ? _text : _vision!;
    }

    /// <summary>Short provider label for status messages.</summary>
    public string Describe(string mediaType) => GetClient(mediaType) switch
    {
        PdfLayoutExtractionClient or PdfPigDocumentExtractionClient => "PdfPig",
        VisionLanguageModelDocumentExtractionClient => "vision model",
        PlainTextExtractionClient => "text",
        var c => c.GetType().Name,
    };

    public void Dispose()
    {
        _text.Dispose();
        if (_ownsClients)
        {
            _pdf.Dispose();
            if (!ReferenceEquals(_vision, _pdf))
            {
                _vision?.Dispose();
            }
        }
    }
}
