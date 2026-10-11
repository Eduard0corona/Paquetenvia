using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class SyntheticEnvironmentArchitectureTests
{
    [Fact]
    public void DevSynthetic_configuration_file_is_absent()
    {
        var matches = Directory.GetFiles(
                TestRepository.Root,
                "appsettings*.json",
                SearchOption.AllDirectories)
            .Where(static path =>
                Path.GetFileName(path).Contains("Synthetic", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void Non_migrated_development_and_testing_guards_remain_isolated()
    {
        var bootstrap = Read("src/Modules/Identity/Identity.Infrastructure/DependencyInjection.cs");
        Assert.Contains("environment.IsDevelopment() || environment.IsEnvironment(\"Testing\")", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticEnvironmentPolicy", bootstrap, StringComparison.Ordinal);

        var custody = Read("src/Modules/Custody/Custody.Infrastructure/DependencyInjection.cs");
        Assert.Contains("options.ThreatScanner != ProofThreatScannerProvider.Synthetic", custody, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticEnvironmentPolicy", custody, StringComparison.Ordinal);

        var realtime = Read("src/Modules/Realtime/Realtime.Infrastructure/DependencyInjection.cs");
        Assert.Contains("environment.IsEnvironment(\"Testing\")", realtime, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticEnvironmentPolicy", realtime, StringComparison.Ordinal);

        var api = Read("src/Paqueteria.Api/Program.cs");
        Assert.Contains("if (app.Environment.IsDevelopment())", api, StringComparison.Ordinal);
        Assert.DoesNotContain("UseDeveloperExceptionPage", api, StringComparison.Ordinal);
        Assert.DoesNotContain("DevSynthetic", api, StringComparison.Ordinal);

        var nextConfig = Read("apps/web/next.config.ts");
        Assert.Contains("process.env.NODE_ENV === \"development\"", nextConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("PAQUETERIA_DEPLOYMENT_CLASS", nextConfig, StringComparison.Ordinal);

        foreach (var path in new[]
                 {
                     "src/Modules/Identity/Identity.Endpoints/Testing/IdentityTestEndpoints.cs",
                     "src/Modules/Orders/Orders.Endpoints/Testing/PublicTrackingTestEndpoints.cs",
                     "src/Modules/Organizations/Organizations.Endpoints/Testing/OrganizationTestEndpoints.cs",
                 })
        {
            var testingSurface = Read(path);
            Assert.Contains("Testing", testingSurface, StringComparison.Ordinal);
            Assert.DoesNotContain("DevSynthetic", testingSurface, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tracking_verifier_has_no_persistence_configuration_or_output_path()
    {
        var source = Read("tools/Paqueteria.SyntheticVerification/SyntheticTrackingVerifier.cs");
        foreach (var forbidden in new[]
                 {
                     "Npgsql",
                     "IConfiguration",
                     "IssueAsync(",
                     "Console.",
                     "ILogger",
                     "app.current_user_id",
                     "app.current_org_ids",
                     "SET LOCAL ROLE",
                     "INSERT INTO",
                     "UPDATE ",
                     "DELETE FROM",
                     "get_public_tracking_projection",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }

        // TRK-002-AUTO-LINK: first link and repeat (same link); no rotation exists and, since TRK-002-NO-REVOCATION,
        // no revocation either.
        Assert.Equal(2, Regex.Matches(source, @"tokenService\.GetOrCreateAsync\(").Count);
        Assert.DoesNotContain("RevokeAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RotateAsync(", source, StringComparison.Ordinal);
        Assert.Contains("projectionReader.FindAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DevSynthetic_DevSeed_authorizes_only_prerequisite_seed_without_login_or_tracking()
    {
        var source = Read("tools/Paqueteria.DevSeed/Program.cs");
        Assert.Contains("args[0] == \"seed\" &&", source, StringComparison.Ordinal);
        Assert.Contains("PAQUETERIA_SYNTHETIC_SEED_ENABLED", source, StringComparison.Ordinal);
        Assert.Contains("if (!isLocalDevelopment && !isDevSyntheticSeed)", source, StringComparison.Ordinal);
        Assert.Contains("if (args[0] == \"tracking\")", source, StringComparison.Ordinal);
        Assert.Contains("if (args[0] == \"bootstrap\")", source, StringComparison.Ordinal);
        Assert.Contains("await SeedPrerequisitesAsync(connection);", source, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(source, @"BootstrapRuntimeRolesAsync\(").Count - 1);
        Assert.Equal(2, Regex.Matches(source, @"EnsureLoginAsync\(").Count - 1);
    }

    [Fact]
    public void Web_deployment_class_has_no_public_or_request_control_plane()
    {
        var policy = Read("apps/web/src/dev/dev-portal-policy.ts");
        var page = Read("apps/web/src/app/dev/page.dev.tsx");
        Assert.Contains("process.env.PAQUETERIA_DEPLOYMENT_CLASS", page, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXT_PUBLIC_", policy + page, StringComparison.Ordinal);
        Assert.DoesNotContain("headers()", policy + page, StringComparison.Ordinal);
        Assert.DoesNotContain("cookies()", policy + page, StringComparison.Ordinal);
        Assert.DoesNotContain("searchParams", policy + page, StringComparison.Ordinal);
    }

    // These pins started at the SEC-003 base but are not a frozen baseline: every authorized
    // normative change re-pins them with values regenerated by
    // `validate_contracts.py --write-integrity`, so the test detects unreviewed drift only.
    [Fact]
    public void Governance_integrity_files_match_pinned_values()
    {
        AssertSha256(
            "docs/normative/v0.6/specs/AI-08_BACKLOG.yaml",
            "26B49845872302F07F508AE3AAE3EB8261AA0EA0EA931C7CC636C3D82AE89439");
        AssertSha256(
            "docs/normative/v0.6/MANIFEST.json",
            "350849185496ED7408538F7FB9F292AEC3CADB74905D6332648FC1F3C5821073");
        AssertSha256(
            "docs/normative/v0.6/CHECKSUMS_SHA256.txt",
            "070666BE8455E8B2903336617E9026DE64E8C09B7400359F19A85B7CB0E02E80");
    }

    [Fact]
    public void Foundation_workflow_remains_thirteen_jobs()
    {
        var lines = File.ReadAllLines(TestRepository.GetPath(".github/workflows/ci.yml"));
        var jobsIndex = Array.IndexOf(lines, "jobs:");
        Assert.True(jobsIndex >= 0);
        var jobs = lines
            .Skip(jobsIndex + 1)
            .Count(static line => Regex.IsMatch(line, @"^  [a-z0-9_-]+:$"));

        Assert.Equal(13, jobs);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestRepository.GetPath(relativePath));

    private static void AssertSha256(string relativePath, string expected)
    {
        using var stream = File.OpenRead(TestRepository.GetPath(relativePath));
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(stream)));
    }
}
