namespace RfidLabelPrinter.Core.Models;

/// <summary>
/// Audit log entry for a print attempt.
/// </summary>
public class PrintLog
{
    public long Id { get; set; }
    public int PrintJobId { get; set; }

    /// <summary>
    /// Action performed (e.g., "SendZpl", "Retry", "MarkFailed").
    /// </summary>
    public string Action { get; set; } = string.Empty;

    public bool Success { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// The ZPL payload that was sent (for debugging).
    /// </summary>
    public string? ZplSent { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Navigation
    public PrintJob? PrintJob { get; set; }
}
