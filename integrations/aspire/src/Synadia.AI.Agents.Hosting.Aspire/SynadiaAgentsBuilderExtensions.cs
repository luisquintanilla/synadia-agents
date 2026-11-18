using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synadia.AI.Agents.Hosting.Aspire;
using Synadia.AI.Agents.Hosting.Aspire.Client;

namespace Aspire.Hosting;

/// <summary>
/// Aspire hosting extensions for the Synadia Agent Protocol for NATS.
/// </summary>
public static class SynadiaAgentsBuilderExtensions
{
    /// <summary>The environment variable every agent harness in this repo reads to find NATS.</summary>
    public const string NatsUrlEnvironmentVariable = "NATS_URL";

    /// <summary>
    /// Adds a dashboard resource that watches <paramref name="nats"/> for Synadia agents: live heartbeats,
    /// per-agent child rows, a health check (healthy once <see cref="SynadiaAgentsOptions.MinimumAgents"/>
    /// agents heartbeat), and Discover / Status / Prompt (smoke) commands.
    /// </summary>
    public static IResourceBuilder<SynadiaAgentsResource> AddSynadiaAgents(
        this IResourceBuilder<NatsServerResource> nats,
        string name = "agents",
        Action<SynadiaAgentsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(nats);
        var builder = nats.ApplicationBuilder;
        var resource = new SynadiaAgentsResource(name, nats.Resource);
        configure?.Invoke(resource.Options);

        return AddSynadiaAgentsCore(builder, resource, (url, ct) => resource.Options.Client switch
        {
            AgentFleetClientKind.RawNats => NatsAgentFleetClient.ConnectAsync(url, ct),
            _ => SdkAgentFleetClient.ConnectAsync(url, ct),
        });
    }

    internal static IResourceBuilder<SynadiaAgentsResource> AddSynadiaAgentsCore(
        IDistributedApplicationBuilder builder,
        SynadiaAgentsResource resource,
        AgentFleetClientFactory clientFactory)
    {
        var healthCheckKey = $"{resource.Name}_agents_alive";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new AgentFleetHealthCheck(resource),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));

        var resourceBuilder = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "SynadiaAgents",
                State = KnownResourceStates.Waiting,
                Properties = [new("agents.minimum", resource.Options.MinimumAgents.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            })
            .WithHealthCheck(healthCheckKey)
            .ExcludeFromManifest();

        SynadiaAgentsCommands.AddFleetCommands(resourceBuilder);

        builder.Eventing.Subscribe<InitializeResourceEvent>(resource, (e, ct) =>
        {
            var logger = e.Logger;
            var notifications = e.Notifications;
            var lifetime = e.Services.GetRequiredService<IHostApplicationLifetime>();

            _ = Task.Run(async () =>
            {
                var stopping = lifetime.ApplicationStopping;
                try
                {
                    await notifications.WaitForResourceHealthyAsync(resource.Parent.Name, stopping).ConfigureAwait(false);
                    await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);

                    var url = await resource.Parent.UriExpression.GetValueAsync(stopping).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"NATS resource '{resource.Parent.Name}' has no URI.");

                    var monitor = new AgentFleetMonitor(resource, notifications, logger);
                    resource.Monitor = monitor;
                    stopping.Register(() => monitor.DisposeAsync().AsTask().GetAwaiter().GetResult());
                    await monitor.StartAsync(url, clientFactory, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent fleet monitor failed to start");
                    await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            return Task.CompletedTask;
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Injects <c>NATS_URL</c> (credentials included) from <paramref name="nats"/> and waits for NATS to be healthy.
    /// Every harness in this repo (TS + Python SDKs, agent-web-ui, pi-headless, dspy) reads <c>NATS_URL</c>.
    /// </summary>
    public static IResourceBuilder<T> WithNatsUrl<T>(this IResourceBuilder<T> builder, IResourceBuilder<NatsServerResource> nats)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(nats);
        return builder
            .WithEnvironment(NatsUrlEnvironmentVariable, nats.Resource.UriExpression)
            .WaitFor(nats);
    }

    /// <summary>
    /// Adds a TypeScript agent run by Bun (<c>bun &lt;scriptPath&gt;</c>) wired to NATS.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Resource name.</param>
    /// <param name="appDirectory">Directory containing <c>package.json</c>.</param>
    /// <param name="scriptPath">Entry script relative to <paramref name="appDirectory"/>.</param>
    /// <param name="nats">NATS server the agent connects to.</param>
    /// <param name="installPackages">Run <c>bun install</c> before start. Disable when dependencies are prebuilt (e.g. <c>file:</c>-linked SDKs).</param>
    public static IResourceBuilder<JavaScript.JavaScriptAppResource> AddTypeScriptAgent(
        this IDistributedApplicationBuilder builder,
        string name,
        string appDirectory,
        string scriptPath,
        IResourceBuilder<NatsServerResource> nats,
        bool installPackages = true)
    {
        return builder.AddBunApp(name, appDirectory, scriptPath)
            .WithBun(install: installPackages)
            .WithNatsUrl(nats)
            .WithOtlpExporter();
    }

    /// <summary>
    /// Adds a Python agent script wired to NATS, with unbuffered output so logs stream into the dashboard.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Resource name.</param>
    /// <param name="appDirectory">Directory containing <c>pyproject.toml</c> / <c>.venv</c>.</param>
    /// <param name="scriptPath">Script relative to <paramref name="appDirectory"/>.</param>
    /// <param name="nats">NATS server the agent connects to.</param>
    /// <param name="installPackages">
    /// <c>true</c> keeps Aspire's default installer (<c>uv sync</c> when <c>uv.lock</c> exists);
    /// <c>false</c> runs the existing <c>.venv</c> as-is.
    /// </param>
    public static IResourceBuilder<PythonAppResource> AddPythonAgent(
        this IDistributedApplicationBuilder builder,
        string name,
        string appDirectory,
        string scriptPath,
        IResourceBuilder<NatsServerResource> nats,
        bool installPackages = true)
    {
        var app = builder.AddPythonApp(name, appDirectory, scriptPath)
            .WithEnvironment("PYTHONUNBUFFERED", "1")
            .WithEnvironment("PYTHONIOENCODING", "utf-8")
            .WithNatsUrl(nats);

        if (!installPackages)
        {
            app.WithPip(install: false).WithVirtualEnvironment(".venv", createIfNotExists: false);
        }

        return app;
    }

    /// <summary>
    /// Adds a .NET agent project wired to NATS two ways: <c>ConnectionStrings__&lt;nats&gt;</c> (for
    /// <c>Aspire.NATS.Net</c>'s <c>builder.AddNatsClient("&lt;nats&gt;")</c>) and <c>NATS_URL</c> (for plain
    /// <c>NatsConnection</c> / repo-convention harnesses). Waits for NATS to be healthy.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddDotNetAgent<TProject>(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<NatsServerResource> nats)
        where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProject<TProject>(name).WithNatsReference(nats);
    }

    /// <summary>
    /// Path-based overload of <see cref="AddDotNetAgent{TProject}"/> for projects outside the AppHost's
    /// reference graph (e.g. an agent on another branch that may or may not be checked out).
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddDotNetAgent(
        this IDistributedApplicationBuilder builder,
        string name,
        string projectPath,
        IResourceBuilder<NatsServerResource> nats)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProject(name, projectPath).WithNatsReference(nats);
    }

    private static IResourceBuilder<ProjectResource> WithNatsReference(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<NatsServerResource> nats)
    {
        ArgumentNullException.ThrowIfNull(nats);
        return project
            .WithReference(nats)
            .WithNatsUrl(nats);
    }
}

/// <summary>Healthy once at least <see cref="SynadiaAgentsOptions.MinimumAgents"/> agents are heartbeating.</summary>
internal sealed class AgentFleetHealthCheck(SynadiaAgentsResource resource) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (resource.Monitor is not { IsConnected: true } monitor)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Not connected to NATS yet."));
        }

        var alive = monitor.AliveCount;
        var minimum = resource.Options.MinimumAgents;
        return Task.FromResult(alive >= minimum
            ? HealthCheckResult.Healthy($"{alive} agent(s) heartbeating.")
            : HealthCheckResult.Unhealthy($"{alive}/{minimum} agent(s) heartbeating."));
    }
}
