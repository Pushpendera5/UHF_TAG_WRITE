using RfidLabelPrinter.Core.DTOs;
using RfidLabelPrinter.Core.Models;

namespace RfidLabelPrinter.Core.Interfaces;

/// <summary>
/// Repository interface for print job data access operations.
/// </summary>
public interface IPrintJobRepository
{
    /// <summary>
    /// Creates a batch of print jobs atomically from label items.
    /// Each item contains the barcode (for RFID) and full label ZPL (from the upstream application).
    /// </summary>
    Task<List<PrintJob>> CreateBatchAsync(List<LabelItem> labels, Guid batchId, CancellationToken ct = default);


    /// <summary>
    /// Atomically claims a batch of pending jobs for processing.
    /// Uses row-level locking to prevent duplicate processing.
    /// </summary>
    Task<List<PrintJob>> ClaimPendingJobsAsync(int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Updates the status of a job after processing.
    /// </summary>
    Task UpdateJobStatusAsync(int jobId, PrintJobStatus status, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>
    /// Increments the retry count and resets status to Pending for retry.
    /// </summary>
    Task IncrementRetryAsync(int jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Marks a job as permanently failed (max retries exceeded).
    /// </summary>
    Task MarkAsFailedAsync(int jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all jobs, optionally filtered by status, ordered by creation date.
    /// </summary>
    Task<List<PrintJob>> GetJobsAsync(PrintJobStatus? statusFilter = null, int page = 1, int pageSize = 50, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single job by ID.
    /// </summary>
    Task<PrintJob?> GetJobByIdAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all failed jobs eligible for manual retry.
    /// </summary>
    Task<List<PrintJob>> GetFailedJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resets a failed job to Pending status for manual retry.
    /// </summary>
    Task RetryFailedJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Resets all jobs stuck in Processing status back to Pending.
    /// Called at agent startup to recover from power cuts / crashes.
    /// </summary>
    Task<int> ResetStaleProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets aggregated dashboard statistics.
    /// </summary>
    Task<DashboardStats> GetDashboardStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds an audit log entry for a print attempt.
    /// </summary>
    Task AddPrintLogAsync(PrintLog log, CancellationToken ct = default);

    /// <summary>
    /// Gets recent print logs for a specific job.
    /// </summary>
    Task<List<PrintLog>> GetPrintLogsAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Resets any jobs stuck in "Processing" state (e.g., after a crash).
    /// Called during application startup.
    /// </summary>
    Task ResetStuckProcessingJobsAsync(CancellationToken ct = default);
}
