using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Synadia.AI.Agents.Service;

namespace Synadia.AI.Agents.AgentFramework;

/// <summary>Options for <see cref="AIAgentServeExtensions.ServeAIAgent"/>.</summary>
public sealed class AIAgentServeOptions
{
    /// <summary>
    /// Selects the conversation (and so the MAF <see cref="AgentSession"/>) a prompt belongs to. Default: the
    /// <see cref="NatsAIAgent.SessionIdExtra"/> envelope extra when present (sent by <see cref="NatsAIAgent"/>), otherwise
    /// one conversation for the whole agent address (the protocol's stateful-agent model).
    /// </summary>
    public Func<PromptContext, string>? ConversationKey { get; init; }

    /// <summary>Run options passed to every <c>RunStreamingAsync</c> call.</summary>
    public AgentRunOptions? RunOptions { get; init; }

    /// <summary>Forward <see cref="TextReasoningContent"/> as <c>status</c> chunks (default false).</summary>
    public bool IncludeReasoning { get; init; }

    /// <summary>How long to wait for the caller to answer a relayed query (default: the service's query timeout).</summary>
    public TimeSpan? QueryTimeout { get; init; }

    /// <summary>Builds the query text for a <see cref="ToolApprovalRequestContent"/>. Default: <c>Allow tool 'x' with arguments {...}? (yes/no)</c>.</summary>
    public Func<ToolApprovalRequestContent, string>? ApprovalPrompt { get; init; }

    /// <summary>Interprets the caller's reply to an approval. Default: <c>y</c>, <c>yes</c>, <c>approve(d)</c>, <c>allow</c>, <c>ok</c>, <c>true</c> approve.</summary>
    public Func<PromptEnvelope, bool>? ApprovalParser { get; init; }

    /// <summary>Maximum input-request round trips within one prompt (default 8).</summary>
    public int MaxInputRounds { get; init; } = 8;
}

/// <summary>Host any Microsoft Agent Framework <see cref="AIAgent"/> as a Synadia protocol agent.</summary>
public static class AIAgentServeExtensions
{
    /// <summary>
    /// Serve <paramref name="agent"/> on this service. Each prompt becomes a user message
    /// (<see cref="PromptContext.ToChatMessage"/>) run on the conversation's MAF <see cref="AgentSession"/> (MAF keeps the
    /// history); streaming updates are written as <c>response</c>/<c>status</c> chunks. MAF human-in-the-loop requests
    /// become protocol queries (§7) and the answers are fed back on the same session:
    /// <list type="bullet">
    /// <item><see cref="ToolApprovalRequestContent"/> → yes/no query → <see cref="ToolApprovalResponseContent"/>.</item>
    /// <item><see cref="AgentQueryContent"/> (a nested <see cref="NatsAIAgent"/> asking) → relayed query → <see cref="AgentQueryResponseContent"/>.</item>
    /// <item>A <see cref="NatsAIAgent.QueryFunctionName"/> call without a result → relayed query → <see cref="FunctionResultContent"/>.</item>
    /// </list>
    /// Prompts on the same conversation are serialized.
    /// </summary>
    /// <returns>The same service, for chaining. Call <see cref="AgentService.StartAsync"/> afterwards.</returns>
    public static AgentService ServeAIAgent(this AgentService service, AIAgent agent, AIAgentServeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(agent);
        var host = new AIAgentHost(agent, options ?? new AIAgentServeOptions());
        return service.OnPrompt(host.HandleAsync);
    }

    /// <summary>Default conversation key: the <see cref="NatsAIAgent.SessionIdExtra"/> extra, or <c>""</c>.</summary>
    public static string DefaultConversationKey(PromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Extras.TryGetValue(NatsAIAgent.SessionIdExtra, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }

    private sealed class Conversation
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public AgentSession? Session;
    }

    private sealed class AIAgentHost(AIAgent agent, AIAgentServeOptions options)
    {
        private static readonly HashSet<string> Yes = new(StringComparer.OrdinalIgnoreCase) { "y", "yes", "approve", "approved", "allow", "ok", "true" };
        private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

        public async Task HandleAsync(PromptContext context, PromptStream stream, CancellationToken cancellationToken)
        {
            var key = (options.ConversationKey ?? DefaultConversationKey)(context);
            var conversation = _conversations.GetOrAdd(key, _ => new Conversation());
            await conversation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                conversation.Session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                IEnumerable<ChatMessage> input = [context.ToChatMessage()];
                for (var round = 0; ; round++)
                {
                    var requests = new List<AIContent>();
                    var answeredCalls = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var update in agent.RunStreamingAsync(input, conversation.Session, options.RunOptions, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        foreach (var content in update.Contents)
                        {
                            switch (content)
                            {
                                case ToolApprovalRequestContent:
                                case InputRequestContent:
                                    requests.Add(content);
                                    break;
                                case FunctionCallContent { Name: NatsAIAgent.QueryFunctionName }:
                                    requests.Add(content);
                                    break;
                                case FunctionResultContent result:
                                    answeredCalls.Add(result.CallId);
                                    break;
                            }
                        }
                        await stream.WriteAsync(update.AsChatResponseUpdate(), options.IncludeReasoning, cancellationToken).ConfigureAwait(false);
                    }

                    requests.RemoveAll(r => r is FunctionCallContent call && answeredCalls.Contains(call.CallId));
                    if (requests.Count == 0) return;
                    if (round >= options.MaxInputRounds)
                    {
                        throw new RequestRejectedException(500, $"exceeded {options.MaxInputRounds} input-request rounds");
                    }
                    var answers = new List<AIContent>(requests.Count);
                    foreach (var request in requests)
                    {
                        answers.Add(await AnswerAsync(request, stream, cancellationToken).ConfigureAwait(false));
                    }
                    var next = new List<ChatMessage>();
                    if (answers.OfType<FunctionResultContent>().ToList() is { Count: > 0 } results) next.Add(new ChatMessage(ChatRole.Tool, [.. results]));
                    if (answers.Where(a => a is not FunctionResultContent).ToList() is { Count: > 0 } responses) next.Add(new ChatMessage(ChatRole.User, responses));
                    input = next;
                }
            }
            finally
            {
                conversation.Gate.Release();
            }
        }

        private async Task<AIContent> AnswerAsync(AIContent request, PromptStream stream, CancellationToken cancellationToken)
        {
            switch (request)
            {
                case ToolApprovalRequestContent approval:
                {
                    var reply = await AskAsync(options.ApprovalPrompt?.Invoke(approval) ?? DefaultApprovalPrompt(approval), null, stream, cancellationToken)
                        .ConfigureAwait(false);
                    if (reply is null) return approval.CreateResponse(false, "approval timed out");
                    var approved = options.ApprovalParser?.Invoke(reply) ?? Yes.Contains(reply.Prompt.Trim().TrimEnd('.', '!'));
                    return approval.CreateResponse(approved, approved ? null : (reply.Prompt.Length > 0 ? reply.Prompt : "denied"));
                }
                case AgentQueryContent query:
                {
                    var reply = await AskAsync(query.Prompt, query.Attachments, stream, cancellationToken).ConfigureAwait(false)
                        ?? throw new RequestRejectedException(504, $"no answer to relayed query \"{query.Prompt}\"");
                    return new AgentQueryResponseContent(query.RequestId, reply.Prompt, reply.Attachments);
                }
                case FunctionCallContent call:
                {
                    var prompt = call.Arguments?.TryGetValue("prompt", out var p) == true ? p?.ToString() ?? "" : "";
                    var reply = await AskAsync(prompt, null, stream, cancellationToken).ConfigureAwait(false)
                        ?? throw new RequestRejectedException(504, $"no answer to relayed query \"{prompt}\"");
                    return new FunctionResultContent(call.CallId, reply.Prompt);
                }
                default:
                    throw new RequestRejectedException(501, $"agent requested input of unsupported type {request.GetType().Name}");
            }
        }

        private async Task<PromptEnvelope?> AskAsync(string prompt, IEnumerable<DataContent>? attachments, PromptStream stream,
            CancellationToken cancellationToken)
        {
            try
            {
                return await stream.AskAsync(prompt, attachments, options.QueryTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentQueryTimeoutException)
            {
                return null;
            }
        }

        private static string DefaultApprovalPrompt(ToolApprovalRequestContent request)
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
    }
}
