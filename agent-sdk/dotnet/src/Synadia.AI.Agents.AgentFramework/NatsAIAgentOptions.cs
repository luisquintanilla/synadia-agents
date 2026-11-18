namespace Synadia.AI.Agents.AgentFramework;

/// <summary>How a remote agent's mid-stream <c>query</c> (§7) is surfaced to the MAF caller.</summary>
public enum NatsQueryMode
{
    /// <summary>
    /// Surface the <see cref="AgentQueryContent"/> itself (an MEAI <c>InputRequestContent</c>). Answer on the next run with
    /// <see cref="AgentQueryContentExtensions.CreateResponse"/> or plain user text.
    /// </summary>
    InputRequest,

    /// <summary>
    /// Surface a <c>FunctionCallContent</c> named <see cref="NatsAIAgent.QueryFunctionName"/> whose <c>CallId</c> is the
    /// query id and whose <c>prompt</c> argument is the question. Answer with a <c>FunctionResultContent</c> (the result
    /// text is the reply). MAF workflows turn this into a <c>RequestInfoEvent</c> (external request) automatically.
    /// </summary>
    FunctionCall,

    /// <summary>
    /// Surface a <c>ToolApprovalRequestContent</c> wrapping the <see cref="FunctionCall"/> call. The approval answer is sent
    /// as <c>"yes"</c> / <c>"no"</c>. Use for remote yes/no confirmations in MAF approval UIs and workflows.
    /// </summary>
    ToolApproval,
}

/// <summary>Options for <see cref="NatsAIAgent"/>.</summary>
public sealed class NatsAIAgentOptions
{
    /// <summary>MAF agent id (default: the remote agent's instance id).</summary>
    public string? Id { get; set; }

    /// <summary>MAF agent name (default: the remote agent's advertised name).</summary>
    public string? Name { get; set; }

    /// <summary>MAF agent description (default: the remote agent's advertised description).</summary>
    public string? Description { get; set; }

    /// <summary>Per-prompt transport options (timeouts, headers).</summary>
    public PromptOptions? PromptOptions { get; set; }

    /// <summary>Surface non-ack status chunks as <see cref="AgentStatusContent"/> (default true).</summary>
    public bool IncludeStatus { get; set; } = true;

    /// <summary>
    /// Answer remote queries inline (the run never pauses). Required when the agent is used as a tool via
    /// <c>AsAIFunction()</c>, because a tool invocation cannot pause across turns.
    /// </summary>
    public Func<AgentQueryContent, CancellationToken, ValueTask<string?>>? QueryHandler { get; set; }

    /// <summary>How unanswered remote queries are surfaced (default <see cref="NatsQueryMode.InputRequest"/>).</summary>
    public NatsQueryMode QueryMode { get; set; } = NatsQueryMode.InputRequest;
}
