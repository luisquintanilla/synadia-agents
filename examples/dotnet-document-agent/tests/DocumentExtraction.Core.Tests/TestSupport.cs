using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Synadia.Examples.DocumentAgent.Tests;

internal static class TestFiles
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static DataContent Load(string name, string? mediaType = null) =>
        new(File.ReadAllBytes(PathOf(name)), mediaType ?? MediaTypes.Resolve(null, name)) { Name = name };

    public static async Task<(string Text, List<DocumentAgentStatus> Statuses)> CollectAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var text = new System.Text.StringBuilder();
        var statuses = new List<DocumentAgentStatus>();
        await foreach (var update in updates)
        {
            if (DocumentAgentStatus.TryGet(update, out var status))
            {
                statuses.Add(status);
            }
            else
            {
                text.Append(update.Text);
            }
        }

        return (text.ToString(), statuses);
    }
}

/// <summary>Deterministic bag-of-words embeddings: lets RAG tests run without a model.</summary>
internal sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 256;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var vector = new float[Dimensions];
            foreach (var token in value.ToLowerInvariant().Split([' ', '\n', '\r', '\t', '.', ',', '|', ':', ';', '?', '(', ')', '#', '-'], StringSplitOptions.RemoveEmptyEntries))
            {
                vector[(int)((uint)StringComparer.Ordinal.GetHashCode(token) % Dimensions)] += 1;
            }

            float norm = MathF.Sqrt(vector.Sum(v => v * v));
            for (int i = 0; i < vector.Length && norm > 0; i++)
            {
                vector[i] /= norm;
            }

            result.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Records requests; optionally calls the first tool once, then answers with a canned reply.</summary>
internal sealed class ScriptedChatClient(string answer, string? toolQuery = null) : IChatClient
{
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add((list, options));
        await Task.Yield();
        bool toolAlreadyCalled = list.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        if (toolQuery is not null && !toolAlreadyCalled && options?.Tools?.FirstOrDefault() is AIFunction tool)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?> { ["query"] = toolQuery })]);
            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking that must not leak")]);
        foreach (var word in answer.Split(' '))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
