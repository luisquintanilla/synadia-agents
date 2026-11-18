using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Synadia.AI.Agents.Service;

namespace Synadia.AI.Agents.AgentFramework;

/// <summary>Options for <see cref="AIAgentServeExtensions.ServeAIAgent"/>.</summary>
public sealed class AIAgentServeOptions
{
    /// <summary>
    /// Selects the conversation (and so the MAF <see cref="AgentSession"/>) a prompt belongs to. Default: the caller's
    /// <see cref="PromptContext.SessionId"/> (sent by <see cref="NatsAIAgent"/> and by <c>AsIChatClient()</c> when
    /// <see cref="ChatOptions.ConversationId"/> is set), otherwise one conversation for the whole agent address (the
    /// protocol's stateful-agent model).
    /// </summary>
    public Func<PromptContext, string>? ConversationKey { get; init; }

    /// <summary>Run options passed to every <c>RunStreamingAsync</c> call.</summary>
    public AgentRunOptions? RunOptions { get; init; }

    /// <summary>Forward <see cref="TextReasoningContent"/> as <c>status</c> chunks (default false).</summary>
    public bool IncludeReasoning { get; init; }

    /// <summary>
    /// How long to wait for the caller to answer a query this host asks: relayed queries, <see cref="ServedRun.AskAsync"/>,
    /// and tool approvals unless <see cref="ToolApprovalOptions.Timeout"/> is set. Default: the service's query timeout.
    /// </summary>
    public TimeSpan? QueryTimeout { get; init; }

    /// <summary>Maximum input-request round trips within one prompt (default 8). Exceeding it fails the prompt.</summary>
    public int MaxInputRounds { get; init; } = 8;

    /// <summary>How tool approvals are asked and parsed (see <see cref="PromptStream.AskApprovalAsync"/>).</summary>
    public ToolApprovalOptions? Approval { get; init; }

    /// <summary>
    /// Called when a prompt starts running on its conversation's <see cref="AgentSession"/>; the returned value (if any) is
    /// disposed when the prompt ends. Lets out-of-band callbacks that the agent does not surface as MAF content (for example
    /// a provider's own permission handler) find the active run and ask the caller via <see cref="ServedRun.AskAsync"/>.
    /// </summary>
    public Func<ServedRun, IDisposable?>? OnRun { get; init; }

    /// <summary>
    /// Durable storage for conversation sessions. When <c>null</c> (the default), sessions live in memory and are lost on
    /// restart. When set, each prompt loads its session with <see cref="AgentSessionStore.GetOrCreateSessionAsync"/> and
    /// saves it with <see cref="AgentSessionStore.SaveSessionAsync"/> after a successful run, so a restarted (or another)
    /// host instance continues the conversation. The store key is the conversation key, partitioned by the agent's
    /// <c>agent</c>, <c>owner</c> and <c>session</c> subject tokens.
    /// </summary>
    [Experimental(Diagnostics.SessionStore, UrlFormat = Diagnostics.UrlFormat)]
    public AgentSessionStore? SessionStore { get; init; }
}

internal static class Diagnostics
{
    public const string SessionStore = "SYNAGENT001";
    public const string UrlFormat = "https://github.com/synadia-ai/synadia-agents/blob/main/docs/dotnet-diagnostics.md#{0}";
}

/// <summary>A prompt being served by <see cref="AIAgentServeExtensions.ServeAIAgent"/>.</summary>
public sealed class ServedRun
{
    private readonly TimeSpan? _queryTimeout;

    internal ServedRun(PromptContext context, PromptStream stream, AgentSession session, TimeSpan? queryTimeout, CancellationToken cancellationToken)
    {
        Context = context;
        Stream = stream;
        Session = session;
        CancellationToken = cancellationToken;
        _queryTimeout = queryTimeout;
    }

    /// <summary>The prompt's envelope context.</summary>
    public PromptContext Context { get; }

    /// <summary>The prompt's response stream.</summary>
    public PromptStream Stream { get; }

    /// <summary>The MAF session the prompt runs on.</summary>
    public AgentSession Session { get; }

    /// <summary>Cancelled when the prompt is cancelled.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Ask the caller a query (§7); returns <c>null</c> on timeout.</summary>
    public async Task<PromptEnvelope?> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken);
        try
        {
            return await Stream.AskAsync(prompt, null, _queryTimeout, linked.Token).ConfigureAwait(false);
        }
        catch (AgentQueryTimeoutException)
        {
            return null;
        }
    }
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

    private sealed class Conversation
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public AgentSession? Session;
    }

    private sealed class AIAgentHost(AIAgent agent, AIAgentServeOptions options)
    {
        private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

        private readonly ToolApprovalOptions _approval = options.Approval is { Timeout: not null } a
            ? a
            : new ToolApprovalOptions { Prompt = options.Approval?.Prompt, Parser = options.Approval?.Parser, Timeout = options.QueryTimeout };

        public async Task HandleAsync(PromptContext context, PromptStream stream, CancellationToken cancellationToken)
        {
            var key = options.ConversationKey?.Invoke(context) ?? context.SessionId ?? "";
            var conversation = _conversations.GetOrAdd(key, _ => new Conversation());
            await conversation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
#pragma warning disable SYNAGENT001, MAAI001
                var store = options.SessionStore;
                var storeKey = store is null ? null : new AgentSessionStoreKey(key, new Dictionary<string, string>
                {
                    ["agent"] = context.Agent.Agent,
                    ["owner"] = context.Agent.Owner,
                    ["session"] = context.Agent.Name,
                });
                var session = store is null
                    ? conversation.Session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
                    : await store.GetOrCreateSessionAsync(agent, storeKey!, cancellationToken).ConfigureAwait(false);
                await RunAsync(context, stream, session, cancellationToken).ConfigureAwait(false);
                if (store is not null) await store.SaveSessionAsync(agent, storeKey!, session, cancellationToken).ConfigureAwait(false);
#pragma warning restore SYNAGENT001, MAAI001
            }
            finally
            {
                conversation.Gate.Release();
            }
        }

        private async Task RunAsync(PromptContext context, PromptStream stream, AgentSession session, CancellationToken cancellationToken)
        {
            using var run = options.OnRun?.Invoke(new ServedRun(context, stream, session, options.QueryTimeout, cancellationToken));
            IEnumerable<ChatMessage> input = [context.ToChatMessage()];
            for (var round = 0; ; round++)
            {
                var requests = new List<AIContent>();
                var answeredCalls = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var update in agent.RunStreamingAsync(input, session, options.RunOptions, cancellationToken)
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

        private async Task<AIContent> AnswerAsync(AIContent request, PromptStream stream, CancellationToken cancellationToken)
        {
            switch (request)
            {
                case ToolApprovalRequestContent approval:
                    return await stream.AskApprovalAsync(approval, _approval, cancellationToken).ConfigureAwait(false);
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
    }
}
