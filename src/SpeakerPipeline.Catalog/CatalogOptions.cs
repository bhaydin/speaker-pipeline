namespace SpeakerPipeline.Catalog;

public sealed class CatalogOptions
{
    public bool Enabled { get; set; }
    public int MaxExtractionCallsPerSync { get; set; } = 5;
    public int MaxExtractionInputCharacters { get; set; } = 12000;
    public string ExtractionModel { get; set; } = "";
    public GitHubCatalogOptions GitHub { get; set; } = new();
}

public sealed class GitHubCatalogOptions
{
    public string AppId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    // Resolve from Key Vault app-setting references. Never persist or log these values.
    public string PrivateKeyPem { get; set; } = "";
}

public sealed record RepositoryFile(string Path, string BlobSha, string? Text);
public sealed record RepositorySnapshot(string Repository, string CommitSha, IReadOnlyList<RepositoryFile> Files);
public interface ICatalogSource
{
    Task<RepositorySnapshot> ReadAsync(string repository, CancellationToken ct = default);
}
