namespace RfidLabelPrinter.Core.DTOs;

/// <summary>
/// Response DTO representing a single print job.
/// </summary>
public class PrintJobResponse
{
    public int Id { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid BatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
