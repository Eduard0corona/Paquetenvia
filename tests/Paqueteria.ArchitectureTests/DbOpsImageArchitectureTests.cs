using System.Text.Json;
using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// AZR-001 / ENV-001 db-ops images. Before any command (<c>verify</c>, <c>apply</c>, <c>assert</c>,
/// <c>runtime-logins</c>, <c>master-data-load</c>, <c>platform-bootstrap</c> and the rest), the DatabaseMigrator
/// finds the repository root from its working directory (<c>RepositoryRootLocator</c>). It then verifies the
/// baseline manifest and AI-06/AI-18 (<c>DatabaseBaselineVerifier</c>) and reads the source of every module lane
/// (<c>ModuleMigrationCoordinator.VerifySources</c>). If one of those files is not in the image, every job fails at
/// start (a missing lane source exits 3). The paths are taken from the migrator source and the manifest, not from a list kept here, so a new
/// lane or baseline file is checked against every db-ops Dockerfile as soon as it is added.
/// </summary>
public sealed partial class DbOpsImageArchitectureTests
{
    private const string Coordinator = "tools/Paqueteria.DatabaseMigrator/ModuleMigrationCoordinator.cs";

    // The migrator project, and the only directory of the projects it references that reads repository files.
    private static readonly string[] ReaderSources =
    [
        "tools/Paqueteria.DatabaseMigrator",
        "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Baseline",
    ];

    private static readonly string[] KnownDbOpsDockerfiles =
    [
        "deploy/azure/Dockerfile.db-ops",
        "deploy/azure/pilot/Dockerfile.db-ops",
    ];

    [Fact]
    public void Every_db_ops_image_copies_every_file_the_migrator_reads()
    {
        var paths = MigratorReadPaths();
        var dockerfiles = DbOpsDockerfiles();

        // The glob is what is checked, so a new db-ops image is covered too; the known two must be in it.
        Assert.Subset(dockerfiles.ToHashSet(StringComparer.Ordinal), KnownDbOpsDockerfiles.ToHashSet(StringComparer.Ordinal));
        var failures = dockerfiles
            .SelectMany(dockerfile => ImageFailures(File.ReadAllText(TestRepository.GetPath(dockerfile)), paths)
                .Select(failure => $"{dockerfile}: {failure}"))
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void The_scan_sees_the_baseline_files_and_every_lane_source()
    {
        var paths = MigratorReadPaths();

        Assert.Subset(paths, new SortedSet<string>(StringComparer.Ordinal)
        {
            "Paqueteria.sln",
            CanonicalBaselineContract.ManifestRelativePath,
            CanonicalBaselineContract.SchemaRelativePath,
            CanonicalBaselineContract.RolesRelativePath,
        });
        Assert.All(paths, path => Assert.True(File.Exists(TestRepository.GetPath(path)), $"{path} does not exist"));

        // ModuleMigrationCoordinator reads one file per Contracts row and one per Verify*Source(root, ...) call, each
        // named by a string literal. A path built any other way would escape the scan, so it fails here.
        var coordinator = File.ReadAllText(TestRepository.GetPath(Coordinator));
        var contracts = ContractsTable().Match(coordinator);
        Assert.True(contracts.Success, "the Contracts table was not found");
        var rows = ContractRow().Matches(contracts.Value).Count;
        var calls = VerifySourceCall().Matches(coordinator).Count;
        var literals = StringLiteral().Matches(coordinator)
            .Select(match => match.Groups["value"].Value)
            .Count(value => value.EndsWith(".cs", StringComparison.Ordinal) && IsRepositoryPath(value));
        Assert.True(rows > 0 && calls > 0, $"rows={rows} calls={calls}");
        Assert.Equal(rows + calls, literals);
        Assert.Equal<string>(
            ["contract.SourcePath", "sourcePath"],
            PathCombineArgument().Matches(coordinator).Select(match => match.Groups["argument"].Value)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // Only the reader sources locate the repository root, so no other file can add a read the scan misses.
        var separator = Path.DirectorySeparatorChar;
        var locating = new[] { "src", "tools" }
            .SelectMany(directory => SourceFiles(directory))
            .Where(file => File.ReadAllText(file).Contains(nameof(RepositoryRootLocator), StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(TestRepository.Root, file).Replace(separator, '/'))
            .Where(file => !ReaderSources.Any(source => file.StartsWith(source + "/", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(locating);
    }

    [Fact]
    public void An_image_without_a_copied_directory_is_reported()
    {
        var paths = MigratorReadPaths();
        var dockerfile = File.ReadAllText(TestRepository.GetPath("deploy/azure/pilot/Dockerfile.db-ops"))
            .ReplaceLineEndings("\n");
        Assert.Empty(ImageFailures(dockerfile, paths));

        // The image as it was before this check: no src/BuildingBlocks directory at all.
        var withoutBuildingBlocks = string.Join('\n', dockerfile.Split('\n')
            .Where(line => !line.Contains(" /app/src/BuildingBlocks/", StringComparison.Ordinal)));
        var buildingBlocks = paths.Where(path => path.StartsWith("src/BuildingBlocks/", StringComparison.Ordinal))
            .Select(path => $"{path} is not copied").ToArray();
        Assert.NotEmpty(buildingBlocks);
        Assert.Equal(buildingBlocks, ImageFailures(withoutBuildingBlocks, paths));

        // A copy to another place, or from a stage other than the build one, does not count.
        var modules = paths.Where(path => path.StartsWith("src/Modules/", StringComparison.Ordinal))
            .Select(path => $"{path} is not copied").ToArray();
        Assert.NotEmpty(modules);
        Assert.Equal(modules, ImageFailures(
            dockerfile.Replace(" /app/src/Modules\n", " /app/modules\n", StringComparison.Ordinal), paths));
        Assert.Equal(modules, ImageFailures(
            dockerfile.Replace("--from=build /src/src/Modules ", "--from=other /src/src/Modules ", StringComparison.Ordinal), paths));

        // The root is found from the working directory, so the final stage must work in /app.
        Assert.Contains(
            "final stage WORKDIR is /srv, not /app",
            ImageFailures(dockerfile.Replace("\nWORKDIR /app\n", "\nWORKDIR /srv\n", StringComparison.Ordinal), paths));
    }

    /// <summary>Every repository file the migrator reads before it runs a command.</summary>
    private static SortedSet<string> MigratorReadPaths()
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ReaderSources.SelectMany(SourceFiles))
        {
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                var value = literal.Groups["value"].Value;
                if (IsRepositoryPath(value))
                {
                    paths.Add(value);
                }
            }
        }

        // DatabaseBaselineVerifier opens the step files that the manifest names.
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(TestRepository.GetPath(CanonicalBaselineContract.ManifestRelativePath)));
        foreach (var step in manifest.RootElement.GetProperty("steps").EnumerateArray())
        {
            paths.Add(step.GetProperty("path").GetString()!);
        }

        return paths;
    }

    /// <summary>A literal in a subdirectory (<c>a/b.ext</c>), or the name of a file at the repository root.</summary>
    private static bool IsRepositoryPath(string value) =>
        RepositoryFilePath().IsMatch(value) &&
        (value.Contains('/', StringComparison.Ordinal) || File.Exists(TestRepository.GetPath(value)));

    private static string[] DbOpsDockerfiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("deploy"), "Dockerfile.db-ops*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(TestRepository.Root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> SourceFiles(string directory)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(TestRepository.GetPath(directory), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why the image would miss a file the migrator reads. The build stage must hold the repository at <c>/src</c>
    /// (<c>WORKDIR /src</c>, <c>COPY . .</c>) and the final stage must work in <c>/app</c>. A path counts as copied
    /// only through a final-stage <c>COPY --from=build /src/X /app/X</c> whose <c>X</c> is the path or a directory
    /// above it.
    /// </summary>
    private static List<string> ImageFailures(string dockerfile, IEnumerable<string> paths)
    {
        var failures = new List<string>();
        var stages = Stages(Instructions(dockerfile));
        if (stages.Count < 2)
        {
            return [$"expected a build stage and a final stage, found {stages.Count} stage(s)"];
        }

        var build = stages.SingleOrDefault(stage =>
        {
            var from = stage[0].Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return from.Length >= 3 &&
                string.Equals(from[^2], "AS", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(from[^1], "build", StringComparison.Ordinal);
        });
        var buildWorkdir = build?.FindIndex(instruction => instruction is ("WORKDIR", "/src")) ?? -1;
        if (build is null || buildWorkdir < 0 || build.FindIndex(buildWorkdir, instruction => instruction is ("COPY", ". .")) < 0)
        {
            failures.Add("the build stage does not copy the repository to /src");
        }

        var final = stages[^1];
        var workdirs = final.Where(instruction => instruction.Keyword == "WORKDIR").Select(instruction => instruction.Arguments).ToArray();
        var workdir = workdirs.Length == 0 ? "unset" : workdirs[^1];
        if (workdir != "/app")
        {
            failures.Add($"final stage WORKDIR is {workdir}, not /app");
        }

        var copied = new List<string>();
        foreach (var (keyword, arguments) in final)
        {
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keyword != "COPY" || !parts.Contains("--from=build", StringComparer.Ordinal))
            {
                continue;
            }

            var operands = parts.Where(part => !part.StartsWith("--", StringComparison.Ordinal)).ToArray();
            if (operands is [var source, var destination] &&
                source.StartsWith("/src/", StringComparison.Ordinal) &&
                destination.StartsWith("/app/", StringComparison.Ordinal) &&
                source["/src/".Length..].TrimEnd('/') is { Length: > 0 } relative &&
                relative == destination["/app/".Length..].TrimEnd('/'))
            {
                copied.Add(relative);
            }
        }

        failures.AddRange(paths
            .Where(path => !copied.Any(root =>
                path == root || path.StartsWith(root + "/", StringComparison.Ordinal)))
            .Select(path => $"{path} is not copied"));
        return failures;
    }

    /// <summary>Instructions with comments and blank lines skipped and backslash continuations joined.</summary>
    private static List<(string Keyword, string Arguments)> Instructions(string dockerfile)
    {
        var instructions = new List<(string Keyword, string Arguments)>();
        var pending = string.Empty;
        foreach (var raw in dockerfile.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                pending += line[..^1].TrimEnd() + " ";
                continue;
            }

            instructions.Add(Instruction(pending + line));
            pending = string.Empty;
        }

        if (pending.Trim().Length > 0)
        {
            instructions.Add(Instruction(pending));
        }

        return instructions;
    }

    private static (string Keyword, string Arguments) Instruction(string line)
    {
        var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return (parts[0].ToUpperInvariant(), parts.Length > 1 ? Whitespace().Replace(parts[1].Trim(), " ") : string.Empty);
    }

    private static List<List<(string Keyword, string Arguments)>> Stages(List<(string Keyword, string Arguments)> instructions)
    {
        var stages = new List<List<(string Keyword, string Arguments)>>();
        foreach (var instruction in instructions)
        {
            if (instruction.Keyword == "FROM")
            {
                stages.Add([]);
            }

            if (stages.Count > 0)
            {
                stages[^1].Add(instruction);
            }
        }

        return stages;
    }

    [GeneratedRegex(@"""(?<value>[^""\\\r\n]*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.[A-Za-z0-9]+$")]
    private static partial Regex RepositoryFilePath();

    [GeneratedRegex(@"\bContracts\s*=\s*\[(?<rows>.*?)\n\s*\];", RegexOptions.Singleline)]
    private static partial Regex ContractsTable();

    [GeneratedRegex(@"^\s*\(""\w+"",", RegexOptions.Multiline)]
    private static partial Regex ContractRow();

    [GeneratedRegex(@"\bVerify\w+Source\(\s*root\s*,")]
    private static partial Regex VerifySourceCall();

    [GeneratedRegex(@"Path\.Combine\(\s*root\s*,\s*(?<argument>[^,)]+?)\s*(?:\.Replace\(|[,)])")]
    private static partial Regex PathCombineArgument();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
