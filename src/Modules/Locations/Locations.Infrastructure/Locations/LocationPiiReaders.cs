using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Paqueteria.Application.Locations;
using Paqueteria.Infrastructure.Security.Pii;

namespace Locations.Infrastructure.Locations;

/// <summary>
/// <c>Locations:PiiProtector=AzureKeyVault</c>: opens each value with the ADP-001 envelope, bound to its owning
/// organization, location row, purpose and key version, with the purposes the protector sealed them with. A value
/// rejected for its row leaves only that location unreadable; an unavailable key service stops the batch, so the
/// service is not asked again for the rest, and every location not read yet stays unreadable.
/// </summary>
public sealed class EnvelopeLocationPiiReader(IPiiEnvelopeProtector envelope) : ILocationPiiReader
{
    private const int MaximumConcurrentLocations = 8;

    public async Task<IReadOnlyList<LocationPiiText?>> ReadAsync(
        IReadOnlyList<StoredLocationPii> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var texts = new LocationPiiText?[locations.Count];
        if (locations.Count == 0)
        {
            return texts;
        }

        using var keyServiceDown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(
                    Enumerable.Range(0, locations.Count),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaximumConcurrentLocations,
                        CancellationToken = keyServiceDown.Token,
                    },
                    async (index, token) =>
                    {
                        try
                        {
                            texts[index] = await ReadOneAsync(locations[index], token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (PiiProtectionUnavailableException)
                        {
                            await keyServiceDown.CancelAsync().ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Rejected for its row (tampered, moved, malformed): only this location stays unreadable.
                            texts[index] = null;
                        }
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The key service went down mid-batch: the locations not read yet stay unreadable.
        }

        return texts;
    }

    private async Task<LocationPiiText> ReadOneAsync(StoredLocationPii location, CancellationToken cancellationToken)
    {
        var binding = new PiiBinding(location.OwnerOrganizationId, location.LocationId);
        var stored = await envelope.UnprotectAsync(
                binding,
                AzureKeyVaultLocationPiiProtector.AddressTextPurpose,
                location.AddressCiphertext,
                location.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var contactName = location.ContactNameCiphertext is null
            ? null
            : await envelope.UnprotectAsync(
                    binding,
                    AzureKeyVaultLocationPiiProtector.ContactNamePurpose,
                    location.ContactNameCiphertext,
                    location.KeyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        var (addressText, references) = LocationAddressText.Split(stored);
        return new LocationPiiText(addressText, references, contactName);
    }
}

/// <summary>
/// <c>Locations:PiiProtector=Mock</c> (Development, Testing and DEV_SYNTHETIC): the mock stored one-way digests, so
/// nothing can be read back. Every location gets the same fixed placeholders, so the flows that show a location can be
/// exercised end to end without any real personal data.
/// </summary>
public sealed class SyntheticLocationPiiReader : ILocationPiiReader
{
    public const string AddressPlaceholder = "Dirección sintética de prueba";
    public const string ContactNamePlaceholder = "Contacto Sintético";

    public Task<IReadOnlyList<LocationPiiText?>> ReadAsync(
        IReadOnlyList<StoredLocationPii> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LocationPiiText?>>(locations
            .Select(location => (LocationPiiText?)new LocationPiiText(
                AddressPlaceholder,
                null,
                location.ContactNameCiphertext is null ? null : ContactNamePlaceholder))
            .ToArray());
    }
}

/// <summary>No protector is configured: no location can be read back.</summary>
public sealed class DisabledLocationPiiReader : ILocationPiiReader
{
    public Task<IReadOnlyList<LocationPiiText?>> ReadAsync(
        IReadOnlyList<StoredLocationPii> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return Task.FromResult<IReadOnlyList<LocationPiiText?>>(new LocationPiiText?[locations.Count]);
    }
}
