namespace Drivers.Domain;

public enum DriverType
{
    Own,
    External,
    Ally,
}

public enum VehicleType
{
    Motorcycle,
    Car,
    Van,
    Bicycle,
    Walker,
}

public enum DriverProfileStatus
{
    Pending,
    Active,
    Suspended,
    Inactive,
}

public enum DriverServiceAreaStatus
{
    Active,
    Inactive,
}

public enum DriverDocumentType
{
    Identity,
    DriverLicense,
    VehicleCard,
    Insurance,
    BackgroundCheck,
    Other,
}

public enum DriverDocumentStatus
{
    Pending,
    Valid,
    Rejected,
    Expired,
    Revoked,
}

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02: what backs a driver document. <see cref="File"/> is a stored object with its
/// SHA-256 (the original DSP-001 shape, kept for a later upload path); <see cref="InPerson"/> is a dispatcher who saw
/// the original and recorded only its type, expiry and decision, with no file, key or hash.
/// </summary>
public enum DriverDocumentEvidenceKind
{
    File,
    InPerson,
}

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (Y5, Y12): where the expiry of an in-person document comes from - the date printed
/// on it, or the review date of a permanent Sinaloa driver license (the review day plus 12 months).
/// </summary>
public enum DriverDocumentExpiryBasis
{
    PrintedExpiry,
    PermanentLicenseReview,
}

/// <summary>DRV-DOC-001-IN-PERSON-2026-11-02 (Y7): the fixed reasons for a rejection; there is no free text.</summary>
public enum DriverDocumentRejectionCode
{
    Illegible,
    Expired,
    WrongDocument,
    HolderMismatch,
    Incomplete,
    NotAccepted,
}

public static class DriverEnumContract
{
    public static string ToContractValue(this DriverType value) => value switch
    {
        DriverType.Own => "OWN",
        DriverType.External => "EXTERNAL",
        DriverType.Ally => "ALLY",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this VehicleType value) => value switch
    {
        VehicleType.Motorcycle => "MOTORCYCLE",
        VehicleType.Car => "CAR",
        VehicleType.Van => "VAN",
        VehicleType.Bicycle => "BICYCLE",
        VehicleType.Walker => "WALKER",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverProfileStatus value) => value switch
    {
        DriverProfileStatus.Pending => "PENDING",
        DriverProfileStatus.Active => "ACTIVE",
        DriverProfileStatus.Suspended => "SUSPENDED",
        DriverProfileStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverServiceAreaStatus value) => value switch
    {
        DriverServiceAreaStatus.Active => "ACTIVE",
        DriverServiceAreaStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentType value) => value switch
    {
        DriverDocumentType.Identity => "IDENTITY",
        DriverDocumentType.DriverLicense => "DRIVER_LICENSE",
        DriverDocumentType.VehicleCard => "VEHICLE_CARD",
        DriverDocumentType.Insurance => "INSURANCE",
        DriverDocumentType.BackgroundCheck => "BACKGROUND_CHECK",
        DriverDocumentType.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentStatus value) => value switch
    {
        DriverDocumentStatus.Pending => "PENDING",
        DriverDocumentStatus.Valid => "VALID",
        DriverDocumentStatus.Rejected => "REJECTED",
        DriverDocumentStatus.Expired => "EXPIRED",
        DriverDocumentStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentEvidenceKind value) => value switch
    {
        DriverDocumentEvidenceKind.File => "FILE",
        DriverDocumentEvidenceKind.InPerson => "IN_PERSON",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentExpiryBasis value) => value switch
    {
        DriverDocumentExpiryBasis.PrintedExpiry => "PRINTED_EXPIRY",
        DriverDocumentExpiryBasis.PermanentLicenseReview => "PERMANENT_LICENSE_REVIEW",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentRejectionCode value) => value switch
    {
        DriverDocumentRejectionCode.Illegible => "ILLEGIBLE",
        DriverDocumentRejectionCode.Expired => "EXPIRED",
        DriverDocumentRejectionCode.WrongDocument => "WRONG_DOCUMENT",
        DriverDocumentRejectionCode.HolderMismatch => "HOLDER_MISMATCH",
        DriverDocumentRejectionCode.Incomplete => "INCOMPLETE",
        DriverDocumentRejectionCode.NotAccepted => "NOT_ACCEPTED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
