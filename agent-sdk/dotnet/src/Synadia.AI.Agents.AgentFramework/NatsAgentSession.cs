using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Synadia.AI.Agents.AgentFramework;

/// <summary>
/// A MAF <see cref="AgentSession"/> for a <see cref="NatsAIAgent"/>. The remote agent owns the conversation history
/// (the protocol is stateful per agent), so the session carries only a stable <see cref="SessionId"/> (sent as the
/// <see cref="NatsAIAgent.SessionIdExtra"/> envelope extra so hosts can keep one history per MAF session) and, while
/// a remote query is unanswered, the paused prompt stream.
/// </summary>
/// <remarks>
/// Serialization keeps the <see cref="SessionId"/> only. A paused prompt stream is a live NATS subscription and cannot
/// be serialized; answer the query in the same process (the remote agent waits up to its query timeout, 5 minutes
/// by default) or call <see cref="CancelPendingQueryAsync"/>.
/// </remarks>
public sealed class NatsAgentSession : AgentSession
{
    private const string SessionIdKey = "synadia.session_id";

    internal NatsAgentSession()
        : this(new AgentSessionStateBag())
    {
    }

    internal NatsAgentSession(AgentSessionStateBag stateBag)
        : base(stateBag)
    {
        if (!StateBag.TryGetValue<string>(SessionIdKey, out var id) || string.IsNullOrEmpty(id))
        {
            StateBag.SetValue(SessionIdKey, Guid.NewGuid().ToString("N"));
        }
    }

    /// <summary>Stable id of this conversation, sent to the remote agent as an envelope extra.</summary>
    public string SessionId => StateBag.TryGetValue<string>(SessionIdKey, out var id) && id is not null ? id : "";

    /// <summary>The remote query awaiting an answer, if the last run paused on one.</summary>
    public AgentQueryContent? PendingQuery => Pending?.Query;

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal PendingQuery? Pending { get; set; }

    /// <summary>Abandon a paused prompt (the remote agent sees the caller go away and its query times out).</summary>
    public async ValueTask CancelPendingQueryAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Pending is { } p)
            {
                Pending = null;
                await p.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal sealed class PendingQuery(IAsyncEnumerator<ChatResponseUpdate> stream, CancellationTokenSource cts, AgentQueryContent query) : IAsyncDisposable
{
    public IAsyncEnumerator<ChatResponseUpdate> Stream { get; } = stream;

    public CancellationTokenSource Cts { get; } = cts;

    public AgentQueryContent Query { get; } = query;

    public async ValueTask DisposeAsync()
    {
        await Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        Cts.Dispose();
    }
}
