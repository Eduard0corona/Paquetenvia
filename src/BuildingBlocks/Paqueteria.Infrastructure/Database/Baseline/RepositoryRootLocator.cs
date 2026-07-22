namespace Paqueteria.Infrastructure.Database.Baseline;

public static class RepositoryRootLocator
{
    public static string Find(string? startDirectory = null)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory()));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Paqueteria.sln")) &&
                File.Exists(Path.Combine(current.FullName, CanonicalBaselineContract.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Paqueteria repository root.");
    }
}
