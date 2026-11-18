using System.Text.Json;

namespace Synadia.AI.Agents.Hosting.Aspire.Protocol;

/// <summary>
/// A protocol-compliant agent instance as assembled from a <c>$SRV.INFO.agents</c> record
/// (spec §4.3). Mirrors <c>AgentInfo</c> in the TypeScript SDK.
/// </summary>
public sealed record AgentInstance(
    string InstanceId,
    string Agent,
    string Owner,
    string Name,
    string? Session,
    string ProtocolVersion,
    string Description,
    string Version,
    string PromptSubject,
    string? StatusSubject,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary><c>agent/owner/name</c> — the stable address of the instance.</summary>
    public string Key => FleetProtocol.Key(Agent, Owner, Name);
}

/// <summary>Heartbeat / status payload (spec §8).</summary>
public sealed record FleetHeartbeat(
    string Agent,
    string Owner,
    string? Name,
    string? Session,
    string InstanceId,
    DateTimeOffset? Timestamp,
    double IntervalSeconds,
    string? ProtocolVersion,
    string? PromptSubject,
    string RawJson)
{
    public string Key => FleetProtocol.Key(Agent, Owner, Name ?? string.Empty);
}

/// <summary>Outcome of one prompt round-trip.</summary>
public sealed record PromptReply(
    string AgentKey,
    bool Success,
    string Text,
    int ChunkCount,
    IReadOnlyList<string> StatusUpdates,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Wire-format constants and parsers for the Synadia Agent Protocol for NATS (v0.3).
/// Canonical spec: https://github.com/synadia-ai/synadia-agent-sdk-docs
/// </summary>
public static class FleetProtocol
{
    public const string ServiceName = "agents";
    public const string PromptEndpointName = "prompt";
    public const string StatusEndpointName = "status";
    public const string InfoSubject = "$SRV.INFO." + ServiceName;
    public const string PingSubject = "$SRV.PING." + ServiceName;
    public const string HeartbeatWildcard = "agents.hb.>";
    public const string ServiceErrorCodeHeader = "Nats-Service-Error-Code";
    public const string ServiceErrorHeader = "Nats-Service-Error";

    public static string Key(string agent, string owner, string name) => $"{agent}/{owner}/{name}";

    /// <summary>
    /// Converts one raw <c>$SRV.INFO</c> reply into an <see cref="AgentInstance"/>, or returns
    /// <c>null</c> for records that are not protocol-compliant agents (they are silently dropped,
    /// same as the TS and Python SDKs).
    /// </summary>
    public static AgentInstance? ParseServiceInfo(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetString(root, "name") != ServiceName)
            {
                return null;
            }

            var metadata = ReadStringMap(root, "metadata");
            if (!metadata.TryGetValue("agent", out var agent) ||
                !metadata.TryGetValue("owner", out var owner) ||
                !metadata.TryGetValue("protocol_version", out var protocolVersion))
            {
                return null;
            }

            string? promptSubject = null;
            string? statusSubject = null;
            if (root.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var endpoint in endpoints.EnumerateArray())
                {
                    switch (GetString(endpoint, "name"))
                    {
                        case PromptEndpointName:
                            promptSubject = GetString(endpoint, "subject");
                            break;
                        case StatusEndpointName:
                            statusSubject = GetString(endpoint, "subject");
                            break;
                    }
                }
            }

            if (string.IsNullOrEmpty(promptSubject))
            {
                return null;
            }

            // §4.3 step 3: instance name is the 5th token of agents.prompt.<agent>.<owner>.<name>.
            var tokens = promptSubject.Split('.');
            var name = tokens.Length > 4 ? tokens[4] : string.Empty;
            metadata.TryGetValue("session", out var session);

            return new AgentInstance(
                InstanceId: GetString(root, "id") ?? string.Empty,
                Agent: agent,
                Owner: owner,
                Name: name,
                Session: string.IsNullOrEmpty(session) ? null : session,
                ProtocolVersion: protocolVersion,
                Description: GetString(root, "description") ?? string.Empty,
                Version: GetString(root, "version") ?? string.Empty,
                PromptSubject: promptSubject,
                StatusSubject: string.IsNullOrEmpty(statusSubject) ? null : statusSubject,
                Metadata: metadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a heartbeat (<c>agents.hb.&lt;agent&gt;.&lt;owner&gt;.&lt;name&gt;</c>) or status reply.
    /// <paramref name="subject"/> supplies the instance name when the payload omits it.
    /// </summary>
    public static FleetHeartbeat? ParseHeartbeat(ReadOnlySpan<byte> json, string? subject = null)
    {
        if (json.IsEmpty)
        {
            return null;
        }

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(json);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var agent = GetString(root, "agent");
            var owner = GetString(root, "owner");
            var instanceId = GetString(root, "instance_id");
            if (agent is null || owner is null || instanceId is null)
            {
                return null;
            }

            string? promptSubject = null;
            if (root.TryGetProperty("endpoints", out var endpoints) &&
                endpoints.ValueKind == JsonValueKind.Object &&
                endpoints.TryGetProperty(PromptEndpointName, out var prompt))
            {
                promptSubject = GetString(prompt, "subject");
            }

            var name = NameFromSubject(subject, 4) ?? NameFromSubject(promptSubject, 4);

            DateTimeOffset? ts = null;
            if (GetString(root, "ts") is { } tsText && DateTimeOffset.TryParse(tsText, out var parsed))
            {
                ts = parsed;
            }

            var interval = root.TryGetProperty("interval_s", out var iv) && iv.ValueKind == JsonValueKind.Number
                ? iv.GetDouble()
                : 0;

            return new FleetHeartbeat(
                Agent: agent,
                Owner: owner,
                Name: name,
                Session: GetString(root, "session"),
                InstanceId: instanceId,
                Timestamp: ts,
                IntervalSeconds: interval,
                ProtocolVersion: GetString(root, "protocol_version"),
                PromptSubject: promptSubject,
                RawJson: raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One decoded chunk of a prompt reply stream (spec §6).</summary>
    public readonly record struct Chunk(string Type, string? Data);

    public static Chunk? ParseChunk(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type)
            {
                return null;
            }

            string? data = null;
            if (root.TryGetProperty("data", out var d))
            {
                data = d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
            }

            return new Chunk(type, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] EncodePrompt(string prompt) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["prompt"] = prompt });

    private static string? NameFromSubject(string? subject, int index)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var tokens = subject.Split('.');
        return tokens.Length > index && tokens[index].Length > 0 ? tokens[index] : null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    result[entry.Name] = entry.Value.GetString()!;
                }
            }
        }

        return result;
    }
}
