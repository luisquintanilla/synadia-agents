using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Synadia.AI.Agents.AgentFramework;

using AgentResponse = Microsoft.Agents.AI.AgentResponse;

/// <summary>
/// A Microsoft Agent Framework <see cref="AIAgent"/> backed by a remote agent on NATS (Synadia Agent Protocol).
/// Built on <see cref="Agent.AsChatClient"/>: the newest user message is the prompt, response chunks stream as
/// <see cref="AgentResponseUpdate"/>s, status chunks surface as <see cref="AgentStatusContent"/>.
/// </summary>
/// <remarks>
/// <para>Remote mid-stream queries (§7) <b>pause</b> the run: the run ends with the query surfaced per
/// <see cref="NatsAIAgentOptions.QueryMode"/>, the prompt stream stays open on the <see cref="NatsAgentSession"/>, and
/// the next run on that session delivers the answer and resumes the same remote prompt. This is MAF's
/// human-in-the-loop pattern, so a person, another agent, or a workflow can answer across turns.</para>
/// <para>Set <see cref="NatsAIAgentOptions.QueryHandler"/> to answer queries inline instead (required when the agent is
/// used as a tool via <c>AsAIFunction()</c>).</para>
/// </remarks>
public sealed class NatsAIAgent : AIAgent
{
    /// <summary>Envelope extra (§5.3) carrying <see cref="NatsAgentSession.SessionId"/>; hosts may key history on it.</summary>
    public const string SessionIdExtra = "session_id";

    /// <summary>Function name used to surface queries in <see cref="NatsQueryMode.FunctionCall"/> / <see cref="NatsQueryMode.ToolApproval"/> mode.</summary>
    public const string QueryFunctionName = "nats_agent_query";

    private readonly IChatClient _chat;
    private readonly NatsAIAgentOptions _options;

    /// <summary>Wrap <paramref name="agent"/>.</summary>
    public NatsAIAgent(Agent agent, NatsAIAgentOptions? options = null)
    {
        RemoteAgent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new NatsAIAgentOptions();
        _chat = agent.AsChatClient(new AgentChatClientOptions
        {
            PromptOptions = _options.PromptOptions,
            QueryHandler = _options.QueryHandler,
            IncludeStatus = _options.IncludeStatus,
        });
    }

    /// <summary>The remote agent.</summary>
    public Agent RemoteAgent { get; }

    /// <inheritdoc />
    protected override string? IdCore => _options.Id ?? RemoteAgent.Info.InstanceId;

    /// <inheritdoc />
    public override string? Name => _options.Name ?? RemoteAgent.Info.Name;

    /// <inheritdoc />
    public override string? Description => _options.Description ?? RemoteAgent.Info.Description;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null)
        {
            if (serviceType == typeof(Agent)) return RemoteAgent;
            if (serviceType == typeof(IChatClient)) return _chat;
        }
        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new NatsAgentSession());

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var s = session as NatsAgentSession ?? throw new ArgumentException($"session must be a {nameof(NatsAgentSession)}", nameof(session));
        return new(s.StateBag.Serialize());
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new NatsAgentSession(AgentSessionStateBag.Deserialize(serializedState)));

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }
        return updates.ToAgentResponse();
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var input = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var s = session switch
        {
            null => new NatsAgentSession(),
            NatsAgentSession n => n,
            _ => throw new ArgumentException($"session must be a {nameof(NatsAgentSession)} created by this agent", nameof(session)),
        };

        await s.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAsyncEnumerator<ChatResponseUpdate>? stream = null;
        CancellationTokenSource? streamCts = null;
        CancellationTokenRegistration link = default;
        var paused = false;
        try
        {
            if (s.Pending is { } pending)
            {
                // Resolve before detaching so a missing answer leaves the remote query pending for a retry.
                var answer = ResolveAnswer(pending.Query, input)
                    ?? throw new InvalidOperationException(
                        $"the remote agent is waiting for an answer to \"{pending.Query.Prompt}\" (query {pending.Query.RequestId}); " +
                        "pass an AgentQueryResponseContent, a matching function result/approval, or user text");
                s.Pending = null;
                stream = pending.Stream;
                streamCts = pending.Cts;
                link = cancellationToken.Register(static c => ((CancellationTokenSource)c!).Cancel(), streamCts);
                if (_options.QueryMode == NatsQueryMode.ToolApproval)
                {
                    // MAF workflows require the approved call and its result to appear in the response that follows an approval.
                    var call = QueryCall(pending.Query);
                    yield return Wrap(new ChatResponseUpdate(ChatRole.Assistant, [call]));
                    yield return Wrap(new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent(call.CallId, answer.Prompt)]));
                }
                await pending.Query.ReplyAsync(answer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                streamCts = new CancellationTokenSource();
                link = cancellationToken.Register(static c => ((CancellationTokenSource)c!).Cancel(), streamCts);
                stream = _chat.GetStreamingResponseAsync(input, BuildChatOptions(s, options), streamCts.Token).GetAsyncEnumerator(streamCts.Token);
            }

            while (await stream.MoveNextAsync().ConfigureAwait(false))
            {
                var update = stream.Current;
                if (update.Contents.OfType<AgentQueryContent>().FirstOrDefault() is { } query)
                {
                    s.Pending = new PendingQuery(stream, streamCts, query);
                    paused = true;
                    yield return Wrap(Surface(update, query));
                    yield break;
                }
                yield return Wrap(update);
            }
        }
        finally
        {
            await link.DisposeAsync().ConfigureAwait(false);
            if (!paused)
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                streamCts?.Dispose();
            }
            s.Gate.Release();
        }
    }

    private AgentResponseUpdate Wrap(ChatResponseUpdate update) => new(update) { AgentId = Id };

    private ChatResponseUpdate Surface(ChatResponseUpdate update, AgentQueryContent query)
    {
        if (_options.QueryMode == NatsQueryMode.InputRequest) return update;
        var call = QueryCall(query);
        AIContent content = _options.QueryMode == NatsQueryMode.ToolApproval
            ? new ToolApprovalRequestContent(query.RequestId, call)
            : call;
        var surfaced = update.Clone();
        surfaced.Contents = [content];
        return surfaced;
    }

    private static FunctionCallContent QueryCall(AgentQueryContent query)
        => new(query.RequestId, QueryFunctionName, new Dictionary<string, object?> { ["prompt"] = query.Prompt }) { RawRepresentation = query };

    private ChatOptions BuildChatOptions(NatsAgentSession session, AgentRunOptions? options)
    {
        var props = new AdditionalPropertiesDictionary();
        if (options?.AdditionalProperties is { } extra)
        {
            foreach (var (k, v) in extra) props[k] = v;
        }
        props.TryAdd(SessionIdExtra, session.SessionId);
        return new ChatOptions { AdditionalProperties = props };
    }

    /// <summary>Find the answer to <paramref name="query"/> in the messages passed to the resuming run.</summary>
    internal static PromptEnvelope? ResolveAnswer(AgentQueryContent query, IEnumerable<ChatMessage> messages)
    {
        var id = query.RequestId;
        ChatMessage? lastUser = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User) lastUser = message;
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case AgentQueryResponseContent r when r.RequestId == id:
                        return new PromptEnvelope(r.Text, r.Attachments);
                    case ToolApprovalResponseContent a when a.RequestId == id:
                        return new PromptEnvelope(a.Approved ? "yes" : "no");
                    case FunctionResultContent f when f.CallId == id:
                        return new PromptEnvelope(ResultText(f.Result));
                    case InputResponseContent other when other.RequestId == id:
                        return new PromptEnvelope(message.Text);
                }
            }
        }
        if (lastUser is null) return null;
        var text = new StringBuilder();
        foreach (var t in lastUser.Contents.OfType<TextContent>()) text.Append(t.Text);
        var attachments = lastUser.Contents.OfType<DataContent>().ToList();
        if (text.Length == 0 && attachments.Count == 0) return null;
        return new PromptEnvelope(text.ToString(), attachments);
    }

    private static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        TextContent t => t.Text,
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions.GetTypeInfo(result.GetType())),
    };
}

/// <summary>MAF helpers for <see cref="Agent"/>.</summary>
public static class AgentFrameworkAgentExtensions
{
    /// <summary>Use the remote NATS agent as a Microsoft Agent Framework <see cref="AIAgent"/>.</summary>
    public static NatsAIAgent AsAIAgent(this Agent agent, NatsAIAgentOptions? options = null) => new(agent, options);
}
