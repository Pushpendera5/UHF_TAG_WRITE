namespace RfidLabelPrinter.Core.DTOs;

/// <summary>
/// Response DTO returned when a batch of print jobs is created.
/// </summary>
public class BatchCreateResponse
{
    public Guid BatchId { get; set; }
    public int JobCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
