namespace SpeakerPipeline.Core;

/// <summary>Fail closed when local-only configuration reaches an Azure host.</summary>
public static class RuntimeSafety
{
    public static bool IsAzureHost =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

    public static void ValidateHost(string environmentName, bool isAzureHost)
    {
        if (isAzureHost && !string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Azure hosts must explicitly use the Production environment.");
        }
    }

    public static void ValidateConsumer(string environmentName, bool isAzureHost, string? baseUrl, string? scope)
    {
        ValidateHost(environmentName, isAzureHost);
        var development = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(development && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException("SpeakerPipelineApi:BaseUrl must be HTTPS (HTTP loopback is allowed locally).");
        }
        if (!development && string.IsNullOrWhiteSpace(scope))
        {
            throw new InvalidOperationException("SpeakerPipelineApi:Scope is required outside local Development.");
        }
    }

    public static void ValidateStorage(string environmentName, bool isAzureHost, string? connectionString, string? tableEndpoint = null)
    {
        ValidateHost(environmentName, isAzureHost);
        if (!string.IsNullOrWhiteSpace(connectionString) &&
            (isAzureHost || !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Storage connection strings are supported only for local Development; production uses managed identity.");
        }
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(connectionString) && tableEndpoint is not null &&
            (!Uri.TryCreate(tableEndpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback))
        {
            throw new InvalidOperationException("Local Development must use Azurite; cloud data requires Production authentication.");
        }
    }
}
