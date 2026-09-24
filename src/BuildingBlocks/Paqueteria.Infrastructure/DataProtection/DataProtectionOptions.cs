namespace Paqueteria.Infrastructure.DataProtection;

public enum DataProtectionProviderKind
{
    Disabled,
    PostgreSql,
}

/// <summary>
/// SCL-001 names one reproducible profile that actually turns the shared PostgreSQL key ring on.
/// It is a hosting environment name, so it is only selected when the deployment explicitly asks
/// for it; Production keeps the <c>Disabled</c> default from <c>appsettings.json</c> and never
/// enables distributed Data Protection implicitly.
/// </summary>
/// <remarks>
/// The profile is a validation profile, not production authorization: an external
/// key-encryption protector is required before productive distributed Data Protection activation.
/// </remarks>
public static class DataProtectionProfiles
{
    public const string ScaleReadyEnvironmentName = "ScaleReady";
}

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    public DataProtectionProviderKind Provider { get; set; } = DataProtectionProviderKind.Disabled;

    /// <summary>
    /// Shared key-ring discriminator. Every API and Worker replica that must interchange
    /// protected payloads has to resolve the same value.
    /// </summary>
    public string ApplicationName { get; set; } = "Paquetenvia";

    /// <summary>
    /// The <c>ConnectionStrings</c> entry the key ring is read from. The API reads it with its
    /// own least-privilege login and the Worker with the worker login.
    /// </summary>
    public string ConnectionStringName { get; set; } = "Paqueteria";

    /// <summary>
    /// The canonical runtime role this host assumes before touching the key ring. Runtime logins
    /// are <c>NOINHERIT</c>, so the role has to be set explicitly on every connection.
    /// </summary>
    public string RuntimeRole { get; set; } = DataProtectionRuntimeRoles.Application;

    public int KeyLifetimeDays { get; set; } = 90;

    public int CommandTimeoutSeconds { get; set; } = 5;
}

public static class DataProtectionRuntimeRoles
{
    public const string Application = "paqueteria_app";
    public const string Worker = "paqueteria_worker";

    public static bool IsCanonical(string? role) =>
        role is Application or Worker;
}

public static class DataProtectionOptionsValidator
{
    public static bool IsValid(DataProtectionOptions? options) =>
        options is not null &&
        Enum.IsDefined(options.Provider) &&
        !string.IsNullOrWhiteSpace(options.ApplicationName) &&
        options.ApplicationName.Length is >= 3 and <= 100 &&
        !string.IsNullOrWhiteSpace(options.ConnectionStringName) &&
        DataProtectionRuntimeRoles.IsCanonical(options.RuntimeRole) &&
        options.KeyLifetimeDays is >= 7 and <= 3_650 &&
        options.CommandTimeoutSeconds is >= 1 and <= 60;
}
