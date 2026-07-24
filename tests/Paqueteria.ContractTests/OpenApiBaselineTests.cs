using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Paqueteria.ContractTests;

public sealed partial class OpenApiBaselineTests
{
    private const string ExpectedSha256 = "cb009c42fb48034c6bedbfe3e0ff5a6661c6c753c67570ceca9a8ae5ef449b69";

    [Fact]
    public void Canonical_openapi_contract_exists_and_matches_v0_6()
    {
        var contractPath = FindRepositoryFile(
            "docs",
            "normative",
            "v0.6",
            "contracts",
            "AI-05_OPENAPI.yaml");

        Assert.True(File.Exists(contractPath), $"Missing canonical contract: {contractPath}");

        var contract = File.ReadAllText(contractPath);
        Assert.Matches(OpenApiVersionPattern(), contract);
        Assert.Matches(ContractVersionPattern(), contract);

        using var stream = File.OpenRead(contractPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Assert.Equal(ExpectedSha256, actualHash);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine([AppContext.BaseDirectory, .. relativeSegments]);
    }

    [GeneratedRegex(@"(?m)^openapi:\s*3\.1\.0\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenApiVersionPattern();

    [GeneratedRegex("(?m)^\\s{2}version:\\s*['\\\"]?0\\.6\\.0['\\\"]?\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContractVersionPattern();
}
