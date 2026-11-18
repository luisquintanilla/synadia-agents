using NATS.Client.Core;
using Synadia.AI.Agents.Service;

namespace Synadia.Examples.DocumentAgent.Host;

/// <summary>Wires a <see cref="DocumentAgent"/> onto the Synadia Agent Protocol for NATS via the .NET host SDK.</summary>
public static class DocumentAgentHosting
{
    /// <summary>§2 <c>agent</c> token this example registers under: <c>agents.prompt.docs.&lt;owner&gt;.&lt;name&gt;</c>.</summary>
    public const string AgentType = "docs";

    /// <summary>
    /// Largest raw attachment that fits, base64-encoded, inside the connected server's <c>max_payload</c>
    /// (1 MiB when the server reports none).
    /// </summary>
    public static long MaxAttachmentBytesFor(INatsConnection connection)
    {
        long maxPayload = connection.ServerInfo?.MaxPayload is > 0 and var reported ? reported : PayloadLimits.DefaultMaxPayloadBytes;
        return PayloadLimits.MaxRawAttachmentBytes(maxPayload);
    }

    /// <summary>
    /// Registers <paramref name="agent"/> as an agent service. The agent owns its state (the document index), so
    /// the host keeps no chat history: each prompt reaches the agent as one user message whose
    /// <see cref="Microsoft.Extensions.AI.DataContent"/> items are the attachments. Status updates become
    /// <c>status</c> chunks; markdown becomes <c>response</c> chunks. Call <see cref="AgentService.StartAsync"/> next.
    /// </summary>
    public static AgentService Create(INatsConnection connection, DocumentAgent agent, bool supportsImages, string owner, string name = "main")
    {
        ArgumentNullException.ThrowIfNull(agent);
        var formats = supportsImages ? "pdf,image,text" : "pdf,text";
        var modes = agent.SupportsQuestions ? "extract,ask" : "extract";
        var service = new AgentService(connection, new AgentServiceOptions
        {
            Agent = AgentType,
            Owner = owner,
            Name = name,
            Description = $"Document extraction agent: attachments ({formats}) to markdown; modes: {modes}. Send /help for usage.",
            ExtraMetadata = new Dictionary<string, string>
            {
                ["document_formats"] = formats,
                ["document_modes"] = modes,
                ["max_attachment_bytes"] = MaxAttachmentBytesFor(connection).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return service.ServeChatClient(new DocumentAgentChatClient(agent), new ChatClientServeOptions { KeepHistory = false });
    }

    /// <summary>Lower-cases <paramref name="value"/> and keeps only <c>[a-z0-9-]</c>, so it is a valid subject token.</summary>
    public static string ToToken(string value)
    {
        var chars = value.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-').ToArray();
        return chars.Length > 0 ? new string(chars) : "local";
    }
}
