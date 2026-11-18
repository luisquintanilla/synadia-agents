using System.Text.Json;
using Microsoft.Extensions.AI;
using NATS.Client.Core;

namespace Synadia.AI.Agents.Service;

/// <summary>The inbound side of one prompt request: the decoded envelope plus transport details.</summary>
public sealed class PromptContext
{
    internal PromptContext(PromptEnvelope envelope, string subject, NatsHeaders? headers, AgentSubject agent)
    {
        Envelope = envelope;
        Subject = subject;
        Headers = headers;
        Agent = agent;
    }

    /// <summary>The decoded §5.1 envelope.</summary>
    public PromptEnvelope Envelope { get; }

    /// <summary>The prompt text.</summary>
    public string Prompt => Envelope.Prompt;

    /// <summary>Attachments as MEAI <see cref="DataContent"/> (name = wire filename).</summary>
    public IReadOnlyList<DataContent> Attachments => Envelope.Attachments;

    /// <summary>Top-level envelope fields the protocol does not define (§5.6).</summary>
    public IReadOnlyDictionary<string, JsonElement> Extras => Envelope.Extras;

    /// <summary>The subject the prompt arrived on.</summary>
    public string Subject { get; }

    /// <summary>NATS headers of the request, if any.</summary>
    public NatsHeaders? Headers { get; }

    /// <summary>The registration that received the prompt.</summary>
    public AgentSubject Agent { get; }

    /// <summary>
    /// Map the prompt to a single MEAI user <see cref="ChatMessage"/>: a <see cref="TextContent"/> followed by the
    /// attachments. Envelope extras are copied into <see cref="ChatMessage.AdditionalProperties"/>.
    /// </summary>
    public ChatMessage ToChatMessage()
    {
        var contents = new List<AIContent>(1 + Attachments.Count) { new TextContent(Prompt) };
        contents.AddRange(Attachments);
        var message = new ChatMessage(ChatRole.User, contents);
        if (Extras.Count > 0)
        {
            message.AdditionalProperties = [];
            foreach (var (k, v) in Extras) message.AdditionalProperties[k] = v;
        }
        return message;
    }
}
