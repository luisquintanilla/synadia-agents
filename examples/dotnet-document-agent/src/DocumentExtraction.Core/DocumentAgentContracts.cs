using Microsoft.Extensions.AI;

namespace Synadia.Examples.DocumentAgent;

/// <summary>What the agent should do with a prompt.</summary>
public enum DocumentAgentMode
{
    /// <summary>Infer from the prompt and attachments (see <see cref="DocumentAgent"/>).</summary>
    Auto,

    /// <summary>Convert each attachment to markdown (text + tables) and stream it back.</summary>
    Extract,

    /// <summary>Index any attachments, then answer the prompt from the indexed documents.</summary>
    Ask,

    /// <summary>Index attachments only.</summary>
    Ingest,

    /// <summary>Forget all indexed documents.</summary>
    Reset,
}

/// <summary>One prompt, transport-neutral: the text plus attachments as MEAI <see cref="DataContent"/> (Name = filename).</summary>
public sealed record DocumentAgentRequest(string Prompt, IReadOnlyList<DataContent> Attachments, DocumentAgentMode Mode = DocumentAgentMode.Auto)
{
    public static DocumentAgentRequest FromMessage(ChatMessage message, DocumentAgentMode mode = DocumentAgentMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message.Text ?? string.Empty, message.Contents.OfType<DataContent>().ToArray(), mode);
    }
}

/// <summary>
/// A progress note ("extracting report.pdf (page 2/5)"). Emitted in-band, in order with the text, as a
/// content-less <see cref="ChatResponseUpdate"/> carrying this record in
/// <see cref="ChatResponseUpdate.AdditionalProperties"/>; a NATS host maps it to a wire
/// <c>{"type":"status","data":...}</c> chunk and plain MEAI consumers simply see no text.
/// </summary>
public sealed record DocumentAgentStatus(string Message, string? Attachment = null, int? Page = null, int? PageCount = null)
{
    public const string AdditionalPropertyKey = "synadia.status";

    public ChatResponseUpdate ToUpdate() => new()
    {
        Role = ChatRole.Assistant,
        AdditionalProperties = new() { [AdditionalPropertyKey] = this },
    };

    public static bool TryGet(ChatResponseUpdate update, out DocumentAgentStatus status)
    {
        if (update.AdditionalProperties?.TryGetValue(AdditionalPropertyKey, out var value) == true && value is DocumentAgentStatus s)
        {
            status = s;
            return true;
        }

        status = null!;
        return false;
    }

    public override string ToString() => Message;
}

/// <summary>
/// The NATS-independent seam: a prompt in, a stream of markdown text and status updates out.
/// A NATS host (or the console harness, or tests) drives this; nothing below it knows about NATS.
/// </summary>
public interface IDocumentAgentHandler
{
    IAsyncEnumerable<ChatResponseUpdate> HandleAsync(DocumentAgentRequest request, CancellationToken cancellationToken = default);
}

public sealed class DocumentAgentOptions
{
    /// <summary>
    /// Largest raw attachment accepted. Defaults to what fits a base64-encoded attachment inside the
    /// NATS default 1 MiB <c>max_payload</c> (~760 KiB); larger files are refused with an explanation.
    /// </summary>
    public long MaxAttachmentBytes { get; set; } = PayloadLimits.MaxRawAttachmentBytes();

    /// <summary>Retrieve passages for the question up front and put them in the prompt (robust with small models).</summary>
    public bool PrefetchPassages { get; set; } = true;

    /// <summary>Also give the model the <c>search_documents</c> tool for follow-up lookups.</summary>
    public bool UseSearchTool { get; set; } = true;

    /// <summary>Template for answer-generation options (model id, temperature, provider-specific knobs).</summary>
    public ChatOptions? AnswerChatOptions { get; set; }

    public string AnswerSystemPrompt { get; set; } =
        "You answer questions about documents the user has shared. Use only the provided passages and the " +
        "search_documents tool. Quote numbers exactly, cite the source by its plain file name in parentheses " +
        "(for example: (report.pdf)) and never invent links, paths or URLs. Say so plainly if the documents do " +
        "not contain the answer. Answer in concise markdown.";
}
