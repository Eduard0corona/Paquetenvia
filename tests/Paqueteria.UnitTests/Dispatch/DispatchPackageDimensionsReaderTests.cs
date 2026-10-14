using Dispatch.Infrastructure.Assignments;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>
/// The Dispatch reader behind assignDriver, listAssignableDrivers and the external offers (create, list and accept).
/// </summary>
public sealed class DispatchPackageDimensionsReaderTests : PackageDimensionsReaderContract
{
    protected override bool TryRead(string json, out int? length, out int? width, out int? height) =>
        PostgreSqlAssignmentToOrderCoordinator.TryReadDimensions(json, out length, out width, out height);
}
