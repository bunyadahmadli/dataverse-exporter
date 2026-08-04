namespace DataverseExporter.Core.Model;

/// <summary>Outcome of the post-export verification checks for one entity.</summary>
public record VerificationResult(
    string EntityName,
    long TargetRows,
    long CopiedRows,
    long? CrmSnapshotRows,
    int SamplesChecked,
    int SampleMismatches,
    int ChangedAfterMigration,
    string Status, // OK | Warning | Error
    string? Details)
{
    public const string StatusOk = "OK";
    public const string StatusWarning = "Warning";
    public const string StatusError = "Error";
}
