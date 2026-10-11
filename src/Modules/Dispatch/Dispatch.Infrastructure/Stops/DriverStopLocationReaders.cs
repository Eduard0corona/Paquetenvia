using Dispatch.Application.Stops;
using Microsoft.Extensions.Configuration;
using Paqueteria.Infrastructure.Security.Pii;

namespace Dispatch.Infrastructure.Stops;

/// <summary>
/// The protected values of the location a stop is heading to, exactly as <c>locations.locations</c> stores them:
/// the owning organization and the row the ADP-001 ciphertexts are bound to, and the key version they were sealed
/// under. <see cref="ToString"/> never prints the ciphertexts.
/// </summary>
public sealed record ProtectedDriverStopLocation(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string KeyVersion,
    byte[] AddressCiphertext,
    byte[]? ContactNameCiphertext)
{
    public override string ToString() =>
        $"ProtectedDriverStopLocation {{ LocationId = {LocationId:D}, [ciphertexts redacted] }}";
}

/// <summary>The plaintext of one stop location, only in memory and only for the response being built.</summary>
public sealed record DriverStopLocationText(string AddressText, string? ContactName)
{
    public override string ToString() => "DriverStopLocationText { [redacted] }";
}

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: turns the protected address and contact name of the driver's stops into
/// text, in one batch, after the tenant transaction has closed (no Key Vault call while a transaction is held). An
/// implementation that cannot read every value throws <see cref="DriverStopsPiiUnavailableException"/>; it never
/// returns a partial result, a placeholder for a failed value or the ciphertext itself.
/// </summary>
public interface IDriverStopLocationReader
{
    Task<IReadOnlyList<DriverStopLocationText>> ReadAsync(
        IReadOnlyList<ProtectedDriverStopLocation> locations,
        CancellationToken cancellationToken);
}

/// <summary>
/// How the location values were protected, so they can be read back the same way. It follows
/// <c>Locations:PiiProtector</c>, the setting the Locations module protects them with: <c>AzureKeyVault</c> is the
/// ADP-001 envelope, <c>Mock</c> the GEO-001 one-way digest (synthetic environments only) and anything else means
/// nothing can be read.
/// </summary>
public enum DriverStopLocationProtection
{
    Disabled,
    Synthetic,
    Envelope,
}

public static class DriverStopLocationProtectionSetting
{
    public const string ConfigurationKey = "Locations:PiiProtector";

    public static DriverStopLocationProtection Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[ConfigurationKey]?.Trim();
        if (string.Equals(value, "AzureKeyVault", StringComparison.OrdinalIgnoreCase))
        {
            return DriverStopLocationProtection.Envelope;
        }

        return string.Equals(value, "Mock", StringComparison.OrdinalIgnoreCase)
            ? DriverStopLocationProtection.Synthetic
            : DriverStopLocationProtection.Disabled;
    }
}

/// <summary>
/// Live mode: decrypts each value with the ADP-001 envelope, bound to its owning organization, location row, column
/// and key version. The values are read concurrently with a small bound; the first failure fails the whole batch.
/// </summary>
public sealed class EnvelopeDriverStopLocationReader(IPiiEnvelopeProtector envelope) : IDriverStopLocationReader
{
    /// <summary>
    /// <c>locations.locations.address_ciphertext</c>. It must stay equal to the Locations protector's purpose
    /// (<c>AzureKeyVaultLocationPiiProtector.AddressTextPurpose</c>); a test pins both.
    /// </summary>
    public const string AddressTextPurpose = "locations.address_text";

    /// <summary>
    /// <c>locations.locations.contact_name_ciphertext</c>, equal to
    /// <c>AzureKeyVaultLocationPiiProtector.ContactNamePurpose</c>; a test pins both.
    /// </summary>
    public const string ContactNamePurpose = "locations.contact_name";

    private const int MaximumConcurrentLocations = 8;

    public async Task<IReadOnlyList<DriverStopLocationText>> ReadAsync(
        IReadOnlyList<ProtectedDriverStopLocation> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var texts = new DriverStopLocationText[locations.Count];
        try
        {
            await Parallel.ForEachAsync(
                    Enumerable.Range(0, locations.Count),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaximumConcurrentLocations,
                        CancellationToken = cancellationToken,
                    },
                    async (index, token) =>
                    {
                        var location = locations[index];
                        var binding = new PiiBinding(location.OwnerOrganizationId, location.LocationId);
                        var address = await envelope.UnprotectAsync(
                                binding,
                                AddressTextPurpose,
                                location.AddressCiphertext,
                                location.KeyVersion,
                                token)
                            .ConfigureAwait(false);
                        var contactName = location.ContactNameCiphertext is null
                            ? null
                            : await envelope.UnprotectAsync(
                                    binding,
                                    ContactNamePurpose,
                                    location.ContactNameCiphertext,
                                    location.KeyVersion,
                                    token)
                                .ConfigureAwait(false);
                        texts[index] = new DriverStopLocationText(address, contactName);
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DriverStopsPiiUnavailableException(exception);
        }

        return texts;
    }
}

/// <summary>
/// Synthetic mode (Development, Testing and DEV_SYNTHETIC, where <c>Locations:PiiProtector=Mock</c> stored one-way
/// digests): it never decrypts anything. Every stop gets the same fixed placeholders, so the driver app can be
/// exercised end to end without any real personal data.
/// </summary>
public sealed class SyntheticDriverStopLocationReader : IDriverStopLocationReader
{
    public const string AddressPlaceholder = "Dirección sintética de prueba";
    public const string ContactNamePlaceholder = "Contacto Sintético";

    public Task<IReadOnlyList<DriverStopLocationText>> ReadAsync(
        IReadOnlyList<ProtectedDriverStopLocation> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DriverStopLocationText>>(locations
            .Select(location => new DriverStopLocationText(
                AddressPlaceholder,
                location.ContactNameCiphertext is null ? null : ContactNamePlaceholder))
            .ToArray());
    }
}

/// <summary>No protector is configured, so no stop detail can be read: a driver with stops gets 503.</summary>
public sealed class DisabledDriverStopLocationReader : IDriverStopLocationReader
{
    public Task<IReadOnlyList<DriverStopLocationText>> ReadAsync(
        IReadOnlyList<ProtectedDriverStopLocation> locations,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<DriverStopLocationText>>(new DriverStopsPiiUnavailableException());
}
