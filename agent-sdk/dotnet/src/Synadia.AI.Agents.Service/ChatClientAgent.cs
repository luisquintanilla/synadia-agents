using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Synadia.AI.Agents.Service;

/// <summary>Options for <see cref="ChatClientAgentExtensions.ServeChatClient"/>.</summary>
public sealed class ChatClientServeOptions
{
    /// <summary>Optional system instructions prepended to every model call (not stored in history).</summary>
    public string? Instructions { get; init; }

    /// <summary>Base <see cref="ChatOptions"/> (tools, model id, temperature, ...). Cloned per model call.</summary>
    public ChatOptions? ChatOptions { get; init; }

    /// <summary>
    /// Selects the conversation a prompt belongs to. The default (<c>null</c>) treats the whole agent address as one
    /// conversation, which matches the protocol's model of a stateful agent. Return e.g. a value from
    /// <see cref="PromptContext.Extras"/> or a sender identity to keep separate histories.
    /// </summary>
    public Func<PromptContext, string>? ConversationKey { get; init; }

    /// <summary>Keep conversation history across prompts (default true). False makes every prompt stateless.</summary>
    public bool KeepHistory { get; init; } = true;

    /// <summary>Upper bound on stored history messages per conversation (default 100). Oldest turns are dropped first.</summary>
    public int MaxHistoryMessages { get; init; } = 100;

    /// <summary>Forward <see cref="TextReasoningContent"/> as <c>status</c> chunks (default false).</summary>
    public bool IncludeReasoning { get; init; }

    /// <summary>How long to wait for the caller to answer a tool-approval query (default: the service's query timeout).</summary>
    public TimeSpan? ApprovalTimeout { get; init; }

    /// <summary>Builds the query text for a <see cref="ToolApprovalRequestContent"/>. Default: <c>Allow tool 'x' with arguments {...}? (yes/no)</c>.</summary>
    public Func<ToolApprovalRequestContent, string>? ApprovalPrompt { get; init; }

    /// <summary>Interprets the caller's reply. Default: <c>y</c>, <c>yes</c>, <c>approve(d)</c>, <c>allow</c>, <c>ok</c>, <c>true</c> (case-insensitive) approve; anything else denies.</summary>
    public Func<PromptEnvelope, bool>? ApprovalParser { get; init; }

    /// <summary>Maximum approval round trips within one prompt (default 8).</summary>
    public int MaxApprovalRounds { get; init; } = 8;

    /// <summary>
    /// Let the model delegate to other agents on the network: the tools of <see cref="Tools.AgentTools"/> are added to
    /// every model call, and each prompt runs in its own call scope (<see cref="Tools.AgentTools.RunInPromptScopeAsync{T}"/>),
    /// so calls the model starts end with the prompt and the agent that sent the prompt cannot be prompted back. The
    /// chat client must invoke functions itself (e.g. <c>.UseFunctionInvocation()</c>); the helper is not disposed here.
    /// </summary>
    public Tools.AgentTools? AgentTools { get; init; }
}

/// <summary>Host any <see cref="IChatClient"/> as a protocol agent.</summary>
public static class ChatClientAgentExtensions
{
    /// <summary>
    /// Serve <paramref name="chatClient"/> on this service. Each prompt becomes a user <see cref="ChatMessage"/>
    /// (<see cref="PromptContext.ToChatMessage"/>), the model's streaming updates are written with
    /// <see cref="PromptStream.WriteAsync(ChatResponseUpdate, bool, CancellationToken)"/> (text/data → <c>response</c>,
    /// <see cref="AgentStatusContent"/> or <c>AdditionalProperties["synadia.status"]</c> → <c>status</c>), and every
    /// <see cref="ToolApprovalRequestContent"/> (e.g. from a <see cref="ApprovalRequiredAIFunction"/> behind a
    /// <c>FunctionInvokingChatClient</c>) is turned into a yes/no <c>query</c> (§7); the answer is fed back as a
    /// <see cref="ToolApprovalResponseContent"/> and the model is re-invoked. Prompts on the same conversation are serialized.
    /// </summary>
    /// <returns>The same service, for chaining. Call <see cref="AgentService.StartAsync"/> afterwards.</returns>
    public static AgentService ServeChatClient(this AgentService service, IChatClient chatClient, ChatClientServeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(chatClient);
        var host = new ChatClientHost(chatClient, options ?? new ChatClientServeOptions());
        return service.OnPrompt(host.HandleAsync);
    }

    private sealed class Conversation
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<ChatMessage> History = [];
    }

    private sealed class ChatClientHost(IChatClient client, ChatClientServeOptions options)
    {
        private static readonly HashSet<string> Yes = new(StringComparer.OrdinalIgnoreCase) { "y", "yes", "approve", "approved", "allow", "ok", "true" };
        private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

        private readonly IList<AITool>? _agentTools = options.AgentTools?.AsAITools();

        public Task HandleAsync(PromptContext context, PromptStream stream, CancellationToken cancellationToken)
            => options.AgentTools is { } tools
                ? tools.RunInPromptScopeAsync(() => HandleCoreAsync(context, stream, cancellationToken), CallerOf(context))
                : HandleCoreAsync(context, stream, cancellationToken);

        private static string? CallerOf(PromptContext context)
            => context.Sender is Synadia.AI.Agents.Identity.VerifiedSender verified ? verified.Id.ToString() : null;

        private ChatOptions? ChatOptionsForCall()
        {
            var chatOptions = options.ChatOptions?.Clone();
            if (_agentTools is null) return chatOptions;
            chatOptions ??= new ChatOptions();
            chatOptions.Tools = [.. chatOptions.Tools ?? [], .. _agentTools];
            return chatOptions;
        }

        private async Task HandleCoreAsync(PromptContext context, PromptStream stream, CancellationToken cancellationToken)
        {
            var key = options.ConversationKey?.Invoke(context) ?? "";
            var conversation = _conversations.GetOrAdd(key, _ => new Conversation());
            await conversation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var turn = new List<ChatMessage> { context.ToChatMessage() };
                for (var round = 0; ; round++)
                {
                    var messages = new List<ChatMessage>();
                    if (!string.IsNullOrEmpty(options.Instructions)) messages.Add(new ChatMessage(ChatRole.System, options.Instructions));
                    if (options.KeepHistory) messages.AddRange(conversation.History);
                    messages.AddRange(turn);

                    var updates = new List<ChatResponseUpdate>();
                    var approvals = new List<ToolApprovalRequestContent>();
                    await foreach (var update in client.GetStreamingResponseAsync(messages, ChatOptionsForCall(), cancellationToken)
                        .ConfigureAwait(false))
                    {
                        updates.Add(update);
                        approvals.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
                        await stream.WriteAsync(update, options.IncludeReasoning, cancellationToken).ConfigureAwait(false);
                    }
                    turn.AddRange(updates.ToChatResponse().Messages);

                    if (approvals.Count == 0) break;
                    if (round >= options.MaxApprovalRounds)
                    {
                        throw new RequestRejectedException(500, $"exceeded {options.MaxApprovalRounds} tool-approval rounds");
                    }
                    var answers = new List<AIContent>(approvals.Count);
                    foreach (var request in approvals)
                    {
                        answers.Add(await AskApprovalAsync(request, stream, cancellationToken).ConfigureAwait(false));
                    }
                    turn.Add(new ChatMessage(ChatRole.User, answers));
                }

                if (options.KeepHistory)
                {
                    conversation.History.AddRange(turn);
                    Trim(conversation.History, options.MaxHistoryMessages);
                }
            }
            finally
            {
                conversation.Gate.Release();
            }
        }

        private async Task<ToolApprovalResponseContent> AskApprovalAsync(ToolApprovalRequestContent request, PromptStream stream,
            CancellationToken cancellationToken)
        {
            var question = options.ApprovalPrompt?.Invoke(request) ?? DefaultPrompt(request);
            PromptEnvelope reply;
            try
            {
                reply = await stream.AskAsync(question, null, options.ApprovalTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentQueryTimeoutException)
            {
                return request.CreateResponse(false, "approval timed out");
            }
            var approved = options.ApprovalParser?.Invoke(reply) ?? Yes.Contains(reply.Prompt.Trim().TrimEnd('.', '!'));
            return request.CreateResponse(approved, approved ? null : (reply.Prompt.Length > 0 ? reply.Prompt : "denied"));
        }

        private static string DefaultPrompt(ToolApprovalRequestContent request)
        {
            if (request.ToolCall is FunctionCallContent call)
            {
                var args = call.Arguments is { Count: > 0 }
                    ? JsonSerializer.Serialize(call.Arguments, Compact.GetTypeInfo(typeof(IDictionary<string, object?>)))
                    : "{}";
                return $"Allow tool '{call.Name}' with arguments {args}? (yes/no)";
            }
            return $"Allow tool call {request.ToolCall}? (yes/no)";
        }

        private static void Trim(List<ChatMessage> history, int max)
        {
            if (max <= 0)
            {
                history.Clear();
                return;
            }
            if (history.Count <= max) return;
            var drop = history.Count - max;
            // Never start history mid-turn (e.g. with an orphaned tool result): advance to the next user prompt.
            while (drop < history.Count && !(history[drop].Role == ChatRole.User && history[drop].Contents.OfType<TextContent>().Any()))
            {
                drop++;
            }
            history.RemoveRange(0, drop);
        }
    }
}
