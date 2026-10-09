namespace RfidLabelPrinter.Core.Models;

/// <summary>
/// Domain entity representing a single RFID label print job.
/// Each job corresponds to one label to be printed.
/// </summary>
public class PrintJob
{
    public int Id { get; set; }

    /// <summary>
    /// The barcode value to encode into the RFID tag EPC memory.
    /// This is the same barcode shown on the physical label.
    /// </summary>
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    /// Full ZPL data received from the upstream application.
    /// Contains the complete label layout (product name, weight, price, barcode, etc.).
    /// Our system injects RFID commands into this before sending to printer.
    /// </summary>
    public string LabelZpl { get; set; } = string.Empty;

    /// <summary>
    /// Current processing status of this job.
    /// </summary>
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;

    /// <summary>
    /// Number of times this job has been retried after failure.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Error message from the last failed attempt, if any.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Groups jobs that were submitted together in a single API call.
    /// </summary>
    public Guid BatchId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
