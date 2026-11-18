using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using OllamaSharp.Models.Chat;
using Synadia.AI.Agents.Service;

namespace AgentFleet.DotNetAgent;

/// <summary>Identity and behaviour of the served agent (configuration section <c>Agent</c>).</summary>
public sealed class OllamaAgentOptions
{
    public string Agent { get; set; } = "dotnet-agent";
    public string Owner { get; set; } = "aspire";
    public string Name { get; set; } = "ollama";
    public int HeartbeatSeconds { get; set; } = 5;
    public string Instructions { get; set; } =
        "You are a concise, friendly assistant running as a .NET agent on NATS, orchestrated by Aspire. Keep answers short.";
    public string Model { get; set; } = "";
}

/// <summary>Registers the agent on start and drains it on shutdown.</summary>
internal sealed partial class OllamaAgentWorker(
    INatsConnection nats,
    IChatClient chatClient,
    IOptions<OllamaAgentOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        await using var service = new AgentService(nats, new AgentServiceOptions
        {
            Agent = o.Agent,
            Owner = o.Owner,
            Name = o.Name,
            Description = $".NET agent (AgentService.ServeChatClient) backed by Ollama {o.Model}",
            Version = "0.1.0",
            HeartbeatInterval = TimeSpan.FromSeconds(o.HeartbeatSeconds),
            ExtraMetadata = new Dictionary<string, string> { ["runtime"] = ".NET", ["model"] = o.Model },
            Logger = loggerFactory.CreateLogger<AgentService>(),
        }).ServeChatClient(chatClient, new ChatClientServeOptions
        {
            Instructions = o.Instructions,
            ChatOptions = new ChatOptions
            {
                // qwen3.5 is a reasoning model; skip the think phase so smoke prompts answer in seconds.
                RawRepresentationFactory = _ => new ChatRequest { Think = false },
            },
        });

        await service.StartAsync(stoppingToken);
        LogStarted(loggerFactory.CreateLogger<OllamaAgentWorker>(), o.Agent, o.Owner, o.Name, service.InstanceId, o.Model);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        await service.StopAsync(CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {Agent}/{Owner}/{Name} serving on NATS (instance {InstanceId}, model {Model})")]
    private static partial void LogStarted(ILogger logger, string agent, string owner, string name, string? instanceId, string model);
}
