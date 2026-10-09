using Microsoft.Extensions.Options;
using RfidLabelPrinter.Core.Configuration;
using RfidLabelPrinter.Core.Interfaces;
using RfidLabelPrinter.Core.Models;

namespace RfidLabelPrinter.Api.Workers;

/// <summary>
/// Background worker service that continuously polls the database for pending print jobs,
/// generates ZPL commands, and sends them to the Zebra ZD621T RFID printer.
/// 
/// Concurrency Control:
/// - Only ONE instance of this worker processes jobs at any time (singleton + SemaphoreSlim).
/// - Jobs are claimed atomically using SQL Server row-level locking.
/// - Each label is sent individually with inter-label delay for reliable RFID encoding.
/// 
/// Error Handling:
/// - Failed RFID writes are retried up to MaxRetries (default: 3).
/// - After max retries, jobs are permanently marked as Failed.
/// - Every attempt is logged to the PrintLogs table for audit.
/// 
/// Crash Recovery:
/// - On startup, any jobs stuck in "Processing" state (from a previous crash)
///   are reset to "Pending" for re-processing.
/// </summary>
public class PrintJobWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PrinterSettings _settings;
    private readonly ILogger<PrintJobWorker> _logger;

    // Ensures only one processing cycle runs at a time
    private static readonly SemaphoreSlim _processingLock = new(1, 1);

    public PrintJobWorker(
        IServiceProvider serviceProvider,
        IOptions<PrinterSettings> settings,
        ILogger<PrintJobWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Called once when the application starts.
    /// Resets any jobs stuck in Processing state from a previous crash.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrintJobWorker starting. Checking for stuck jobs...");

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPrintJobRepository>();
        await repository.ResetStuckProcessingJobsAsync(cancellationToken);

        _logger.LogInformation(
            "PrintJobWorker started. Polling every {Interval}ms, batch size: {BatchSize}",
            _settings.PollingIntervalMs, _settings.BatchSize);

        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Main processing loop. Polls the database for pending jobs and processes them.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let the application fully start
        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("PrintJobWorker stopping gracefully");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in PrintJobWorker processing loop");
            }

            // Wait before next poll
            try
            {
                await Task.Delay(_settings.PollingIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Claims and processes a batch of pending jobs.
    /// </summary>
    private async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        // Ensure only one processing cycle at a time
        if (!await _processingLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("Another processing cycle is already running, skipping");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPrintJobRepository>();
            var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();
            var zplGenerator = scope.ServiceProvider.GetRequiredService<IZplGenerator>();

            // Claim a batch of pending jobs atomically
            var jobs = await repository.ClaimPendingJobsAsync(_settings.BatchSize, ct);

            if (jobs.Count == 0)
            {
                _logger.LogDebug("No pending jobs found");
                return;
            }

            _logger.LogInformation("Processing batch of {Count} print jobs", jobs.Count);

            // Process each job individually for reliable RFID encoding
            // (RFID requires per-label acknowledgment, batch ZPL can cause sync issues)
            foreach (var job in jobs)
            {
                if (ct.IsCancellationRequested) break;

                await ProcessSingleJobAsync(job, repository, printerService, zplGenerator, ct);

                // Inter-label delay for RFID encoding reliability
                if (_settings.InterLabelDelayMs > 0)
                {
                    await Task.Delay(_settings.InterLabelDelayMs, ct);
                }
            }

            _logger.LogInformation("Batch processing complete. Processed {Count} jobs", jobs.Count);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    /// <summary>
    /// Processes a single print job: generates ZPL, sends to printer, handles result.
    /// </summary>
    private async Task ProcessSingleJobAsync(
        PrintJob job,
        IPrintJobRepository repository,
        IPrinterService printerService,
        IZplGenerator zplGenerator,
        CancellationToken ct)
    {
        _logger.LogDebug("Processing job {JobId} - Barcode: {Barcode}", job.Id, job.Barcode);

        string zplData = string.Empty;

        try
        {
            // Generate ZPL for this label (inject RFID commands into upstream ZPL)
            zplData = zplGenerator.InjectRfidCommands(job.LabelZpl, job.Barcode);

            // Send to printer via TCP
            bool success = await printerService.SendZplAsync(zplData, ct);

            if (success)
            {
                // Mark as successful
                await repository.UpdateJobStatusAsync(job.Id, PrintJobStatus.Success, ct: ct);

                // Log success
                await repository.AddPrintLogAsync(new PrintLog
                {
                    PrintJobId = job.Id,
                    Action = "PrintAndEncode",
                    Success = true,
                    Message = $"Successfully printed and encoded barcode: {job.Barcode}",
                    ZplSent = zplData,
                    Timestamp = DateTime.UtcNow
                }, ct);

                _logger.LogInformation("Job {JobId} completed successfully - Barcode: {Barcode}",
                    job.Id, job.Barcode);
            }
            else
            {
                // Print failed - handle retry
                await HandleFailureAsync(job, repository, "Printer communication failed - ZPL send returned false", zplData, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown - reset job to Pending so it can be retried
            await repository.UpdateJobStatusAsync(job.Id, PrintJobStatus.Pending,
                "Processing interrupted by shutdown", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(job, repository,
                $"Exception during processing: {ex.Message}", zplData, ct);
        }
    }

    /// <summary>
    /// Handles a failed print attempt with retry logic.
    /// </summary>
    private async Task HandleFailureAsync(
        PrintJob job,
        IPrintJobRepository repository,
        string errorMessage,
        string zplData,
        CancellationToken ct)
    {
        // Log the failure
        await repository.AddPrintLogAsync(new PrintLog
        {
            PrintJobId = job.Id,
            Action = job.RetryCount < _settings.MaxRetries ? "RetryScheduled" : "PermanentFailure",
            Success = false,
            Message = errorMessage,
            ZplSent = zplData,
            Timestamp = DateTime.UtcNow
        }, ct);

        if (job.RetryCount < _settings.MaxRetries)
        {
            // Schedule retry by resetting to Pending with incremented count
            await repository.IncrementRetryAsync(job.Id, errorMessage, ct);

            _logger.LogWarning(
                "Job {JobId} failed (attempt {Attempt}/{MaxRetries}), scheduled for retry: {Error}",
                job.Id, job.RetryCount + 1, _settings.MaxRetries, errorMessage);
        }
        else
        {
            // Max retries exceeded - mark as permanently failed
            await repository.MarkAsFailedAsync(job.Id,
                $"Max retries ({_settings.MaxRetries}) exceeded. Last error: {errorMessage}", ct);

            _logger.LogError(
                "Job {JobId} permanently failed after {MaxRetries} retries: {Error}",
                job.Id, _settings.MaxRetries, errorMessage);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrintJobWorker shutting down...");
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("PrintJobWorker stopped");
    }
}
