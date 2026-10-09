using Microsoft.AspNetCore.Mvc;
using RfidLabelPrinter.Core.DTOs;
using RfidLabelPrinter.Core.Interfaces;
using RfidLabelPrinter.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace RfidLabelPrinter.Api.Controllers;

/// <summary>
/// API controller for managing RFID label print jobs.
/// Accepts batch submissions from external clients.
/// </summary>
[ApiController]
[Route("api/print-jobs")]
public class PrintJobsController : ControllerBase
{
    private readonly IPrintJobRepository _repository;
    private readonly ILogger<PrintJobsController> _logger;

    public PrintJobsController(
        IPrintJobRepository repository,
        ILogger<PrintJobsController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Creates a batch of print jobs from an array of barcodes.
    /// Each barcode becomes one label that will be printed with both
    /// a visual barcode and an RFID EPC encoding of the same value.
    /// </summary>
    /// <param name="request">Request containing barcode values.</param>
    /// <returns>Batch ID and job count for tracking.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(BatchCreateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePrintJobs([FromBody] CreatePrintJobRequest request)
    {
        // Normalize request payload:
        // 1) Existing batch format (labels[])
        // 2) Simple URL format (url/pdfUrl/barcodePdfUrl) with optional barcode
        try
        {
            NormalizeLabelsFromSimplePayload(request);
            NormalizeBatchLabelItems(request);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (request.Labels == null || request.Labels.Count == 0)
        {
            return BadRequest(new
            {
                error = "Provide either labels[] or a PDF URL field (url/pdfUrl/barcodePdfUrl)."
            });
        }

        // Validate individual labels
        var invalidLabels = request.Labels
            .Where(l => string.IsNullOrWhiteSpace(l.Barcode) || string.IsNullOrWhiteSpace(l.LabelZpl))
            .ToList();

        if (invalidLabels.Count > 0)
        {
            return BadRequest(new { error = "One or more labels have empty barcode or ZPL data." });
        }

        // Check for duplicates within the batch (based on Barcode)
        var duplicates = request.Labels
            .GroupBy(l => l.Barcode.Trim())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            _logger.LogWarning("Batch contains duplicate barcodes: {Duplicates}",
                string.Join(", ", duplicates));
        }

        var batchId = Guid.NewGuid();
        var jobs = await _repository.CreateBatchAsync(request.Labels, batchId);

        _logger.LogInformation(
            "Created print job batch {BatchId} with {Count} labels",
            batchId, jobs.Count);

        var response = new BatchCreateResponse
        {
            BatchId = batchId,
            JobCount = jobs.Count,
            Message = $"Successfully queued {jobs.Count} labels for printing."
        };

        return CreatedAtAction(nameof(GetPrintJobs), null, response);
    }

    private static void NormalizeLabelsFromSimplePayload(CreatePrintJobRequest request)
    {
        if (request.Labels is { Count: > 0 })
            return;

        var pdfUrl = FirstNonEmpty(request.PdfUrl, request.Url, request.BarcodePdfUrl);
        if (string.IsNullOrWhiteSpace(pdfUrl))
            return;

        var barcode = string.IsNullOrWhiteSpace(request.Barcode)
            ? ExtractBarcodeFromPdfUrl(pdfUrl)
            : request.Barcode.Trim();

        request.Labels = new List<LabelItem>
        {
            new()
            {
                Barcode = barcode,
                // We intentionally store the URL in LabelZpl.
                // Local worker detects URL and fetches PDF for rendering.
                LabelZpl = pdfUrl.Trim()
            }
        };
    }

    private static void NormalizeBatchLabelItems(CreatePrintJobRequest request)
    {
        if (request.Labels is not { Count: > 0 })
            return;

        foreach (var item in request.Labels)
        {
            var pdfUrl = FirstNonEmpty(item.PdfUrl, item.Url, item.BarcodePdfUrl);
            if (string.IsNullOrWhiteSpace(item.LabelZpl) && !string.IsNullOrWhiteSpace(pdfUrl))
            {
                item.LabelZpl = pdfUrl.Trim();
            }

            if (string.IsNullOrWhiteSpace(item.Barcode) && !string.IsNullOrWhiteSpace(pdfUrl))
            {
                item.Barcode = ExtractBarcodeFromPdfUrl(pdfUrl);
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string ExtractBarcodeFromPdfUrl(string url)
    {
        // Expected pattern: .../barcode_FE3B3F00001.pdf
        var match = Regex.Match(url, @"barcode_([A-Za-z0-9]+)\.pdf($|\?)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new ArgumentException(
                "Could not extract barcode from URL. Expected format like barcode_FE3B3F00001.pdf");
        }

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Retrieves print jobs with optional status filtering and pagination.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<PrintJobResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrintJobs(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        PrintJobStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<PrintJobStatus>(status, ignoreCase: true, out var parsed))
        {
            statusFilter = parsed;
        }

        var jobs = await _repository.GetJobsAsync(statusFilter, page, pageSize);

        var response = jobs.Select(j => new PrintJobResponse
        {
            Id = j.Id,
            Barcode = j.Barcode,
            Status = j.Status.ToString(),
            RetryCount = j.RetryCount,
            ErrorMessage = j.ErrorMessage,
            BatchId = j.BatchId,
            CreatedAt = j.CreatedAt,
            UpdatedAt = j.UpdatedAt
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Retrieves a single print job by ID.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrintJob(int id)
    {
        var job = await _repository.GetJobByIdAsync(id);
        if (job == null)
            return NotFound(new { error = $"Print job {id} not found." });

        return Ok(new PrintJobResponse
        {
            Id = job.Id,
            Barcode = job.Barcode,
            Status = job.Status.ToString(),
            RetryCount = job.RetryCount,
            ErrorMessage = job.ErrorMessage,
            BatchId = job.BatchId,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt
        });
    }

    /// <summary>
    /// Retries a specific failed print job by resetting it to Pending.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryJob(int id)
    {
        var job = await _repository.GetJobByIdAsync(id);
        if (job == null)
            return NotFound(new { error = $"Print job {id} not found." });

        if (job.Status != PrintJobStatus.Failed)
            return BadRequest(new { error = $"Job {id} is not in Failed status. Current: {job.Status}" });

        await _repository.RetryFailedJobAsync(id);

        _logger.LogInformation("Job {JobId} manually retried", id);
        return Ok(new { message = $"Job {id} has been queued for retry." });
    }

    /// <summary>
    /// Retries all failed jobs in a batch.
    /// </summary>
    [HttpPost("retry-all-failed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryAllFailed()
    {
        var failedJobs = await _repository.GetFailedJobsAsync();

        foreach (var job in failedJobs)
        {
            await _repository.RetryFailedJobAsync(job.Id);
        }

        _logger.LogInformation("Manually retried {Count} failed jobs", failedJobs.Count);
        return Ok(new { message = $"Retried {failedJobs.Count} failed jobs." });
    }

    /// <summary>
    /// Resets all jobs stuck in Processing back to Pending.
    /// Should be called by the agent on startup to recover from power cuts or crashes.
    /// </summary>
    [HttpPost("reset-stale-processing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetStaleProcessing()
    {
        var count = await _repository.ResetStaleProcessingJobsAsync();
        _logger.LogWarning("Reset {Count} stale Processing jobs via API call.", count);
        return Ok(new { message = $"Reset {count} stale processing jobs to Pending." });
    }

    /// <summary>
    /// Gets dashboard statistics (total, pending, processing, success, failed).
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(DashboardStats), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _repository.GetDashboardStatsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Gets print logs for a specific job (audit trail).
    /// </summary>
    [HttpGet("{id:int}/logs")]
    [ProducesResponseType(typeof(List<PrintLog>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobLogs(int id)
    {
        var logs = await _repository.GetPrintLogsAsync(id);
        return Ok(logs);
    }

    /// <summary>
    /// Converts a raw EPC value from a reader, usually shown as hex bytes,
    /// back to the barcode text written by the local agent.
    /// </summary>
    [HttpGet("decode-epc/{epcHex}")]
    [ProducesResponseType(typeof(EpcDecodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DecodeEpc(string epcHex)
    {
        try
        {
            var barcode = DecodeAsciiHex(epcHex);
            return Ok(new EpcDecodeResponse
            {
                EpcHex = NormalizeHex(epcHex),
                Barcode = barcode
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Claims pending print jobs for processing by a local agent.
    /// This marks the jobs as 'Processing' so other workers won't pick them up.
    /// </summary>
    [HttpPost("claim")]
    [ProducesResponseType(typeof(List<PrintJob>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ClaimJobs([FromQuery] int batchSize = 10)
    {
        var jobs = await _repository.ClaimPendingJobsAsync(batchSize);
        _logger.LogInformation("Claimed {Count} jobs for local agent", jobs.Count);
        return Ok(jobs);
    }

    /// <summary>
    /// Updates the status of a print job (e.g., to Success or Failed).
    /// </summary>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateJobStatusRequest request)
    {
        var job = await _repository.GetJobByIdAsync(id);
        if (job == null)
            return NotFound(new { error = $"Print job {id} not found." });

        if (!Enum.TryParse<PrintJobStatus>(request.Status, true, out var status))
        {
            return BadRequest(new { error = $"Invalid status: {request.Status}" });
        }

        await _repository.UpdateJobStatusAsync(id, status, request.ErrorMessage);
        
        // Add a log entry
        await _repository.AddPrintLogAsync(new PrintLog
        {
            PrintJobId = id,
            Message = $"Status updated to {status} by local agent. {(string.IsNullOrEmpty(request.ErrorMessage) ? "" : "Error: " + request.ErrorMessage)}",
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation("Job {JobId} updated to {Status} by local agent", id, status);
        return Ok();
    }

    private static string DecodeAsciiHex(string epcHex)
    {
        var hex = NormalizeHex(epcHex);
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            throw new ArgumentException("EPC hex must contain an even number of hex characters.");
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            string pair = hex.Substring(i * 2, 2);
            if (!byte.TryParse(pair, System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                throw new ArgumentException("EPC contains non-hex characters.");
            }
        }

        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    private static string NormalizeHex(string value)
    {
        var hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        return Regex.Replace(hex, @"[\s:-]", "").ToUpperInvariant();
    }
}
