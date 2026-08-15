using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class NotificationsArchitectureTests
{
    [Fact]
    public void Notifications_has_three_layers_and_no_endpoint_or_web_dependency()
    {
        var moduleRoot = TestRepository.GetPath("src/Modules/Notifications");
        var projects = Directory.GetFiles(moduleRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["Notifications.Application.csproj", "Notifications.Domain.csproj", "Notifications.Infrastructure.csproj"],
            projects);
        var source = string.Join('\n', Directory.GetFiles(moduleRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Net", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DbContext", File.ReadAllText(
            TestRepository.GetPath("src/Modules/Notifications/Notifications.Application/Notifications.Application.csproj")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Notifications_application_uses_only_published_foreign_application_ports()
    {
        var references = ProjectMetadataReader.Read(SolutionCatalog.NotificationsApplication)
            .ProjectReferencePaths
            .Select(path => SolutionCatalog.ByProjectPath[path].Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["Identity.Application", "Notifications.Domain", "Organizations.Application", "Paqueteria.Application"],
            references);
        Assert.DoesNotContain(references, value => value.EndsWith(".Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_and_dispatcher_never_log_or_persist_high_cardinality_content()
    {
        var source = string.Join('\n', Directory.GetFiles(
                TestRepository.GetPath("src/Modules/Notifications/Notifications.Infrastructure"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        Assert.DoesNotContain("LogInformation(\"{", source, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient_user_id}", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rendered_body", source, StringComparison.OrdinalIgnoreCase);
    }
}
