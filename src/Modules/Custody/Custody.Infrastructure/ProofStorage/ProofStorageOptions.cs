namespace Custody.Infrastructure.ProofStorage;

public enum ProofStorageProvider
{
    Disabled,
    S3Compatible,
}

public enum ProofThreatScannerProvider
{
    Disabled,
    Synthetic,
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
}
