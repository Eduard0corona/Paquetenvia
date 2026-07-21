using System.Xml.Linq;

namespace Paqueteria.ArchitectureTests.Architecture;

internal sealed record ProjectMetadata(
    string Sdk,
    IReadOnlyList<string> ProjectReferencePaths,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences);

internal static class ProjectMetadataReader
{
    internal static ProjectMetadata Read(ProjectComponent component) =>
        Read(TestRepository.GetPath(component.ProjectPath));

    internal static ProjectMetadata Read(string projectPath)
    {
        var absoluteProjectPath = TestRepository.Normalize(projectPath);
        var projectDirectory = Path.GetDirectoryName(absoluteProjectPath)
            ?? throw new InvalidOperationException($"Project has no directory: {absoluteProjectPath}");
        var document = XDocument.Load(absoluteProjectPath);
        var project = document.Root
            ?? throw new InvalidOperationException($"Project XML has no root: {absoluteProjectPath}");

        var projectReferences = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TestRepository.Normalize(Path.Combine(
                projectDirectory,
                NormalizeProjectReferenceInclude(value!))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packages = ReadIncludes(project, "PackageReference");
        var frameworks = ReadIncludes(project, "FrameworkReference");

        return new ProjectMetadata(
            project.Attribute("Sdk")?.Value ?? string.Empty,
            projectReferences,
            packages,
            frameworks);
    }

    private static string[] ReadIncludes(XElement project, string elementName) =>
        project.Descendants(elementName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string NormalizeProjectReferenceInclude(string include) =>
        include
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
}
