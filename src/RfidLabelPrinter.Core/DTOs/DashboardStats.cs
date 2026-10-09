namespace RfidLabelPrinter.Core.DTOs;

/// <summary>
/// Aggregated dashboard statistics for the web UI.
/// </summary>
public class DashboardStats
{
    public int TotalJobs { get; set; }
    public int PendingCount { get; set; }
    public int ProcessingCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public double SuccessRate => TotalJobs > 0
        ? Math.Round((double)SuccessCount / TotalJobs * 100, 1)
        : 0;
}
