using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.IntegrationTests.Operations;

internal sealed record Ops001ScenarioData(
    int RunNumber,
    Guid OrganizationId,
    Guid DecoyOrganizationId,
    Guid DispatcherUserId,
    Guid CityId,
    Guid ServiceAreaId,
    Guid OperatingZoneId,
    Guid TariffRuleId,
    IReadOnlyList<Guid> DriverUserIds,
    IReadOnlyList<Guid> DriverIds)
{
    internal const int OrderCount = 20;
    internal const int DriverCount = 4;
    internal const int OrdersPerDriver = OrderCount / DriverCount;

    internal static Ops001ScenarioData Create(int runNumber)
    {
        if (runNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runNumber));
        }

        var prefix = $"ops001-run-{runNumber:D2}";
        return new(
            runNumber,
            DeterministicGuid($"{prefix}:organization"),
            DeterministicGuid($"{prefix}:decoy-organization"),
            DeterministicGuid("ops001:dispatcher"),
            DeterministicGuid($"{prefix}:city"),
            DeterministicGuid($"{prefix}:service-area"),
            DeterministicGuid($"{prefix}:operating-zone"),
            DeterministicGuid($"{prefix}:tariff-rule"),
            Enumerable.Range(1, DriverCount)
                .Select(index => DeterministicGuid($"{prefix}:driver-user:{index:D2}"))
                .ToArray(),
            Enumerable.Range(1, DriverCount)
                .Select(index => DeterministicGuid($"{prefix}:driver:{index:D2}"))
                .ToArray());
    }

    internal Guid DriverForOrder(int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        return DriverIds[(orderNumber - 1) / OrdersPerDriver];
    }

    internal Guid DriverUserForOrder(int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        return DriverUserIds[(orderNumber - 1) / OrdersPerDriver];
    }

    internal string IdempotencyKey(string operation, int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        if (string.IsNullOrWhiteSpace(operation) ||
            operation.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "The operation must contain only ASCII letters, digits, or hyphens.",
                nameof(operation));
        }

        return $"ops001-r{RunNumber:D2}-{operation}-{orderNumber:D2}";
    }

    internal string SyntheticAddress(string role, int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        return $"OPS-001 SYNTHETIC {role.ToUpperInvariant()} {RunNumber:D2}-{orderNumber:D2}";
    }

    internal string SyntheticContact(string role, int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        return $"OPS001 {role.ToUpperInvariant()} {RunNumber:D2}-{orderNumber:D2}";
    }

    internal string SyntheticPhone(int orderNumber)
    {
        ValidateOrderNumber(orderNumber);
        return $"0000{RunNumber:D2}{orderNumber:D2}00";
    }

    internal Guid ResourceGuid(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("A deterministic resource name is required.", nameof(resource));
        }

        return DeterministicGuid($"ops001-run-{RunNumber:D2}:{resource}");
    }

    private static void ValidateOrderNumber(int orderNumber)
    {
        if (orderNumber is < 1 or > OrderCount)
        {
            throw new ArgumentOutOfRangeException(nameof(orderNumber));
        }
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        guid[7] = (byte)((guid[7] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }
}
