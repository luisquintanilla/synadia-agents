using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Synadia.AI.Agents.Service;
using Synadia.AI.Agents.Testing;

namespace Synadia.AI.Agents.AgentFramework.Tests;

using AgentResponse = Microsoft.Agents.AI.AgentResponse;

/// <summary>Microsoft Agent Framework adapter, end to end over a real nats-server on a random port.</summary>
[Collection(NatsCollection.Name)]
public sealed class AgentFrameworkTests(NatsServerFixture nats)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Rig : IAsyncDisposable
    {
        public required NatsConnection Host { get; init; }
        public required NatsConnection Caller { get; init; }
        public required IAsyncDisposable Server { get; init; }
        public required AgentsClient Client { get; init; }
        public required Agent Bound { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Host.DisposeAsync();
            await Caller.DisposeAsync();
        }
    }

    private async Task<Rig> ReferenceAsync()
    {
        var host = await nats.ConnectAsync();
        var caller = await nats.ConnectAsync();
        var opts = new ReferenceAgentOptions { Owner = Unique.Owner() };
        var agent = new ReferenceAgent(host, opts);
        await agent.StartAsync();
        var client = new AgentsClient(caller);
        var bound = Assert.Single(await client.DiscoverAsync(new AgentFilter { Owner = opts.Owner }));
        return new Rig { Host = host, Caller = caller, Server = agent, Client = client, Bound = bound };
    }

    private async Task<Rig> ServeAsync(AIAgent agent, AIAgentServeOptions? options = null)
    {
        var host = await nats.ConnectAsync();
        var caller = await nats.ConnectAsync();
        var owner = Unique.Owner();
        var service = new AgentService(host, new AgentServiceOptions { Agent = "maf", Owner = owner, Name = "assistant", Description = "served MAF agent" })
            .ServeAIAgent(agent, options);
        await service.StartAsync();
        var client = new AgentsClient(caller);
        var bound = Assert.Single(await client.DiscoverAsync(new AgentFilter { Owner = owner }));
        return new Rig { Host = host, Caller = caller, Server = service, Client = client, Bound = bound };
    }

    private static AgentQueryContent SingleQuery(AgentResponse response)
        => Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<AgentQueryContent>());

    [Fact]
    public async Task NatsAIAgentRunsAgainstReferenceAgentAndKeepsRemoteConversation()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent();
        Assert.Equal(rig.Bound.Info.Name, agent.Name);
        Assert.Equal(rig.Bound.Info.InstanceId, agent.Id);
        Assert.Same(rig.Bound, agent.GetService<Agent>());

        var session = await agent.CreateSessionAsync();
        var first = await agent.RunAsync("hello", session);
        Assert.Equal("echo: hello", first.Text);
        Assert.Equal(agent.Id, first.AgentId);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync("again", session)) updates.Add(u);
        Assert.Equal("echo: again (turn 2; you previously said: \"hello\")", updates.ToAgentResponse().Text);
    }

    [Fact]
    public async Task RemoteQueryPausesTheRunAndIsAnsweredOnTheNextRun()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent();
        var session = (NatsAgentSession)await agent.CreateSessionAsync();

        var asked = await agent.RunAsync("ask favorite color?", session);
        var query = SingleQuery(asked);
        Assert.Equal("favorite color?", query.Prompt);
        Assert.Same(query, session.PendingQuery);

        var answered = await agent.RunAsync(new ChatMessage(ChatRole.User, [query.CreateResponse("teal")]), session);
        Assert.Equal("you answered: teal", answered.Text);
        Assert.Null(session.PendingQuery);
    }

    [Fact]
    public async Task PlainUserTextAnswersAPendingQueryWhenStreaming()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent();
        var session = await agent.CreateSessionAsync();

        AgentQueryContent? query = null;
        await foreach (var u in agent.RunStreamingAsync("ask size?", session))
        {
            query ??= u.Contents.OfType<AgentQueryContent>().FirstOrDefault();
        }
        Assert.Equal("size?", query?.Prompt);

        var text = "";
        await foreach (var u in agent.RunStreamingAsync("large", session)) text += u.Text;
        Assert.Equal("you answered: large", text);
    }

    [Fact]
    public async Task ResumingWithoutAnAnswerThrowsAndThePendingQueryCanBeCancelled()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent();
        var session = (NatsAgentSession)await agent.CreateSessionAsync();
        SingleQuery(await agent.RunAsync("ask anything?", session));

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("")]), session));
        Assert.NotNull(session.PendingQuery);

        await session.CancelPendingQueryAsync();
        Assert.Null(session.PendingQuery);
        Assert.StartsWith("echo: next", (await agent.RunAsync("next", session)).Text);
    }

    [Fact]
    public async Task QueryHandlerAnswersInline()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent(new NatsAIAgentOptions
        {
            QueryHandler = (q, _) => ValueTask.FromResult<string?>(q.Prompt == "pet?" ? "cat" : "?"),
        });
        Assert.Equal("you answered: cat", (await agent.RunAsync("ask pet?")).Text);
    }

    [Fact]
    public async Task SessionSerializationRoundTripsTheSessionId()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent();
        var session = (NatsAgentSession)await agent.CreateSessionAsync();
        Assert.NotEmpty(session.SessionId);

        JsonElement state = await agent.SerializeSessionAsync(session);
        var restored = Assert.IsType<NatsAgentSession>(await agent.DeserializeSessionAsync(state));
        Assert.Equal(session.SessionId, restored.SessionId);
        Assert.NotEqual(session.SessionId, ((NatsAgentSession)await agent.CreateSessionAsync()).SessionId);
    }

    [Fact]
    public async Task NatsAgentWorksAsAToolOfALocalChatClientAgent()
    {
        await using var rig = await ReferenceAsync();
        var remote = rig.Bound.AsAIAgent(new NatsAIAgentOptions { Name = "echo_agent", Description = "Echoes text" });
        var tool = remote.AsAIFunction();
        var model = new ScriptedChatClient((messages, _) =>
        {
            if (messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            {
                return [new ChatResponseUpdate(ChatRole.Assistant, $"tool said: {result.Result}")];
            }
            return [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call1", tool.Name, new Dictionary<string, object?> { ["query"] = "ping" })])];
        });
        var local = new ChatClientAgent(model, name: "local", tools: [tool]);

        var response = await local.RunAsync("use the echo agent");
        Assert.Contains("tool said: echo: ping", response.Text);
    }

    [Fact]
    public async Task ServeAIAgentKeepsOneMafSessionPerCallerSession()
    {
        var model = new ScriptedChatClient((messages, _) =>
        {
            var users = messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text).ToList();
            return [new ChatResponseUpdate(ChatRole.Assistant, [new AgentStatusContent("thinking")]),
                new ChatResponseUpdate(ChatRole.Assistant, $"seen {users.Count}: {string.Join(",", users)}")];
        });
        await using var rig = await ServeAsync(new ChatClientAgent(model, name: "counter"));
        var caller = rig.Bound.AsAIAgent();
        Assert.Equal("assistant", caller.Name);
        Assert.Equal("served MAF agent", caller.Description);

        var a = await caller.CreateSessionAsync();
        var b = await caller.CreateSessionAsync();
        Assert.Equal("seen 1: one", (await caller.RunAsync("one", a)).Text);
        var second = await caller.RunAsync("two", a);
        Assert.Equal("seen 2: one,two", second.Text);
        Assert.Contains(second.Messages.SelectMany(m => m.Contents).OfType<AgentStatusContent>(), s => s.Status == "thinking");
        Assert.Equal("seen 1: other", (await caller.RunAsync("other", b)).Text);
    }

    [Fact]
    public async Task ServedToolApprovalBecomesARemoteQueryAnsweredAcrossRuns()
    {
        var calls = 0;
        var weather = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string city) =>
        {
            Interlocked.Increment(ref calls);
            return $"sunny in {city}";
        }, "get_weather"));
        var model = new ScriptedChatClient((messages, _) =>
        {
            if (messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault() is { } result)
            {
                return [new ChatResponseUpdate(ChatRole.Assistant, $"weather: {result.Result}")];
            }
            return [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("w1", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris" })])];
        });
        await using var rig = await ServeAsync(new ChatClientAgent(model, name: "weather", tools: [weather]));
        var caller = rig.Bound.AsAIAgent();
        var session = await caller.CreateSessionAsync();

        var asked = await caller.RunAsync("weather in Paris?", session);
        var query = SingleQuery(asked);
        Assert.Equal("Allow tool 'get_weather' with arguments {\"city\":\"Paris\"}? (yes/no)", query.Prompt);
        Assert.Equal(0, calls);

        var answered = await caller.RunAsync("yes", session);
        Assert.Equal("weather: sunny in Paris", answered.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OnRunLetsOutOfBandCallbacksAskTheCaller()
    {
        ServedRun? active = null;
        var model = new ScriptedChatClient((_, _) =>
        {
            // Stands in for a provider callback (e.g. a permission handler) that runs outside MAF's content stream.
            var reply = active!.AskAsync("Allow shell: echo hi? (yes/no)").GetAwaiter().GetResult();
            return [new ChatResponseUpdate(ChatRole.Assistant, $"answer: {reply?.Prompt}")];
        });
        var ended = false;
        await using var rig = await ServeAsync(new ChatClientAgent(model, name: "oob"), new AIAgentServeOptions
        {
            OnRun = run =>
            {
                active = run;
                return new Callback(() => ended = true);
            },
        });
        var caller = rig.Bound.AsAIAgent(new NatsAIAgentOptions
        {
            QueryHandler = (q, _) => ValueTask.FromResult<string?>(q.Prompt.StartsWith("Allow shell", StringComparison.Ordinal) ? "no" : "?"),
        });

        Assert.Equal("answer: no", (await caller.RunAsync("go")).Text);
        Assert.NotNull(active!.Session);
        Assert.True(ended);
    }

    private sealed class Callback(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public async Task ServedNatsAgentRelaysNestedQueriesToTheOuterCaller()
    {
        await using var reference = await ReferenceAsync();
        await using var proxy = await ServeAsync(reference.Bound.AsAIAgent());
        var caller = proxy.Bound.AsAIAgent();
        var session = await caller.CreateSessionAsync();

        var query = SingleQuery(await caller.RunAsync("ask nested?", session));
        Assert.Equal("nested?", query.Prompt);
        Assert.Equal("you answered: deep", (await caller.RunAsync(new ChatMessage(ChatRole.User, [query.CreateResponse("deep")]), session)).Text);
    }

    [Fact]
    public async Task WorkflowSurfacesRemoteQueryAsRequestInfoInFunctionCallMode()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent(new NatsAIAgentOptions { QueryMode = NatsQueryMode.FunctionCall });
        var workflow = AgentWorkflowBuilder.BuildSequential(agent);
        using var timeout = new CancellationTokenSource(Wait);

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, "ask favorite fruit?") },
            cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        string? asked = null;
        List<ChatMessage>? output = null;
        await foreach (var evt in run.WatchStreamAsync(timeout.Token))
        {
            if (evt is RequestInfoEvent info && info.Request.TryGetDataAs<FunctionCallContent>(out var call))
            {
                Assert.Equal(NatsAIAgent.QueryFunctionName, call.Name);
                asked = call.Arguments?["prompt"]?.ToString();
                await run.SendResponseAsync(info.Request.CreateResponse(new FunctionResultContent(call.CallId, "mango")));
            }
            else if (evt is WorkflowOutputEvent { Data: List<ChatMessage> messages })
            {
                output = messages;
                break;
            }
            else if (evt is WorkflowErrorEvent or ExecutorFailedEvent)
            {
                Assert.Fail($"workflow failed: {evt}");
            }
        }
        Assert.Equal("favorite fruit?", asked);
        Assert.NotNull(output);
        Assert.Contains(output, m => m.Text.Contains("you answered: mango", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolApprovalModeSurfacesApprovalRequests()
    {
        await using var rig = await ReferenceAsync();
        var agent = rig.Bound.AsAIAgent(new NatsAIAgentOptions { QueryMode = NatsQueryMode.ToolApproval });
        var session = await agent.CreateSessionAsync();

        var asked = await agent.RunAsync("ask proceed?", session);
        var approval = Assert.Single(asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        var call = Assert.IsType<FunctionCallContent>(approval.ToolCall);
        Assert.Equal("proceed?", call.Arguments?["prompt"]);

        var resumed = await agent.RunAsync(new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]), session);
        var contents = resumed.Messages.SelectMany(m => m.Contents).ToList();
        Assert.Contains(contents, c => c is FunctionResultContent r && r.CallId == call.CallId);
        Assert.EndsWith("you answered: yes", resumed.Text);
    }

    [Fact]
    public async Task HostedServiceServesAKeyedMafAgent()
    {
        var host = await nats.ConnectAsync();
        var owner = Unique.Owner();
        var services = new ServiceCollection();
        services.AddSingleton<INatsConnection>(host);
        services.AddKeyedSingleton<AIAgent>("greeter", (_, _) => new ChatClientAgent(
            new ScriptedChatClient((m, _) => [new ChatResponseUpdate(ChatRole.Assistant, $"hi {m.Last().Text}")]), name: "greeter"));
        services.AddNatsAgentServer("greeter", _ => new AgentServiceOptions { Agent = "maf", Owner = owner, Name = "greeter" });
        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();
        foreach (var h in hosted) await h.StartAsync(CancellationToken.None);
        try
        {
            var caller = await nats.ConnectAsync();
            await using var client = new AgentsClient(caller);
            var bound = Assert.Single(await client.DiscoverAsync(new AgentFilter { Owner = owner }));
            Assert.Equal("hi bob", (await bound.AsAIAgent().RunAsync("bob")).Text);
            await caller.DisposeAsync();
        }
        finally
        {
            foreach (var h in hosted) await h.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void AnswerResolutionPrefersTypedResponsesOverText()
    {
        var chunk = new AgentQueryChunk("q1", "_INBOX.q1", "color?", [], null);
        var query = new AgentQueryContent(chunk);
        Assert.Equal("blue", NatsAIAgent.ResolveAnswer(query, [new ChatMessage(ChatRole.User, [new TextContent("ignored"), query.CreateResponse("blue")])])?.Prompt);
        Assert.Equal("red", NatsAIAgent.ResolveAnswer(query, [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("q1", "red")])])?.Prompt);
        Assert.Equal("green", NatsAIAgent.ResolveAnswer(query, [new ChatMessage(ChatRole.User, "green")])?.Prompt);
        Assert.Null(NatsAIAgent.ResolveAnswer(query, [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("other", "x")])]));
    }

    internal sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, List<ChatResponseUpdate>> script) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(script(messages.ToList(), options).ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var u in script(messages.ToList(), options)) yield return u;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
