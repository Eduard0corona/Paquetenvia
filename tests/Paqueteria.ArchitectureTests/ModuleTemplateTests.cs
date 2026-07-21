using System.Diagnostics;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class ModuleTemplateTests
{
    [Fact]
    public async Task Template_generates_and_builds_a_Sandbox_module_outside_the_repository()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"paquetenvia-arc001-{Guid.NewGuid():N}");
        var workspace = Path.Combine(temporaryRoot, "workspace");
        var cliHome = Path.Combine(temporaryRoot, "dotnet-home");

        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(cliHome);

        try
        {
            CopyBuildConfiguration(workspace);
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            };
            var templatePath = TestRepository.GetPath("templates/Paqueteria.Module");
            var buildingBlocksPath = TestRepository.GetPath("src/BuildingBlocks");
            var modulePath = Path.Combine(workspace, "Sandbox");
            var solutionPath = Path.Combine(workspace, "SandboxSolution.sln");

            await RunDotNetAsync(
                workspace,
                environment,
                "new", "install", templatePath, "--force");
            await RunDotNetAsync(
                workspace,
                environment,
                "new", "paquetenvia-module",
                "--name", "Sandbox",
                "--output", modulePath,
                "--BuildingBlocksPath", buildingBlocksPath.Replace('\\', '/'));

            var projects = Directory.GetFiles(modulePath, "*.csproj", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "Sandbox.Application.csproj",
                    "Sandbox.Domain.csproj",
                    "Sandbox.Endpoints.csproj",
                    "Sandbox.Infrastructure.csproj",
                },
                projects.Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(4, Directory.GetFiles(modulePath, "AssemblyReference.cs", SearchOption.AllDirectories).Length);
            Assert.Equal(8, Directory.GetFiles(modulePath, "*", SearchOption.AllDirectories).Length);

            var domainMetadata = ProjectMetadataReader.Read(
                Path.Combine(modulePath, "Sandbox.Domain", "Sandbox.Domain.csproj"));
            Assert.Empty(domainMetadata.PackageReferences);
            Assert.Empty(domainMetadata.FrameworkReferences);

            await RunDotNetAsync(
                workspace,
                environment,
                "new", "sln", "--name", "SandboxSolution", "--format", "sln");
            await RunDotNetAsync(
                workspace,
                environment,
                "sln", solutionPath, "add", projects[0], projects[1], projects[2], projects[3]);
            await RunDotNetAsync(
                workspace,
                environment,
                "build", solutionPath, "--configuration", "Release");
        }
        finally
        {
            DeleteTemporaryRoot(temporaryRoot);
        }
    }

    private static void CopyBuildConfiguration(string workspace)
    {
        foreach (var fileName in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
        {
            File.Copy(TestRepository.GetPath(fileName), Path.Combine(workspace, fileName));
        }
    }

    private static async Task RunDotNetAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} exceeded two minutes.");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static void DeleteTemporaryRoot(string temporaryRoot)
    {
        var normalizedRoot = TestRepository.Normalize(temporaryRoot);
        var normalizedTemp = TestRepository.Normalize(Path.GetTempPath());

        if (!normalizedRoot.StartsWith(
                normalizedTemp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalizedRoot).StartsWith("paquetenvia-arc001-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected path: {normalizedRoot}");
        }

        if (Directory.Exists(normalizedRoot))
        {
            Directory.Delete(normalizedRoot, recursive: true);
        }
    }
}
