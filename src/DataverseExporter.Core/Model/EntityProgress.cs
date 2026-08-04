namespace DataverseExporter.Core.Model;

/// <summary>
/// Resume checkpoint for one entity. Providers persist this in the target database
/// inside the same transaction as the page's bulk insert, so an interruption at any
/// point never produces duplicate or missing rows.
/// </summary>
public record EntityProgress(
    string EntityName,
    string Status,
    int NextPageNumber,
    string? PagingCookie,
    long RowsCopied)
{
    public const string StatusInProgress = "InProgress";
    public const string StatusCompleted = "Completed";
}
