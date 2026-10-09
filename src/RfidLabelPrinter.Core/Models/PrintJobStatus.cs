namespace RfidLabelPrinter.Core.Models;

/// <summary>
/// Represents the lifecycle status of a print job.
/// </summary>
public enum PrintJobStatus
{
    Pending,
    Processing,
    Success,
    Failed
}
