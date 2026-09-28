using System.Security.Cryptography;
using Npgsql;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

/// <summary>
/// ADP-001: an in-memory, versioned RSA key-encryption key standing in for Azure Key Vault. Never
/// touches Azure; <see cref="Unavailable"/> simulates an unreachable or denied vault.
/// </summary>
internal sealed class Adp001FakeKeyVault : IPiiKeyWrapClient
{
    private readonly Dictionary<string, RSA> _versions = new(StringComparer.Ordinal);

    public Adp001FakeKeyVault()
    {
        Rotate();
    }

    public bool Unavailable { get; set; }

    public string CurrentVersion { get; private set; } = string.Empty;

    public string Rotate()
    {
        CurrentVersion = $"akv:pii-envelope/{Guid.NewGuid():N}";
        _versions[CurrentVersion] = RSA.Create(2048);
        return CurrentVersion;
    }

    public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
        Unavailable
            ? Task.FromException<string>(new InvalidOperationException("Key Vault is unreachable."))
            : Task.FromResult(CurrentVersion);

    public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
        Unavailable
            ? Task.FromException<byte[]>(new InvalidOperationException("Key Vault is unreachable."))
            : Task.FromResult(_versions[keyVersion].Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));

    public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
        Unavailable
            ? Task.FromException<byte[]>(new InvalidOperationException("Key Vault is unreachable."))
            : Task.FromResult(_versions[keyVersion].Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));

    /// <summary>
    /// Counts rows of the tenant's audit log and outbox whose serialized form contains any of the
    /// plaintext values (UTF-8 text or its Base64 form).
    /// </summary>
    public static async Task<long> CountPlaintextInAuditAndOutboxAsync(
        NpgsqlDataSource adminDataSource,
        Guid organizationId,
        IReadOnlyCollection<string> plaintexts)
    {
        long total = 0;
        foreach (var plaintext in plaintexts)
        {
            await using var command = adminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM platform.audit_logs a
                   WHERE a.org_id=@org
                     AND (row_to_json(a)::text LIKE '%' || @plain || '%'
                          OR row_to_json(a)::text LIKE '%' || @base64 || '%'))
                + (SELECT count(*) FROM platform.outbox_events o
                   WHERE o.owner_org_id=@org
                     AND (row_to_json(o)::text LIKE '%' || @plain || '%'
                          OR row_to_json(o)::text LIKE '%' || @base64 || '%'));
                """);
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("plain", plaintext);
            command.Parameters.AddWithValue("base64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)));
            total += (long)(await command.ExecuteScalarAsync())!;
        }

        return total;
    }
}
