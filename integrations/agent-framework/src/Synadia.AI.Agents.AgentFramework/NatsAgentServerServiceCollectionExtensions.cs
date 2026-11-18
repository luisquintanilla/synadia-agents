using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Synadia.AI.Agents.Service;

namespace Synadia.AI.Agents.AgentFramework;

/// <summary>Generic-host registration for serving MAF agents on NATS.</summary>
public static class NatsAgentServerServiceCollectionExtensions
{
    /// <summary>
    /// Serve the keyed <see cref="AIAgent"/> registered under <paramref name="agentName"/> (e.g. by MAF hosting's
    /// <c>builder.AddAIAgent(name, ...)</c>) on NATS for the lifetime of the host. The <see cref="INatsConnection"/> is
    /// resolved from DI.
    /// </summary>
    public static IServiceCollection AddNatsAgentServer(this IServiceCollection services, string agentName,
        Func<IServiceProvider, AgentServiceOptions> serviceOptions, AIAgentServeOptions? serveOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return services.AddNatsAgentServer(serviceOptions, sp => sp.GetRequiredKeyedService<AIAgent>(agentName), serveOptions);
    }

    /// <summary>Serve the <see cref="AIAgent"/> produced by <paramref name="agent"/> on NATS for the lifetime of the host.</summary>
    public static IServiceCollection AddNatsAgentServer(this IServiceCollection services, Func<IServiceProvider, AgentServiceOptions> serviceOptions,
        Func<IServiceProvider, AIAgent> agent, AIAgentServeOptions? serveOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceOptions);
        ArgumentNullException.ThrowIfNull(agent);
        services.AddSingleton<IHostedService>(sp => new NatsAgentServerHostedService(
            sp.GetRequiredService<INatsConnection>(), serviceOptions(sp), () => agent(sp), serveOptions));
        return services;
    }

    private sealed class NatsAgentServerHostedService(INatsConnection connection, AgentServiceOptions options, Func<AIAgent> agent,
        AIAgentServeOptions? serveOptions) : IHostedService, IAsyncDisposable
    {
        private AgentService? _service;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _service = new AgentService(connection, options).ServeAIAgent(agent(), serveOptions);
            await _service.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_service is { } s) await s.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_service is { } s) await s.DisposeAsync().ConfigureAwait(false);
            _service = null;
        }
    }
}
