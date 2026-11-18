using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Ollama:Mode=host: point the fleet at an Ollama server already running on this machine (native GPU,
/// models pulled once with <c>ollama pull</c>) while keeping the exact resource contract of the Community
/// Toolkit integration — a parent <c>ollama</c> resource and model resources whose connection string is
/// <c>Endpoint=&lt;url&gt;;Model=&lt;model&gt;</c> — so consumers wire <c>.WithReference(chat)</c> the same way in both modes.
/// </summary>
internal static class HostOllama
{
    public static IResourceBuilder<ExternalServiceResource> AddHostOllama(this IDistributedApplicationBuilder builder, string name, string url) =>
        builder.AddExternalService(name, url)
            .WithHttpHealthCheck("/api/tags");

    /// <summary>
    /// A connection-string resource shaped like the toolkit's <c>OllamaModelResource</c>, nested under the host
    /// <paramref name="ollama"/> resource, and healthy only once <paramref name="model"/> is pulled on that server.
    /// </summary>
    public static IResourceBuilder<ConnectionStringResource> AddHostModel(
        this IResourceBuilder<ExternalServiceResource> ollama, string name, string url, string model)
    {
        var builder = ollama.ApplicationBuilder;
        var baseUri = new Uri(url.EndsWith('/') ? url : url + "/");
        var healthCheckKey = $"{name}_ollama_model";

        builder.Services.AddHealthChecks().AddAsyncCheck(healthCheckKey, async ct =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
                var tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", ct);
                var pulled = tags?.Models?.Any(m => m.Name == model || m.Name == $"{model}:latest") == true;
                return pulled
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy($"Model '{model}' is not pulled on {url}; run `ollama pull {model}`.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return HealthCheckResult.Unhealthy($"Ollama is not reachable at {url}.", ex);
            }
        });

        return builder.AddConnectionString(name, ReferenceExpression.Create($"Endpoint={url.TrimEnd('/')};Model={model}"))
            .WithParentRelationship(ollama)
            .WithHealthCheck(healthCheckKey);
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel>? Models);

    private sealed record TagModel([property: JsonPropertyName("name")] string Name);
}
