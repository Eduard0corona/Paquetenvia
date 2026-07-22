using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class DatabaseBaselineVerifier
{
    private static readonly (string Id, int Order, string Path, string Hash)[] RequiredSteps =
    [
        ("0001-canonical-schema", 1, CanonicalBaselineContract.SchemaRelativePath, CanonicalBaselineContract.SchemaSha256),
        ("0002-role-model", 2, CanonicalBaselineContract.RolesRelativePath, CanonicalBaselineContract.RolesSha256),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<VerifiedDatabaseBaseline> VerifyAsync(
        string? repositoryRoot = null,
        string? manifestPath = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot ?? RepositoryRootLocator.Find());
        var resolvedManifest = Path.GetFullPath(manifestPath ?? Path.Combine(
            root,
            CanonicalBaselineContract.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!File.Exists(resolvedManifest))
        {
            throw new BaselineVerificationException($"Baseline manifest was not found: {resolvedManifest}");
        }

        BaselineManifest manifest;
        try
        {
            await using var stream = File.OpenRead(resolvedManifest);
            manifest = await JsonSerializer.DeserializeAsync<BaselineManifest>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new BaselineVerificationException("Baseline manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new BaselineVerificationException($"Baseline manifest JSON is invalid: {exception.Message}");
        }

        ValidateManifestShape(manifest);

        var verified = new List<VerifiedBaselineStep>(RequiredSteps.Length);
        for (var index = 0; index < RequiredSteps.Length; index++)
        {
            var required = RequiredSteps[index];
            var step = manifest.Steps[index];
            var normalizedPath = NormalizeRelativePath(step.Path);
            var absolutePath = ResolveAllowedPath(root, normalizedPath);
            if (!File.Exists(absolutePath))
            {
                throw new BaselineVerificationException($"Required baseline step file was not found: {normalizedPath}");
            }

            var actualHash = await ComputeSha256Async(absolutePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, required.Hash, StringComparison.Ordinal))
            {
                throw new BaselineVerificationException(
                    $"Canonical hash mismatch for {normalizedPath}. Expected {required.Hash}, actual {actualHash}.");
            }

            verified.Add(new VerifiedBaselineStep(
                step.Id,
                step.Order,
                normalizedPath,
                absolutePath,
                actualHash,
                step.Credential,
                step.Description,
                step.Required));
        }

        return new VerifiedDatabaseBaseline(root, resolvedManifest, manifest.Baseline, verified.AsReadOnly());
    }

    private static void ValidateManifestShape(BaselineManifest manifest)
    {
        if (!string.Equals(manifest.Baseline, CanonicalBaselineContract.BaselineVersion, StringComparison.Ordinal))
        {
            throw new BaselineVerificationException(
                $"Unsupported baseline '{manifest.Baseline}'. Expected '{CanonicalBaselineContract.BaselineVersion}'.");
        }

        if (manifest.Steps is null || manifest.Steps.Count != RequiredSteps.Length)
        {
            throw new BaselineVerificationException($"Baseline must declare exactly {RequiredSteps.Length} steps.");
        }

        if (manifest.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Steps.Count)
        {
            throw new BaselineVerificationException("Baseline step identifiers must be unique.");
        }

        for (var index = 0; index < RequiredSteps.Length; index++)
        {
            var required = RequiredSteps[index];
            var step = manifest.Steps[index];
            var normalizedPath = NormalizeRelativePath(step.Path);
            if (!string.Equals(step.Id, required.Id, StringComparison.Ordinal) ||
                step.Order != required.Order ||
                !string.Equals(normalizedPath, required.Path, StringComparison.Ordinal))
            {
                throw new BaselineVerificationException(
                    $"Baseline step {index + 1} must be {required.Id} at order {required.Order} with path {required.Path}.");
            }

            if (!string.Equals(step.Sha256, required.Hash, StringComparison.Ordinal))
            {
                throw new BaselineVerificationException($"Manifest hash for {required.Path} does not match the canonical contract.");
            }

            if (!string.Equals(step.Credential, CanonicalBaselineContract.DeploymentCredential, StringComparison.Ordinal))
            {
                throw new BaselineVerificationException($"Step {step.Id} must require {CanonicalBaselineContract.DeploymentCredential} credentials.");
            }

            if (!step.Required || string.IsNullOrWhiteSpace(step.Description))
            {
                throw new BaselineVerificationException($"Step {step.Id} must be required and have a description.");
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new BaselineVerificationException("Baseline step paths must be non-empty repository-relative paths.");
        }

        return path.Replace('\\', '/');
    }

    private static string ResolveAllowedPath(string root, string relativePath)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new BaselineVerificationException($"Baseline path escapes the repository root: {relativePath}");
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
