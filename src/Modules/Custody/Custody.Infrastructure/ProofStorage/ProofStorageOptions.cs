namespace Custody.Infrastructure.ProofStorage;

public enum ProofStorageProvider
{
    Disabled,
    S3Compatible,

    /// <summary>ADP-001-POD-BLOB-DEFENDER: Azure Blob Storage with user-delegation SAS uploads.</summary>
    AzureBlob,
}

public enum ProofThreatScannerProvider
{
    Disabled,
    Synthetic,

    /// <summary>ADP-001-POD-BLOB-DEFENDER: the Microsoft Defender for Storage malware-scanning verdict.</summary>
    DefenderForStorage,
}

public sealed class ProofStorageOptions
{
    public const string SectionName = "ProofStorage";

    public ProofStorageProvider Provider { get; set; }
    public ProofThreatScannerProvider ThreatScanner { get; set; }
    public string ServiceUrl { get; set; } = string.Empty;
    public string PublicPresignUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = string.Empty;
    public bool ForcePathStyle { get; set; } = true;
    public int UploadUrlLifetimeMinutes { get; set; } = 15;
    public int DownloadUrlLifetimeMinutes { get; set; } = 5;
    public int SessionLifetimeMinutes { get; set; } = 30;
    public int ProcessingIntervalSeconds { get; set; } = 5;
    public int StaleValidationSeconds { get; set; } = 5 * 60;
    public int MaximumConcurrency { get; set; } = 4;
    public long MaximumBytes { get; set; } = 10 * 1024 * 1024;
    public long MaximumTextBytes { get; set; } = 4 * 1024;
    public string QuarantinePrefix { get; set; } = "quarantine/";
    public string FinalPrefix { get; set; } = "proofs/";
    public AzureBlobProofStorageOptions AzureBlob { get; set; } = new();
    public DefenderForStorageScannerOptions DefenderForStorage { get; set; } = new();
}

/// <summary>
/// <c>ProofStorage:AzureBlob</c>. Authentication is the workload managed identity only; there is no
/// account key, connection string or SAS in configuration.
/// </summary>
public sealed class AzureBlobProofStorageOptions
{
    /// <summary>Blob service endpoint, <c>https://{account}.blob.core.windows.net</c>.</summary>
    public string ServiceUri { get; set; } = string.Empty;

    public string ContainerName { get; set; } = "proofs";

    /// <summary>
    /// Lifetime of the cached user delegation key that signs the short-lived SAS. It must outlive
    /// the longest SAS it signs, so it is validated against <c>UploadUrlLifetimeMinutes</c>.
    /// </summary>
    public int UserDelegationKeyLifetimeMinutes { get; set; } = 60;
}

/// <summary>
/// <c>ProofStorage:DefenderForStorage</c>. Defaults follow Microsoft's "Understand malware scanning
/// results" documentation; they are configurable because blob index tag keys are case-sensitive.
/// </summary>
public sealed class DefenderForStorageScannerOptions
{
    public string ScanResultTagName { get; set; } = "Malware Scanning scan result";
    public string ScanTimeTagName { get; set; } = "Malware Scanning scan time UTC";
    public string NoThreatsFoundValue { get; set; } = "No threats found";
    public string MaliciousValue { get; set; } = "Malicious";
}
