using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RfidLabelPrinter.Core.DTOs;
using RfidLabelPrinter.Core.Interfaces;
using RfidLabelPrinter.Core.Models;
using RfidLabelPrinter.Infrastructure.Data;

namespace RfidLabelPrinter.Infrastructure.Repositories;

/// <summary>
/// SQL Server implementation of the print job repository.
/// Uses row-level locking for atomic job claiming to prevent race conditions.
/// </summary>
public class PrintJobRepository : IPrintJobRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<PrintJobRepository> _logger;

    public PrintJobRepository(AppDbContext context, ILogger<PrintJobRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<PrintJob>> CreateBatchAsync(List<LabelItem> labels, Guid batchId, CancellationToken ct = default)
    {
        var jobs = labels.Select(label => new PrintJob
        {
            Barcode = label.Barcode.Trim(),
            LabelZpl = label.LabelZpl,
            Status = PrintJobStatus.Pending,
            BatchId = batchId,
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToList();

        _context.PrintJobs.AddRange(jobs);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created batch {BatchId} with {Count} print jobs", batchId, jobs.Count);
        return jobs;
    }

    /// <inheritdoc />
    public async Task<List<PrintJob>> ClaimPendingJobsAsync(int batchSize, CancellationToken ct = default)
    {
        // Use raw SQL with UPDLOCK, READPAST hints for atomic claiming
        // This prevents multiple workers from picking the same jobs
        var statusString = PrintJobStatus.Pending.ToString();
        var jobs = await _context.PrintJobs
            .FromSqlRaw(@"
                SELECT TOP({0}) *
                FROM PrintJobs WITH (UPDLOCK, READPAST)
                WHERE Status = {1}
                ORDER BY CreatedAt ASC", batchSize, statusString)
            .AsTracking()
            .ToListAsync(ct);

        if (jobs.Count == 0)
            return jobs;

        // Mark all claimed jobs as Processing
        foreach (var job in jobs)
        {
            job.Status = PrintJobStatus.Processing;
            job.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Claimed {Count} pending jobs for processing", jobs.Count);

        return jobs;
    }

    /// <inheritdoc />
    public async Task UpdateJobStatusAsync(int jobId, PrintJobStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        var job = await _context.PrintJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            _logger.LogWarning("Attempted to update non-existent job {JobId}", jobId);
            return;
        }

        job.Status = status;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task IncrementRetryAsync(int jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await _context.PrintJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null) return;

        job.RetryCount++;
        job.Status = PrintJobStatus.Pending;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogWarning("Job {JobId} retry #{RetryCount}: {Error}", jobId, job.RetryCount, errorMessage);
    }

    /// <inheritdoc />
    public async Task MarkAsFailedAsync(int jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await _context.PrintJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null) return;

        job.Status = PrintJobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogError("Job {JobId} permanently failed after {RetryCount} retries: {Error}",
            jobId, job.RetryCount, errorMessage);
    }

    /// <inheritdoc />
    public async Task<List<PrintJob>> GetJobsAsync(PrintJobStatus? statusFilter = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var query = _context.PrintJobs.AsNoTracking().AsQueryable();

        if (statusFilter.HasValue)
        {
            query = query.Where(j => j.Status == statusFilter.Value);
        }

        return await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PrintJob?> GetJobByIdAsync(int jobId, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    /// <inheritdoc />
    public async Task<List<PrintJob>> GetFailedJobsAsync(CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .AsNoTracking()
            .Where(j => j.Status == PrintJobStatus.Failed)
            .OrderByDescending(j => j.UpdatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task RetryFailedJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _context.PrintJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null || job.Status != PrintJobStatus.Failed)
        {
            _logger.LogWarning("Cannot retry job {JobId}: not found or not in Failed status", jobId);
            return;
        }

        job.Status = PrintJobStatus.Pending;
        job.RetryCount = 0;
        job.ErrorMessage = null;
        job.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Job {JobId} reset for manual retry", jobId);
    }

    /// <inheritdoc />
    public async Task<int> ResetStaleProcessingJobsAsync(CancellationToken ct = default)
    {
        var stuckJobs = await _context.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Processing)
            .ToListAsync(ct);

        if (stuckJobs.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var job in stuckJobs)
        {
            job.Status = PrintJobStatus.Pending;
            job.ErrorMessage = "Reset after agent restart (was stuck in Processing)";
            job.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogWarning("Reset {Count} stale Processing jobs back to Pending.", stuckJobs.Count);
        return stuckJobs.Count;
    }

    /// <inheritdoc />
    public async Task<DashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var stats = await _context.PrintJobs
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new DashboardStats
            {
                TotalJobs = g.Count(),
                PendingCount = g.Count(j => j.Status == PrintJobStatus.Pending),
                ProcessingCount = g.Count(j => j.Status == PrintJobStatus.Processing),
                SuccessCount = g.Count(j => j.Status == PrintJobStatus.Success),
                FailedCount = g.Count(j => j.Status == PrintJobStatus.Failed)
            })
            .FirstOrDefaultAsync(ct);

        return stats ?? new DashboardStats();
    }

    /// <inheritdoc />
    public async Task AddPrintLogAsync(PrintLog log, CancellationToken ct = default)
    {
        _context.PrintLogs.Add(log);
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<PrintLog>> GetPrintLogsAsync(int jobId, CancellationToken ct = default)
    {
        return await _context.PrintLogs
            .AsNoTracking()
            .Where(l => l.PrintJobId == jobId)
            .OrderByDescending(l => l.Timestamp)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task ResetStuckProcessingJobsAsync(CancellationToken ct = default)
    {
        var stuckJobs = await _context.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Processing)
            .ToListAsync(ct);

        if (stuckJobs.Count == 0) return;

        foreach (var job in stuckJobs)
        {
            job.Status = PrintJobStatus.Pending;
            job.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogWarning("Reset {Count} stuck processing jobs to Pending on startup", stuckJobs.Count);
    }
}
