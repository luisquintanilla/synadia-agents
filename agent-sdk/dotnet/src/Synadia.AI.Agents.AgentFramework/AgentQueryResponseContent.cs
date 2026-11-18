using Microsoft.Extensions.AI;

namespace Synadia.AI.Agents.AgentFramework;

/// <summary>
/// The answer to a remote agent's mid-stream <c>query</c> (§7), as an MEAI <see cref="InputResponseContent"/>.
/// Pass it (inside a user <see cref="ChatMessage"/>) to the next <c>RunAsync</c> on the same <see cref="NatsAgentSession"/>
/// to resume the paused remote prompt. Create one with <see cref="AgentQueryContentExtensions.CreateResponse"/>.
/// </summary>
public sealed class AgentQueryResponseContent : InputResponseContent
{
    /// <summary>Create an answer for the query whose id is <paramref name="requestId"/>.</summary>
    public AgentQueryResponseContent(string requestId, string text, IEnumerable<DataContent>? attachments = null)
        : base(requestId)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Attachments = attachments?.ToList() ?? [];
    }

    /// <summary>The answer text.</summary>
    public string Text { get; }

    /// <summary>Attachments sent with the answer.</summary>
    public IReadOnlyList<DataContent> Attachments { get; }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Helpers for answering <see cref="AgentQueryContent"/> through MAF.</summary>
public static class AgentQueryContentExtensions
{
    /// <summary>Build the <see cref="AgentQueryResponseContent"/> that answers <paramref name="query"/> on the next run.</summary>
    public static AgentQueryResponseContent CreateResponse(this AgentQueryContent query, string text, IEnumerable<DataContent>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new AgentQueryResponseContent(query.RequestId, text, attachments);
    }
}
