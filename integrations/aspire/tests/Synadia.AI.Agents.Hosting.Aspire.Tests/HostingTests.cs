using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Synadia.AI.Agents.Hosting.Aspire.Client;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire.Tests;

public class HostingTests
{
    [Theory]
    [InlineData("demo-agent/aspire/example", "demo-agent-aspire-example")]
    [InlineData("Py_Agent//Owner/x", "py-agent-owner-x")]
    [InlineData("///", "agent")]
    public void SanitizeName_ProducesValidResourceNames(string key, string expected) =>
        Assert.Equal(expected, AgentFleetMonitor.SanitizeName(key));

    [Theory]
    [InlineData("nats://nats:s3cret@localhost:4222", "nats://nats:****@localhost:4222")]
    [InlineData("nats://localhost:4222", "nats://localhost:4222")]
    public void RedactUserInfo_HidesPassword(string url, string expected) =>
        Assert.Equal(expected, AgentFleetMonitor.RedactUserInfo(url));

    [Fact]
    public void CreateOptions_SplitsUserInfoIntoAuthOpts()
    {
        var opts = NatsAgentFleetClient.CreateOptions("nats://nats:p%40ss@localhost:4222", "test");

        Assert.Equal("nats://localhost:4222", opts.Url);
        Assert.Equal("nats", opts.AuthOpts.Username);
        Assert.Equal("p@ss", opts.AuthOpts.Password);
        Assert.Equal("test", opts.Name);
    }

    [Fact]
    public void TrackedAgent_GoesStaleAfterSlackTimesInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = new TrackedAgent("id")
        {
            LastHeartbeat = new FleetHeartbeat("a", "o", "n", null, "id", now, 5, "0.3", null, "{}"),
            LastHeartbeatReceived = now,
        };

        Assert.Equal("Alive", agent.State(now.AddSeconds(14), slack: 3));
        Assert.Equal("Stale", agent.State(now.AddSeconds(16), slack: 3));
        Assert.Equal("Discovered", new TrackedAgent("x").State(now, 3));
    }

    [Fact]
    public void AddSynadiaAgents_RegistersChildResourceWithCommandsAndHealthCheck()
    {
        var builder = DistributedApplication.CreateBuilder();
        var nats = builder.AddNats("nats");

        var agents = nats.AddSynadiaAgents("fleet", o => o.MinimumAgents = 2);

        Assert.Same(nats.Resource, agents.Resource.Parent);
        Assert.Equal(2, agents.Resource.Options.MinimumAgents);
        var commands = agents.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(c => c.Name).ToArray();
        Assert.Equal(["discover", "status", "prompt", "connect"], commands);
        Assert.Contains(agents.Resource.Annotations.OfType<HealthCheckAnnotation>(), h => h.Key == "fleet_agents_alive");
    }

    [Fact]
    public async Task WithNatsUrl_InjectsNatsUrlAndWaitsForNats()
    {
        var builder = DistributedApplication.CreateBuilder();
        var nats = builder.AddNats("nats");
        var agent = builder.AddTypeScriptAgent("ts", ".", "agent.ts", nats, installPackages: false);

#pragma warning disable CS0618 // Simplest public way to evaluate env callbacks in a unit test.
        var env = await agent.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618

        Assert.True(env.ContainsKey("NATS_URL"));
        Assert.Contains(agent.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == nats.Resource);
    }

    [Fact]
    public async Task AddDotNetAgent_InjectsConnectionStringAndNatsUrl()
    {
        var builder = DistributedApplication.CreateBuilder();
        var nats = builder.AddNats("nats");
        var csproj = Path.Combine(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(csproj, "<Project />");
        try
        {
            var agent = builder.AddDotNetAgent("doc-agent", csproj, nats);

#pragma warning disable CS0618
            var env = await agent.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618

            Assert.True(env.ContainsKey("ConnectionStrings__nats"));
            Assert.True(env.ContainsKey("NATS_URL"));
            Assert.Contains(agent.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == nats.Resource);
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void AddSynadiaAgents_DefaultsToDotNetSdkClient()
    {
        var builder = DistributedApplication.CreateBuilder();
        var agents = builder.AddNats("nats").AddSynadiaAgents();

        Assert.Equal(AgentFleetClientKind.DotNetSdk, agents.Resource.Options.Client);
    }

    [Fact]
    public async Task SdkAgentFleetClient_MapsSdkAgentInfoToDashboardInstance()
    {
        await using var connection = new NATS.Client.Core.NatsConnection();
        await using var client = new Synadia.AI.Agents.AgentsClient(connection);
        var agent = client.Bind(new Synadia.AI.Agents.AgentInfo
        {
            InstanceId = "abc",
            Agent = "dotnet-agent",
            Owner = "aspire",
            Name = "ollama",
            ProtocolVersion = "0.3",
            Description = "d",
            Version = "0.1.0",
            PromptEndpoint = new() { Name = "prompt", Subject = "agents.prompt.dotnet-agent.aspire.ollama" },
            Endpoints =
            [
                new() { Name = "prompt", Subject = "agents.prompt.dotnet-agent.aspire.ollama" },
                new() { Name = "status", Subject = "agents.status.dotnet-agent.aspire.ollama" },
            ],
        });

        var instance = SdkAgentFleetClient.ToInstance(agent);

        Assert.Equal("dotnet-agent/aspire/ollama", instance.Key);
        Assert.Equal("abc", instance.InstanceId);
        Assert.Equal("agents.prompt.dotnet-agent.aspire.ollama", instance.PromptSubject);
        Assert.Equal("agents.status.dotnet-agent.aspire.ollama", instance.StatusSubject);
        Assert.Equal("0.3", instance.ProtocolVersion);
    }
}
