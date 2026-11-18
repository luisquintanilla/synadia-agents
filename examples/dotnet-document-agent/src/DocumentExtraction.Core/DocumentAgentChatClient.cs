using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Synadia.Examples.DocumentAgent;

/// <summary>
/// Exposes an <see cref="IDocumentAgentHandler"/> as an MEAI <see cref="IChatClient"/>, so an MEAI-native host
/// (e.g. the .NET agent SDK's <c>ServeChatClient</c>) can serve it unchanged. The last user message supplies
/// the prompt text and its <see cref="DataContent"/> attachments (Name = filename). Status updates pass
/// through as content-less updates tagged with <see cref="DocumentAgentStatus.AdditionalPropertyKey"/>.
/// </summary>
public sealed class DocumentAgentChatClient : IChatClient
{
    private readonly IDocumentAgentHandler _handler;

    public DocumentAgentChatClient(IDocumentAgentHandler handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var last = messages.LastOrDefault(m => m.Role == ChatRole.User) ?? new ChatMessage(ChatRole.User, string.Empty);
        var responseId = Guid.NewGuid().ToString("N");
        await foreach (var update in _handler.HandleAsync(DocumentAgentRequest.FromMessage(last), cancellationToken).ConfigureAwait(false))
        {
            update.ResponseId ??= responseId;
            update.ModelId ??= "document-agent";
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : serviceKey is null && serviceType.IsInstanceOfType(_handler) ? _handler
        : null;

    public void Dispose()
    {
    }
}
