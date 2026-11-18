using System.Text;
using Synadia.AI.Agents.Hosting.Aspire.Protocol;

namespace Synadia.AI.Agents.Hosting.Aspire.Tests;

// Payloads captured verbatim from the TS and Python reference agents running under the examples/aspire-agent-fleet AppHost.
public class FleetProtocolTests
{
    private const string TsInfo = """{"type":"io.nats.micro.v1.info_response","name":"agents","id":"qa8UWkLwsMCh23028u1whB","version":"0.0.1","description":"reference agent for @synadia-ai/agents examples","metadata":{"agent":"demo-agent","owner":"aspire","protocol_version":"0.3"},"endpoints":[{"subject":"agents.prompt.demo-agent.aspire.example","metadata":{"max_payload":"1MB","attachments_ok":"true","min_sender_trust":"any"},"name":"prompt","queue_group":"agents"},{"subject":"agents.status.demo-agent.aspire.example","name":"status","queue_group":"agents"}]}""";

    private const string PyInfo = """{"type": "io.nats.micro.v1.info_response", "id": "QxX2XLA1865qCPOwyy0Zvr", "name": "agents", "version": "0.5.0", "description": "python reference agent", "endpoints": [{"name": "prompt", "subject": "agents.prompt.py-demo-agent.aspire.python", "queue_group": "agents", "metadata": {"max_payload": "1MB", "attachments_ok": "true", "min_sender_trust": "any"}}, {"name": "status", "subject": "agents.status.py-demo-agent.aspire.python", "queue_group": "agents", "metadata": null}], "metadata": {"agent": "py-demo-agent", "owner": "aspire", "session": "python", "protocol_version": "0.3"}}""";

    private const string TsHeartbeat = """{"agent":"demo-agent","owner":"aspire","instance_id":"qa8UWkLwsMCh23028u1whB","ts":"2026-11-18T04:47:15.354Z","interval_s":5}""";

    private const string PyHeartbeat = """{"agent":"py-demo-agent","owner":"aspire","session":"python","instance_id":"QxX2XLA1865qCPOwyy0Zvr","ts":"2026-11-18T04:47:17Z","interval_s":5,"protocol_version":"0.3","endpoints":{"prompt":{"subject":"agents.prompt.py-demo-agent.aspire.python","metadata":{"max_payload":"1MB","attachments_ok":"true","min_sender_trust":"any"}}}}""";

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ParseServiceInfo_TypeScriptAgent()
    {
        var a = FleetProtocol.ParseServiceInfo(B(TsInfo));

        Assert.NotNull(a);
        Assert.Equal("demo-agent/aspire/example", a.Key);
        Assert.Equal("qa8UWkLwsMCh23028u1whB", a.InstanceId);
        Assert.Equal("0.3", a.ProtocolVersion);
        Assert.Null(a.Session);
        Assert.Equal("agents.prompt.demo-agent.aspire.example", a.PromptSubject);
        Assert.Equal("agents.status.demo-agent.aspire.example", a.StatusSubject);
    }

    [Fact]
    public void ParseServiceInfo_PythonAgent_WithSessionAndNullEndpointMetadata()
    {
        var a = FleetProtocol.ParseServiceInfo(B(PyInfo));

        Assert.NotNull(a);
        Assert.Equal("py-demo-agent/aspire/python", a.Key);
        Assert.Equal("python", a.Session);
        Assert.Equal("0.5.0", a.Version);
    }

    [Theory]
    [InlineData("""{"name":"other","metadata":{"agent":"a","owner":"o","protocol_version":"0.3"},"endpoints":[{"name":"prompt","subject":"agents.prompt.a.o.n"}]}""")]
    [InlineData("""{"name":"agents","metadata":{"owner":"o","protocol_version":"0.3"},"endpoints":[{"name":"prompt","subject":"agents.prompt.a.o.n"}]}""")]
    [InlineData("""{"name":"agents","metadata":{"agent":"a","owner":"o"},"endpoints":[{"name":"prompt","subject":"agents.prompt.a.o.n"}]}""")]
    [InlineData("""{"name":"agents","metadata":{"agent":"a","owner":"o","protocol_version":"0.3"},"endpoints":[]}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void ParseServiceInfo_DropsNonCompliantRecords(string json) =>
        Assert.Null(FleetProtocol.ParseServiceInfo(B(json)));

    [Fact]
    public void ParseHeartbeat_TakesNameFromSubjectWhenPayloadOmitsIt()
    {
        var hb = FleetProtocol.ParseHeartbeat(B(TsHeartbeat), "agents.hb.demo-agent.aspire.example");

        Assert.NotNull(hb);
        Assert.Equal("demo-agent/aspire/example", hb.Key);
        Assert.Equal(5, hb.IntervalSeconds);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 4, 47, 15, 354, TimeSpan.Zero), hb.Timestamp);
        Assert.Null(hb.PromptSubject);
    }

    [Fact]
    public void ParseHeartbeat_FallsBackToPromptSubjectForName()
    {
        var hb = FleetProtocol.ParseHeartbeat(B(PyHeartbeat));

        Assert.NotNull(hb);
        Assert.Equal("py-demo-agent/aspire/python", hb.Key);
        Assert.Equal("python", hb.Session);
        Assert.Equal("0.3", hb.ProtocolVersion);
    }

    [Theory]
    [InlineData("""{"owner":"o","instance_id":"x"}""")]
    [InlineData("""[]""")]
    [InlineData("{")]
    public void ParseHeartbeat_RejectsInvalidPayloads(string json) =>
        Assert.Null(FleetProtocol.ParseHeartbeat(B(json), "agents.hb.a.o.n"));

    [Fact]
    public void ParseChunk_HandlesStringAndObjectData()
    {
        Assert.Equal(new FleetProtocol.Chunk("text", "hi"), FleetProtocol.ParseChunk(B("""{"type":"text","data":"hi"}""")));
        Assert.Equal(new FleetProtocol.Chunk("status", """{"x":1}"""), FleetProtocol.ParseChunk(B("""{"type":"status","data":{"x":1}}""")));
        Assert.Null(FleetProtocol.ParseChunk(B("""{"data":"no type"}""")));
    }

    [Fact]
    public void EncodePrompt_EscapesJson() =>
        Assert.Equal("""{"prompt":"say \u0022hi\u0022"}""", Encoding.UTF8.GetString(FleetProtocol.EncodePrompt("say \"hi\"")));
}
