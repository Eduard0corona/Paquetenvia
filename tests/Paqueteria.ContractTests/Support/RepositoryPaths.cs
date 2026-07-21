namespace Paqueteria.ContractTests.Support;

internal static class RepositoryPaths
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    public static string Root => RepositoryRoot.Value;

    public static string Normative(params string[] segments) =>
        Path.Combine([Root, "docs", "normative", "v0.6", .. segments]);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
