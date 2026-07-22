using System.Text.Json.Serialization;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class BaselineManifest
{
    [JsonPropertyName("baseline")]
    public required string Baseline { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<BaselineManifestStep> Steps { get; init; }
}

public sealed class BaselineManifestStep
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("order")]
    public required int Order { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("credential")]
    public required string Credential { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("required")]
    public required bool Required { get; init; }
}

public sealed record VerifiedBaselineStep(
    string Id,
    int Order,
    string RelativePath,
    string AbsolutePath,
    string Sha256,
    string Credential,
    string Description,
    bool Required);

public sealed record VerifiedDatabaseBaseline(
    string RepositoryRoot,
    string ManifestPath,
    string Version,
    IReadOnlyList<VerifiedBaselineStep> Steps);
