using System.Globalization;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Synadia.AI.Agents;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire;

/// <summary>Dashboard / CLI / MCP commands for the agent fleet.</summary>
internal static class SynadiaAgentsCommands
{
    public const string Discover = "discover";
    public const string Status = "status";
    public const string Prompt = "prompt";
    public const string Connect = "connect";

    private const string AgentArg = "agent";
    private const string PromptArg = "prompt";
    private const string TimeoutArg = "timeoutSeconds";
    private const string AttachmentArg = "attachment";

    public static void AddFleetCommands(IResourceBuilder<SynadiaAgentsResource> builder)
    {
        var resource = builder.Resource;

        builder.WithCommand(Discover, "Discover", ctx => DiscoverAsync(resource, ctx), new CommandOptions
        {
            Description = "Run $SRV.INFO.agents discovery and list every agent on this NATS server.",
            IconName = "Search",
            IsHighlighted = true,
            UpdateState = ctx => Availability(resource),
        });

        builder.WithCommand(Status, "Status", ctx => StatusAsync(resource, ctx.Arguments.GetString(AgentArg), ctx), new CommandOptions
        {
            Description = "Request agents.status.<agent>.<owner>.<name> from each (or one) agent.",
            IconName = "HeartPulse",
            IsHighlighted = true,
            UpdateState = ctx => Availability(resource),
            Arguments =
            [
                new InteractionInput { Name = AgentArg, Label = "Agent filter (blank = all)", InputType = InputType.Text, Placeholder = "e.g. demo-agent or py-demo-agent/aspire/python" },
            ],
        });

        builder.WithCommand(Prompt, "Prompt (smoke)", ctx => PromptAsync(resource, ctx.Arguments.GetString(AgentArg), ctx), new CommandOptions
        {
            Description = "Send a prompt to each (or one) agent over NATS and show the streamed replies.",
            IconName = "Chat",
            IsHighlighted = true,
            UpdateState = ctx => Availability(resource),
            Arguments = PromptArguments(resource.Options, includeAgentFilter: true),
        });

        builder.WithCommand(Connect, "Connect a client", ctx => ConnectAsync(resource, ctx), new CommandOptions
        {
            Description = "Show NATS_URL (with the local dev credentials) so agents and clients started outside Aspire can join this fleet.",
            IconName = "PlugConnected",
            UpdateState = ctx => Availability(resource),
        });
    }

    private static async Task<ExecuteCommandResult> ConnectAsync(SynadiaAgentsResource resource, ExecuteCommandContext context)
    {
        var url = await resource.Parent.UriExpression.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(url))
        {
            return CommandResults.Failure("NATS URL is not available yet.");
        }

        var md = new StringBuilder();
        md.AppendLine("# Join this agent fleet from outside Aspire");
        md.AppendLine();
        md.AppendLine("Every SDK, agent harness, and example in this repo reads `NATS_URL`. The password is generated per run for local development.");
        md.AppendLine();
        md.AppendLine("**PowerShell**");
        md.AppendLine();
        md.AppendLine("```powershell");
        md.AppendLine(CultureInfo.InvariantCulture, $"$env:NATS_URL = '{url}'");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("**bash / zsh**");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine(CultureInfo.InvariantCulture, $"export NATS_URL='{url}'");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("Then, from the repo root, e.g. `cd client-sdk/typescript; bun run examples/01-discover.ts` — or start another agent and watch it appear under **agents**.");
        return CommandResults.Success("NATS_URL ready to copy.", new CommandResultData { Value = md.ToString(), Format = CommandResultFormat.Markdown, DisplayImmediately = true });
    }

    public static void AddAgentCommands(DiscoveredAgentResource child)
    {
        var fleet = child.Parent;
        child.Annotations.Add(new ResourceCommandAnnotation(
            name: Status,
            displayName: "Status",
            updateState: _ => Availability(fleet),
            executeCommand: ctx => StatusAsync(fleet, child.AgentKey, ctx),
            displayDescription: "Request this agent's status endpoint.",
            parameter: null,
            confirmationMessage: null,
            iconName: "HeartPulse",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));

        child.Annotations.Add(new ResourceCommandAnnotation(
            Prompt,
            "Prompt",
            _ => Availability(fleet),
            ctx => PromptAsync(fleet, child.AgentKey, ctx),
            "Send a prompt to this agent.",
            PromptArguments(fleet.Options, includeAgentFilter: false),
            null,
            "Chat",
            IconVariant.Regular,
            true,
            ResourceCommandVisibility.UI | ResourceCommandVisibility.Api,
            null));
    }

    private static InteractionInput[] PromptArguments(SynadiaAgentsOptions options, bool includeAgentFilter)
    {
        var inputs = new List<InteractionInput>
        {
            new() { Name = PromptArg, Label = "Prompt", InputType = InputType.Text, Required = true, Value = options.SmokePrompt, MaxLength = 4000 },
        };

        if (includeAgentFilter)
        {
            inputs.Add(new() { Name = AgentArg, Label = "Agent filter (blank = all)", InputType = InputType.Text, Placeholder = "e.g. demo-agent" });
        }

        inputs.Add(new() { Name = AttachmentArg, Label = "Attachment file (optional)", InputType = InputType.Text, Placeholder = "e.g. examples/dotnet-document-agent/fixtures/fabrikam-q3-report.pdf" });
        inputs.Add(new() { Name = TimeoutArg, Label = "Timeout (seconds)", InputType = InputType.Number, Value = ((int)options.PromptTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) });
        return [.. inputs];
    }

    private static ResourceCommandState Availability(SynadiaAgentsResource resource) =>
        resource.Monitor is { IsConnected: true } ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;

    private static async Task<ExecuteCommandResult> DiscoverAsync(SynadiaAgentsResource resource, ExecuteCommandContext context)
    {
        if (resource.Monitor is not { IsConnected: true } monitor)
        {
            return CommandResults.Failure("Not connected to NATS yet.");
        }

        var agents = await monitor.DiscoverAsync(context.CancellationToken).ConfigureAwait(false);
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Discovered {agents.Count} agent instance(s)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"`{FleetProtocol.InfoSubject}` request-many, stall strategy ({NatsStall()}).");
        md.AppendLine();
        md.AppendLine("| Agent | Owner | Name | Session | Protocol | Harness | Prompt subject | Instance |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var a in agents)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(a.Agent)} | {Cell(a.Owner)} | {Cell(a.Name)} | {Cell(a.Session)} | {Cell(a.ProtocolVersion)} | {Cell(a.Version)} | `{Cell(a.PromptSubject)}` | `{Cell(a.InstanceId)}` |");
        }

        foreach (var a in agents)
        {
            context.Logger.LogInformation("discovered {Key} instance={InstanceId}", a.Key, a.InstanceId);
        }

        return CommandResults.Success($"Discovered {agents.Count} agent(s).", new CommandResultData { Value = md.ToString(), Format = CommandResultFormat.Markdown, DisplayImmediately = true });
    }

    private static async Task<ExecuteCommandResult> StatusAsync(SynadiaAgentsResource resource, string? filter, ExecuteCommandContext context)
    {
        if (resource.Monitor is not { IsConnected: true } monitor)
        {
            return CommandResults.Failure("Not connected to NATS yet.");
        }

        var targets = await ResolveTargetsAsync(monitor, filter, context.CancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return CommandResults.Failure(NoMatch(filter));
        }

        var results = await Task.WhenAll(targets.Select(async a =>
        {
            try
            {
                var hb = await monitor.Client.StatusAsync(a, TimeSpan.FromSeconds(5), context.CancellationToken).ConfigureAwait(false);
                return (Agent: a, Heartbeat: hb, Error: hb is null ? "no status endpoint / empty reply" : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                return (Agent: a, Heartbeat: (FleetHeartbeat?)null, Error: ex.Message);
            }
        })).ConfigureAwait(false);

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Status of {results.Length} agent instance(s)");
        md.AppendLine();
        md.AppendLine("| Agent | Result | ts | interval_s | session |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var r in results)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| `{Cell(r.Agent.Key)}` | {(r.Error is null ? "✅ ok" : "❌ " + Cell(r.Error))} | {Cell(r.Heartbeat?.Timestamp?.ToString("O", CultureInfo.InvariantCulture))} | {Cell(r.Heartbeat?.IntervalSeconds.ToString(CultureInfo.InvariantCulture))} | {Cell(r.Heartbeat?.Session)} |");
        }

        foreach (var r in results.Where(r => r.Heartbeat is not null))
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## `{r.Agent.Key}`");
            md.AppendLine();
            md.AppendLine("```json");
            md.AppendLine(PrettyJson(r.Heartbeat!.RawJson));
            md.AppendLine("```");
        }

        var ok = results.Count(r => r.Error is null);
        var message = $"{ok}/{results.Length} agent(s) answered status.";
        var data = new CommandResultData { Value = md.ToString(), Format = CommandResultFormat.Markdown, DisplayImmediately = true };
        return ok == results.Length ? CommandResults.Success(message, data) : CommandResults.Failure(message, data.Value, CommandResultFormat.Markdown);
    }

    private static async Task<ExecuteCommandResult> PromptAsync(SynadiaAgentsResource resource, string? filter, ExecuteCommandContext context)
    {
        if (resource.Monitor is not { IsConnected: true } monitor)
        {
            return CommandResults.Failure("Not connected to NATS yet.");
        }

        var prompt = context.Arguments.GetString(PromptArg);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = resource.Options.SmokePrompt;
        }

        var timeoutText = context.Arguments.GetString(TimeoutArg);
        var timeout = double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : resource.Options.PromptTimeout;

        var targets = await ResolveTargetsAsync(monitor, filter, context.CancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return CommandResults.Failure(NoMatch(filter));
        }

        List<DataContent> attachments = [];
        var attachmentText = context.Arguments.GetString(AttachmentArg);
        if (!string.IsNullOrWhiteSpace(attachmentText))
        {
            var path = Path.GetFullPath(attachmentText.Trim().Trim('"'), resource.Options.AttachmentBaseDirectory ?? Environment.CurrentDirectory);
            if (!File.Exists(path))
            {
                return CommandResults.Failure($"Attachment file not found: {path}");
            }

            attachments.Add(await PromptEnvelope.AttachmentFromFileAsync(path, context.CancellationToken).ConfigureAwait(false));
        }

        context.Logger.LogInformation("Prompting {Count} agent(s): {Prompt}", targets.Count, prompt);
        var replies = await Task.WhenAll(targets.Select(async a =>
        {
            try
            {
                return await monitor.Client.PromptAsync(a, prompt, attachments, timeout, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                return new PromptReply(a.Key, false, string.Empty, 0, [], ex.Message, TimeSpan.Zero);
            }
        })).ConfigureAwait(false);

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Prompted {replies.Length} agent instance(s)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"> {prompt}");
        foreach (var attachment in attachments)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $">\n> 📎 `{Cell(attachment.Name)}` ({attachment.MediaType}, {attachment.Data.Length:N0} bytes)");
        }
        md.AppendLine();
        md.AppendLine("| Agent | Result | Chunks | Elapsed |");
        md.AppendLine("|---|---|---|---|");
        foreach (var r in replies)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| `{Cell(r.AgentKey)}` | {(r.Success ? "✅ terminated" : "❌ " + Cell(r.Error))} | {r.ChunkCount} | {r.Elapsed.TotalSeconds:0.00}s |");
        }

        foreach (var r in replies)
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## `{r.AgentKey}`");
            md.AppendLine();
            if (r.StatusUpdates.Count > 0)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"_status:_ {string.Join(" → ", r.StatusUpdates.Select(Cell))}");
                md.AppendLine();
            }

            md.AppendLine(string.IsNullOrEmpty(r.Text) ? "_(no response text)_" : r.Text);
            context.Logger.LogInformation("reply from {Key} ({Chunks} chunks, {Elapsed:0.00}s, success={Success}): {Text}", r.AgentKey, r.ChunkCount, r.Elapsed.TotalSeconds, r.Success, r.Text);
        }

        var ok = replies.Count(r => r.Success);
        var message = $"{ok}/{replies.Length} agent(s) replied and terminated the stream.";
        var data = new CommandResultData { Value = md.ToString(), Format = CommandResultFormat.Markdown, DisplayImmediately = true };
        return ok == replies.Length ? CommandResults.Success(message, data) : CommandResults.Failure(message, data.Value, CommandResultFormat.Markdown);
    }

    private static async Task<IReadOnlyList<AgentInstance>> ResolveTargetsAsync(AgentFleetMonitor monitor, string? filter, CancellationToken cancellationToken)
    {
        var targets = monitor.Select(filter);
        if (targets.Count == 0)
        {
            await monitor.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            targets = monitor.Select(filter);
        }

        return targets;
    }

    private static string NoMatch(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? "No agents discovered on NATS." : $"No discovered agent matches '{filter}'.";

    private static string NatsStall() => "750 ms idle window, 5 s cap";

    private static string Cell(string? value) =>
        string.IsNullOrEmpty(value) ? "—" : value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };
}
