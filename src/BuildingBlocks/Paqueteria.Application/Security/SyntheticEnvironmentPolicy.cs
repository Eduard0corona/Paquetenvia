namespace Paqueteria.Application.Security;

public static class SyntheticEnvironmentPolicy
{
    public const string EnvironmentName = "DevSynthetic";
    public const string DeploymentClassVariable = "PAQUETERIA_DEPLOYMENT_CLASS";
    public const string DeploymentClass = "DEV_SYNTHETIC";

    public static bool IsDevSynthetic(string? environmentName) =>
        string.Equals(environmentName, EnvironmentName, StringComparison.Ordinal) &&
        string.Equals(
            Environment.GetEnvironmentVariable(DeploymentClassVariable),
            DeploymentClass,
            StringComparison.Ordinal);
}
