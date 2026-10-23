using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace RfidLabelPrinter.LocalAgent;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => { options.ServiceName = "RfidLabelPrinterAgent"; })
            .ConfigureAppConfiguration((ctx, config) =>
            {
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddHttpClient<PrinterWorker>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddHostedService<PrinterWorker>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        logging.AddEventLog(options =>
                        {
                            options.SourceName = "RfidLabelPrinterAgent";
                        });
                    }
                    catch
                    {
                        // Ignore if running as non-admin in console mode
                    }
                }
            })
            .Build();

        await host.RunAsync();
    }
}

public class PrinterWorker : BackgroundService
{
    private const int DefaultLabelWidthDots = 1181;
    private const int DefaultLabelLengthDots = 591;
    private const int DefaultEpcWordCount = 6;

    private readonly ILogger<PrinterWorker> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    private string CloudApiUrl = "";
    private string PrinterIp = "";
    private int PrinterPort = 9100;
    private int PollingIntervalMs = 300;
    private int ClaimBatchSize = 1;
    private int ClaimMaxLoopsPerCycle = 10;
    private bool EnableRfidWrite = true;
    private int PdfRenderDpi = 300;
    private int PdfDownloadRetryCount = 5;
    private int PdfDownloadRetryDelayMs = 800;
    private int PrinterDpi = 300;
    private int BlackThreshold = 240;
    private int AlphaThreshold = 5;
    private int LabelWidthDots = DefaultLabelWidthDots;
    private int LabelLengthDots = DefaultLabelLengthDots;
    private bool PrintPdfAsGraphics = true;

    // --- Clean Positioning & Sizing Controls (All Configurable via appsettings.json) ---
    private int PrintOffsetX = 590;
    private int PrintOffsetY = 15;
    private int PdfSizePercent = 55;
    private bool AutoCenterOnFlap = true;
    private bool AutoTrimWhitespace = true;
    private bool SingleSidePrintOnLowerFlap = true;
    private int SingleSideOffsetY = 0;
    private float TemplateBaseTopPercent = 22.5f;
    private int SingleSideThresholdPercent = 45;
    private int TrimPaddingDots = 4;
    private int LabelTopShiftDots = 0;
    private int PrintAreaWidthDots = 0;
    private int PrintAreaHeightDots = 0;

    // --- RFID Parameters (100% Preserved) ---
    private string RfidMode = "H";
    private int RfidBlockNumber = 1;
    private int RfidStartWord = 2;
    private int RfidWordCount = 4;
    private bool RfidClearTrailingEpcWords = false;
    private int RfidTotalEpcWordsToClear = DefaultEpcWordCount;
    private bool UseEpcBankWriteFormat = true;
    private string RfidHexInputMode = "Always";
    private bool DumpZplToFile = true;
    private string DumpZplPath = "";
    private int InterJobDelayMs = 500;
    private bool PreferPdfTextToZpl = false;

    public PrinterWorker(ILogger<PrinterWorker> logger, HttpClient httpClient, IConfiguration config)
    {
        _logger = logger;
        _httpClient = httpClient;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig();

        _logger.LogInformation("==================================================");
        _logger.LogInformation("   RFID LABEL PRINTER - LOCAL AGENT STARTED     ");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("Cloud API: {Url}", CloudApiUrl);
        _logger.LogInformation("Local Printer: {Ip}:{Port}", PrinterIp, PrinterPort);
        _logger.LogInformation("RFID Write: {Status}", EnableRfidWrite ? "Enabled" : "Disabled");
        _logger.LogInformation("Printer DPI: {Dpi}, Label Size: {W}x{H} dots", PrinterDpi, LabelWidthDots, LabelLengthDots);
        _logger.LogInformation("Positioning: OffsetX={X}, OffsetY={Y}, Size={Scale}%, AutoCenter={Center}, AutoTrim={Trim}",
            PrintOffsetX, PrintOffsetY, PdfSizePercent, AutoCenterOnFlap ? "Yes" : "No", AutoTrimWhitespace ? "Yes" : "No");
        _logger.LogInformation("RFID Params: ^RFW,{Mode},{Block},{Start},{Words}, BankFormat={Bank}",
            RfidMode, RfidBlockNumber, RfidStartWord, RfidWordCount, UseEpcBankWriteFormat ? "Enabled" : "Disabled");
        _logger.LogInformation("--------------------------------------------------");

        // On startup, reset any jobs stuck in Processing state (power cut / crash recovery)
        try
        {
            var resetResponse = await _httpClient.PostAsync($"{CloudApiUrl}/reset-stale-processing", null, stoppingToken);
            if (resetResponse.IsSuccessStatusCode)
                _logger.LogInformation("Startup: stale Processing jobs reset successfully.");
            else
                _logger.LogWarning("Startup: reset-stale-processing returned {Status}", resetResponse.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup: could not call reset-stale-processing (API may be unreachable).");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ProcessPendingJobsAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error during job processing cycle");
                }

                // Reload runtime-adjustable settings without restarting the agent.
                try { LoadConfig(); } catch { /* keep last known config */ }

                await Task.Delay(PollingIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent background service stopping gracefully.");
        }
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;
    }

    private void LoadConfig()
    {
        CloudApiUrl = (_config["AgentSettings:CloudApiUrl"] ?? "").Trim().TrimEnd('/');
        PrinterIp = (_config["AgentSettings:PrinterIp"] ?? "").Trim();
        PrinterPort = ParseInt(_config["AgentSettings:PrinterPort"], 9100);
        PollingIntervalMs = ParseInt(_config["AgentSettings:PollingIntervalMs"], 300);
        ClaimBatchSize = ParseInt(_config["AgentSettings:ClaimBatchSize"], 1);
        ClaimMaxLoopsPerCycle = ParseInt(_config["AgentSettings:ClaimMaxLoopsPerCycle"], 10);
        EnableRfidWrite = bool.TryParse(_config["AgentSettings:EnableRfidWrite"], out var enableRfid) ? enableRfid : true;
        PdfRenderDpi = ParseInt(_config["AgentSettings:PdfRenderDpi"], 300);
        PdfDownloadRetryCount = Math.Max(1, ParseInt(_config["AgentSettings:PdfDownloadRetryCount"], 5));
        PdfDownloadRetryDelayMs = Math.Max(0, ParseInt(_config["AgentSettings:PdfDownloadRetryDelayMs"], 800));

        // Standard Printer DPI (correct any invalid <= 0 DPI to standard 300)
        PrinterDpi = ParseInt(_config["AgentSettings:PrinterDpi"], 300);
        if (PrinterDpi <= 0)
            PrinterDpi = 300;

        BlackThreshold = ParseInt(_config["AgentSettings:BlackThreshold"], 240);
        AlphaThreshold = ParseInt(_config["AgentSettings:AlphaThreshold"], 5);

        int defaultWidth = PrinterDpi >= 300 ? 1181 : 800;
        int defaultLength = PrinterDpi >= 300 ? 591 : 400;
        int defaultOffsetX = PrinterDpi >= 300 ? 590 : 400;
        int defaultOffsetY = PrinterDpi >= 300 ? 15 : 10;

        LabelWidthDots = ParseInt(_config["AgentSettings:LabelWidthDots"], defaultWidth);
        LabelLengthDots = ParseInt(_config["AgentSettings:LabelLengthDots"], defaultLength);

        int labelWidthMm = ParseInt(_config["AgentSettings:LabelWidthMm"], 0);
        int labelHeightMm = ParseInt(_config["AgentSettings:LabelHeightMm"], 0);
        if (labelWidthMm > 0)
            LabelWidthDots = MmToDotsAtDpi(labelWidthMm, PrinterDpi);
        if (labelHeightMm > 0)
            LabelLengthDots = MmToDotsAtDpi(labelHeightMm, PrinterDpi);

        PrintPdfAsGraphics = bool.TryParse(_config["AgentSettings:PrintPdfAsGraphics"], out var printPdf) ? printPdf : true;

        // Clean Positioning Parameters with backward compatibility fallbacks
        PrintOffsetX = ParseInt(
            _config["AgentSettings:PrintOffsetX"] ??
            _config["AgentSettings:PdfLeftRightDots"] ??
            _config["AgentSettings:PdfOffsetXDots"],
            defaultOffsetX);

        PrintOffsetY = ParseInt(
            _config["AgentSettings:PrintOffsetY"] ??
            _config["AgentSettings:PdfUpDownDots"] ??
            _config["AgentSettings:PdfOffsetYDots"],
            defaultOffsetY);

        int printOffsetXMm = ParseInt(_config["AgentSettings:PrintOffsetXMm"], 0);
        int printOffsetYMm = ParseInt(_config["AgentSettings:PrintOffsetYMm"], 0);
        if (printOffsetXMm > 0)
            PrintOffsetX = MmToDotsAtDpi(printOffsetXMm, PrinterDpi);
        if (printOffsetYMm > 0)
            PrintOffsetY = MmToDotsAtDpi(printOffsetYMm, PrinterDpi);

        PdfSizePercent = Math.Clamp(
            ParseInt(_config["AgentSettings:PdfSizePercent"] ?? _config["AgentSettings:PdfScalePercent"], 55),
            10,
            200);

        AutoCenterOnFlap = bool.TryParse(_config["AgentSettings:AutoCenterOnFlap"], out var autoCenter) ? autoCenter : true;

        AutoTrimWhitespace = bool.TryParse(
            _config["AgentSettings:AutoTrimWhitespace"] ?? _config["AgentSettings:TrimPdfWhitespace"],
            out var autoTrim) ? autoTrim : true;

        SingleSidePrintOnLowerFlap = bool.TryParse(_config["AgentSettings:SingleSidePrintOnLowerFlap"], out var ssLower) ? ssLower : true;
        SingleSideOffsetY = ParseInt(_config["AgentSettings:SingleSideOffsetY"], 0);

        TemplateBaseTopPercent = float.TryParse(
            _config["AgentSettings:TemplateBaseTopPercent"]?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var tbTop) ? tbTop : 22.5f;

        SingleSideThresholdPercent = Math.Clamp(
            ParseInt(_config["AgentSettings:SingleSideThresholdPercent"], 45),
            10,
            90);

        TrimPaddingDots = Math.Max(0, ParseInt(_config["AgentSettings:TrimPaddingDots"], 4));

        LabelTopShiftDots = ParseInt(_config["AgentSettings:LabelTopShiftDots"], 0);

        PrintAreaWidthDots = ParseInt(_config["AgentSettings:PrintAreaWidthDots"], 0);
        PrintAreaHeightDots = ParseInt(_config["AgentSettings:PrintAreaHeightDots"], 0);

        int printAreaWidthMm = ParseInt(_config["AgentSettings:PrintAreaWidthMm"], 0);
        int printAreaHeightMm = ParseInt(_config["AgentSettings:PrintAreaHeightMm"], 0);
        if (printAreaWidthMm > 0)
            PrintAreaWidthDots = MmToDotsAtDpi(printAreaWidthMm, PrinterDpi);
        if (printAreaHeightMm > 0)
            PrintAreaHeightDots = MmToDotsAtDpi(printAreaHeightMm, PrinterDpi);

        // --- RFID Parameters (100% UNTOUCHED) ---
        RfidMode = (_config["AgentSettings:RfidMode"] ?? "H").Trim().ToUpperInvariant();
        RfidBlockNumber = ParseInt(_config["AgentSettings:RfidBlockNumber"], 1);
        RfidStartWord = ParseInt(_config["AgentSettings:RfidStartWord"], 2);
        RfidWordCount = ParseInt(_config["AgentSettings:RfidWordCount"], 4);
        RfidClearTrailingEpcWords = bool.TryParse(_config["AgentSettings:RfidClearTrailingEpcWords"], out var clearEpc) ? clearEpc : false;
        RfidTotalEpcWordsToClear = ParseInt(_config["AgentSettings:RfidTotalEpcWordsToClear"], DefaultEpcWordCount);
        UseEpcBankWriteFormat = bool.TryParse(_config["AgentSettings:UseEpcBankWriteFormat"], out var useEpcBank) ? useEpcBank : true;
        RfidHexInputMode = (_config["AgentSettings:RfidHexInputMode"] ?? "Always").Trim();
        DumpZplToFile = bool.TryParse(_config["AgentSettings:DumpZplToFile"], out var dumpZpl) ? dumpZpl : true;
        DumpZplPath = _config["AgentSettings:DumpZplPath"] ?? "";
        InterJobDelayMs = ParseInt(_config["AgentSettings:InterJobDelayMs"], 500);
        PreferPdfTextToZpl = bool.TryParse(_config["AgentSettings:PreferPdfTextToZpl"], out var preferText) ? preferText : false;
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        int loopCount = 0;
        while (loopCount < ClaimMaxLoopsPerCycle && !stoppingToken.IsCancellationRequested)
        {
            var response = await _httpClient.PostAsync($"{CloudApiUrl}/claim?batchSize={ClaimBatchSize}", null, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claim API returned status {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
                return;
            }

            var jobs = await response.Content.ReadFromJsonAsync<List<PrintJobDto>>(stoppingToken);
            if (jobs == null || jobs.Count == 0) return;

            _logger.LogInformation("Claimed {Count} jobs.", jobs.Count);

            for (int i = 0; i < jobs.Count; i++)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var job = jobs[i];
                _logger.LogInformation("Job {Id}: Processing {Barcode}", job.Id, job.Barcode);

                try
                {
                    byte[] printerData = await PreparePrinterDataAsync(job);
                    await SendToPrinterAsync(printerData);
                    await UpdateStatusAsync(job.Id, "Success", null);
                    _logger.LogInformation("Job {Id}: Success - Printed & Encoded.", job.Id);
                }
                catch (Exception ex)
                {
                    await UpdateStatusAsync(job.Id, "Failed", ex.Message);
                    _logger.LogError(ex, "Job {Id}: Failed - {Message}", job.Id, ex.Message);
                }

                if (i < jobs.Count - 1 && InterJobDelayMs > 0 && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(InterJobDelayMs, stoppingToken);
            }

            loopCount++;
        }
    }

    private async Task<byte[]> PreparePrinterDataAsync(PrintJobDto job)
    {
        string cleanBarcode = (job.Barcode ?? "").Trim();
        string rfidCmd = GetRfidZpl(cleanBarcode);

        // If the incoming LabelZpl already contains an RFID write command, skip agent-side injection
        bool labelAlreadyContainsRfid = !string.IsNullOrWhiteSpace(job.LabelZpl) &&
                                         job.LabelZpl.IndexOf("^RFW", StringComparison.OrdinalIgnoreCase) >= 0;
        if (labelAlreadyContainsRfid)
        {
            _logger.LogDebug("Job {Id}: Label already contains ^RFW; skipping RFID injection.", job.Id);
            rfidCmd = string.Empty;
        }

        if (IsPdfUrl(job.LabelZpl))
        {
            _logger.LogInformation("Job {Id}: URL detected. Downloading...", job.Id);
            var downloadedBytes = await DownloadPdfBytesAsync(job.LabelZpl);

            bool isPdf = downloadedBytes.Length >= 4 &&
                         downloadedBytes[0] == '%' &&
                         downloadedBytes[1] == 'P' &&
                         downloadedBytes[2] == 'D' &&
                         downloadedBytes[3] == 'F';

            if (isPdf)
            {
                string zplPayload = BuildPdfJobZpl(job, downloadedBytes, rfidCmd);
                WriteZplDump(job, zplPayload);
                return Encoding.ASCII.GetBytes(zplPayload);
            }
            else
            {
                string downloadedZpl = Encoding.UTF8.GetString(downloadedBytes);
                string sizeCmd = (LabelWidthDots > 0 && LabelLengthDots > 0) ? $"^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n" : "";
                string downloadedZplFinal = downloadedZpl.Contains("^XA") 
                    ? ApplyLabelOffsets(downloadedZpl.Replace("^XA", $"^XA\n^PQ1\n{sizeCmd}{rfidCmd}")) 
                    : $"^XA\n^PQ1\n{sizeCmd}{rfidCmd}{ApplyLabelOffsets(downloadedZpl)}\n^XZ";
                WriteZplDump(job, downloadedZplFinal);
                return Encoding.ASCII.GetBytes(downloadedZplFinal);
            }
        }

        if (IsPdfBase64(job.LabelZpl))
        {
            _logger.LogInformation("Job {Id}: PDF Base64 detected. Converting...", job.Id);
            var pdfBytes = DecodePdfBase64ToBytes(job.LabelZpl);
            string zplPayload = BuildPdfJobZpl(job, pdfBytes, rfidCmd);
            WriteZplDump(job, zplPayload);
            return Encoding.ASCII.GetBytes(zplPayload);
        }

        if (string.IsNullOrWhiteSpace(job.LabelZpl))
        {
            string simpleZpl = BuildSimpleBarcodeLabel(cleanBarcode, rfidCmd);
            WriteZplDump(job, simpleZpl);
            return Encoding.ASCII.GetBytes(simpleZpl);
        }

        string rawSizeCmd = (LabelWidthDots > 0 && LabelLengthDots > 0) ? $"^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n" : "";
        string zplFinal = job.LabelZpl.Contains("^XA") 
            ? ApplyLabelOffsets(job.LabelZpl.Replace("^XA", $"^XA\n^PQ1\n{rawSizeCmd}{rfidCmd}")) 
            : $"^XA\n^PQ1\n{rawSizeCmd}{rfidCmd}{ApplyLabelOffsets(job.LabelZpl)}\n^XZ";

        WriteZplDump(job, zplFinal);
        return Encoding.ASCII.GetBytes(zplFinal);
    }

    private string BuildPdfJobZpl(PrintJobDto job, byte[] pdfBytes, string rfidCmd)
    {
        if (PreferPdfTextToZpl || !PrintPdfAsGraphics)
        {
            try
            {
                string textZpl = ConvertPdfBytesToUniversalTextZpl(pdfBytes, job.Barcode);
                _logger.LogInformation("Job {Id}: Generated text-based ZPL from PDF.", job.Id);
                return $"^XA\n^PQ1\n^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}{textZpl}^XZ";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job {Id}: Text extraction failed, falling back to graphics rendering.", job.Id);
            }
        }

        string graphicsZpl = ConvertPdfBytesToZplGraphics(pdfBytes, out int labelTopShift);
        _logger.LogInformation("Job {Id}: Generated graphics-based ZPL from PDF. Position=({X},{Y})", job.Id, PrintOffsetX, PrintOffsetY);

        string ltCmd = labelTopShift != 0 ? $"^LT{labelTopShift}\n" : "";
        return $"^XA\n^PQ1\n{ltCmd}^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}{graphicsZpl}^XZ";
    }

    private async Task<byte[]> DownloadPdfBytesAsync(string pdfUrl)
    {
        string cleanUrl = (pdfUrl ?? "").Trim();
        Exception? lastError = null;

        for (int attempt = 1; attempt <= PdfDownloadRetryCount; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(cleanUrl);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new FileNotFoundException($"PDF URL returned 404 (Not Found): {cleanUrl}");

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (FileNotFoundException)
            {
                // Fail immediately on 404 without wasting time on retries
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "PDF download failed on attempt {Attempt}/{Max}: {Url}", attempt, PdfDownloadRetryCount, cleanUrl);

                if (attempt < PdfDownloadRetryCount)
                    await Task.Delay(PdfDownloadRetryDelayMs);
            }
        }

        throw new Exception($"Failed to download PDF after {PdfDownloadRetryCount} attempts: {pdfUrl}", lastError);
    }

    private static byte[] DecodePdfBase64ToBytes(string base64Pdf)
    {
        string cleanBase64 = base64Pdf.Trim()
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "");

        if (cleanBase64.Contains(","))
            cleanBase64 = cleanBase64.Split(',')[1];

        return Convert.FromBase64String(cleanBase64);
    }

    private string ConvertPdfBytesToUniversalTextZpl(byte[] pdfBytes, string barcode)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfDocument.Open(stream);

        var lines = new List<string>();

        for (int i = 1; i <= document.NumberOfPages; i++)
        {
            var page = document.GetPage(i);
            foreach (var line in ExtractTextLines(page.Text))
            {
                if (line.Length > 0)
                    lines.Add(line);
            }
        }

        if (lines.Count == 0)
            throw new Exception("No printable text extracted from PDF.");

        return BuildUniversalTextLabel(lines, barcode);
    }

    private IEnumerable<string> ExtractTextLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        string normalized = text.Replace("\r", "\n");
        var rawLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawLines)
        {
            string cleaned = SanitizeZplText(raw);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            foreach (var wrapped in WrapLine(cleaned, 42))
            {
                if (!string.IsNullOrWhiteSpace(wrapped))
                    yield return wrapped;
            }
        }
    }

    private IEnumerable<string> WrapLine(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        string remaining = text.Trim();
        while (remaining.Length > maxChars)
        {
            int splitAt = remaining.LastIndexOf(' ', maxChars);
            if (splitAt <= 0)
                splitAt = maxChars;

            yield return remaining.Substring(0, splitAt).Trim();
            remaining = remaining.Substring(splitAt).TrimStart();
        }

        if (remaining.Length > 0)
            yield return remaining;
    }

    private string BuildUniversalTextLabel(List<string> lines, string barcode)
    {
        var sb = new StringBuilder();

        int left = PrintOffsetX;
        int top = Math.Max(10, PrintOffsetY);
        int lineHeight = PrinterDpi >= 300 ? 44 : 30;
        int maxTextHeight = Math.Max(80, LabelLengthDots - (PrinterDpi >= 300 ? 180 : 120));
        int maxLines = Math.Max(1, maxTextHeight / lineHeight);

        int fontSize = PrinterDpi >= 300 ? 38 : 28;
        sb.Append($"^CF0,{fontSize}\n");

        int printed = 0;
        foreach (var line in lines)
        {
            if (printed >= maxLines)
                break;

            int y = top + (printed * lineHeight);
            sb.Append($"^FO{left},{y}^FD{SanitizeZplText(line)}^FS\n");
            printed++;
        }

        string barcodeData = SanitizeBarcodeData(barcode);
        if (!string.IsNullOrWhiteSpace(barcodeData))
        {
            int barcodeHeight = PrinterDpi >= 300 ? 110 : 80;
            int naturalY = top + (printed * lineHeight) + 12;
            int maxY = LabelLengthDots - barcodeHeight - (PrinterDpi >= 300 ? 30 : 20);
            int barcodeY = Math.Min(naturalY, maxY);
            sb.Append(PrinterDpi >= 300 ? "^BY3,2,110\n" : "^BY2,2,80\n");
            sb.Append($"^FO{left},{barcodeY}^BCN,{barcodeHeight},Y,N,N^FD{barcodeData}^FS\n");
        }

        return sb.ToString();
    }

    private static string SanitizeZplText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            if (ch < 32 || ch > 126)
            {
                sb.Append(' ');
                continue;
            }

            if (ch == '^' || ch == '~')
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(ch);
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string SanitizeBarcodeData(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return string.Empty;

        return Regex.Replace(barcode.Trim(), @"[^A-Za-z0-9\-\._/+]", "");
    }

    private void WriteZplDump(PrintJobDto job, string zpl)
    {
        if (!DumpZplToFile)
            return;

        try
        {
            string targetPath = DumpZplPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zpl-dump.txt");
            }

            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string header = $"\n----- {DateTime.Now:yyyy-MM-dd HH:mm:ss} JobId={job.Id} Barcode={job.Barcode} -----\n";
            File.AppendAllText(targetPath, header + zpl + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write ZPL dump to file.");
        }
    }

    private string GetRfidZpl(string barcode)
    {
        if (!EnableRfidWrite || string.IsNullOrWhiteSpace(barcode))
            return string.Empty;

        string trimmed = barcode.Trim();

        if (string.Equals(RfidMode, "A", StringComparison.OrdinalIgnoreCase))
        {
            int byteCount = Encoding.ASCII.GetByteCount(trimmed);
            return $"^RFW,A,2,{byteCount},1^FD{trimmed}^FS\n";
        }

        int configuredWordCount = RfidWordCount;
        string hexPayload = ShouldTreatBarcodeAsHex(trimmed)
            ? NormalizeHexPayload(trimmed, configuredWordCount, out var wordCount)
            : NormalizeHexPayload(ConvertAsciiToHex(trimmed), configuredWordCount, out wordCount);

        if (RfidWordCount > 0 && wordCount != RfidWordCount)
        {
            _logger.LogDebug("RFID word count adjusted to {Words} based on data length.", wordCount);
        }
        else if (RfidWordCount <= 0)
        {
            _logger.LogDebug("RFID word count not configured. Using data length: {Words} words.", wordCount);
        }

        if (UseEpcBankWriteFormat)
        {
            // Write PC word separately, then EPC data starting at word 2.
            int pcWord = wordCount << 11;
            string pcHex = pcWord.ToString("X4");
            string pcCmd = $"^RFW,H,1,2,1^FD{pcHex}^FS\n";
            string epcCmd = BuildEpcWriteCommand(hexPayload);
            string clearTrailingCmd = BuildTrailingEpcClearCommand(wordCount);

            return pcCmd + epcCmd + clearTrailingCmd;
        }

        return BuildEpcWriteCommand(hexPayload);
    }

    private string BuildEpcWriteCommand(string hexPayload)
    {
        int byteCount = hexPayload.Length / 2;
        return $"^RFW,H,2,{byteCount},1^FD{hexPayload}^FS\n";
    }

    private bool ShouldTreatBarcodeAsHex(string value)
    {
        string trimmed = value.Trim();

        // Check only if all characters are valid hex digits (odd length allowed — NormalizeHexPayload pads it)
        string checkStr = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(2)
            : trimmed;

        if (string.IsNullOrEmpty(checkStr) || !checkStr.All(Uri.IsHexDigit))
            return false;

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(RfidHexInputMode, "Always", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(RfidHexInputMode, "Never", StringComparison.OrdinalIgnoreCase))
            return false;

        return false;
    }

    private string BuildTrailingEpcClearCommand(int writtenWordCount)
    {
        if (!RfidClearTrailingEpcWords || RfidTotalEpcWordsToClear <= writtenWordCount)
            return string.Empty;

        int trailingByteCount = (RfidTotalEpcWordsToClear - writtenWordCount) * 2;
        int trailingStartByte = 2 + (writtenWordCount * 2);
        string zeroPayload = new string('0', trailingByteCount * 2);

        return $"^RFW,H,{trailingStartByte},{trailingByteCount},1^FD{zeroPayload}^FS\n";
    }

    private static string ConvertAsciiToHex(string value) // pure utility, stays static
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        return BitConverter.ToString(bytes).Replace("-", "");
    }

    private static bool IsHexString(string value) // pure utility, stays static
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }

        if (hex.Length == 0 || (hex.Length % 2) != 0)
            return false;

        foreach (char ch in hex)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        return true;
    }

    private static string NormalizeHexPayload(string hexValue, int configuredWordCount, out int wordCount) // pure utility, stays static
    {
        string hex = hexValue.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }

        if ((hex.Length % 2) != 0)
        {
            hex = "0" + hex;
        }

        int computedWordCount = (hex.Length + 3) / 4;

        if (configuredWordCount > 0)
        {
            int targetHexLength = configuredWordCount * 4;
            if (hex.Length < targetHexLength)
            {
                hex = hex.PadRight(targetHexLength, '0');
                wordCount = configuredWordCount;
            }
            else if (hex.Length == targetHexLength)
            {
                wordCount = configuredWordCount;
            }
            else
            {
                // Truncate to configured size to avoid RFID encode failure / VOID on tags
                hex = hex.Substring(0, targetHexLength);
                wordCount = configuredWordCount;
            }
        }
        else
        {
            wordCount = computedWordCount;
        }

        int paddedLength = wordCount * 4;
        if (hex.Length < paddedLength)
        {
            hex = hex.PadRight(paddedLength, '0');
        }

        return hex.ToUpperInvariant();
    }

    private static bool IsPdfBase64(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        string trimmed = content.Trim();
        if (trimmed.StartsWith("JVBER", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.StartsWith("data:application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        int commaIdx = trimmed.IndexOf(',');
        if (commaIdx >= 0 && commaIdx < 100)
        {
            string payload = trimmed.Substring(commaIdx + 1).TrimStart();
            if (payload.StartsWith("JVBER", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsPdfUrl(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (!Uri.TryCreate(content.Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private string ConvertPdfToZplGraphics(string base64Pdf)
    {
        byte[] pdfBytes = DecodePdfBase64ToBytes(base64Pdf);
        return ConvertPdfBytesToZplGraphics(pdfBytes, out _);
    }

    private string ConvertPdfBytesToZplGraphics(byte[] pdfBytes, out int labelTopShift)
    {
        using var stream = new MemoryStream(pdfBytes);

        var options = new RenderOptions
        {
            Dpi = PdfRenderDpi,
            BackgroundColor = SKColors.White
        };

        var skBitmaps = Conversion.ToImages(stream, options: options);

        foreach (var skBitmap in skBitmaps)
        {
            try
            {
                _logger.LogDebug("Rendered PDF Page: {W}x{H} pixels", skBitmap.Width, skBitmap.Height);

                int origHeight = skBitmap.Height;
                int cropTop = 0, cropBottom = origHeight, cropLeft = 0, cropRight = skBitmap.Width;

                // 1. Auto-trim whitespace around content so design starts right at the edge (no dead padding)
                using var prepared = AutoTrimWhitespace
                    ? TrimWhitespaceBorders(skBitmap, out cropTop, out cropBottom, out cropLeft, out cropRight)
                    : skBitmap.Copy();

                float topRatio = (float)cropTop / origHeight;
                // Single-sided label detection based on configured threshold:
                bool isSingleSideLower = topRatio >= (SingleSideThresholdPercent / 100.0f);

                // 2. Determine printable bounds based on PrintOffsetX and PrintOffsetY
                int targetX = Math.Max(0, PrintOffsetX);
                int targetY = PrintOffsetY;
                labelTopShift = LabelTopShiftDots;

                // If user sets a negative offset (moving label higher than 0),
                // instead of cutting off top pixels, we shift Zebra's physical feed via native ^LT!
                if (targetY < 0)
                {
                    labelTopShift += targetY;
                    targetY = 0; // Content remains 100% complete at top (zero pixels cut off!)
                }

                labelTopShift = Math.Clamp(labelTopShift, -120, 120);

                // Flap reference area for jewelry tags (Right flap is right 50% of the label)
                int defaultFlapOffsetX = LabelWidthDots / 2;
                int referenceFlapWidth = PrintAreaWidthDots > 0 ? PrintAreaWidthDots : Math.Max(50, LabelWidthDots - defaultFlapOffsetX);
                int referenceFlapHeight = PrintAreaHeightDots > 0 ? PrintAreaHeightDots : LabelLengthDots;

                int availableWidth = Math.Max(50, referenceFlapWidth);
                int availableHeight = Math.Max(50, referenceFlapHeight);

                // 3. Scale proportionally using PdfSizePercent, guaranteed never to cut off on right or bottom
                using var resized = ResizeForLabel(prepared, availableWidth, availableHeight);

                // Base reference top for 4-line reverse wing template based on configured percentage
                int baseTop = (int)Math.Round(origHeight * (TemplateBaseTopPercent / 100.0f));
                float scaleH = (float)resized.Height / Math.Max(1, prepared.Height);
                int verticalShift = Math.Max(0, (int)Math.Round((cropTop - baseTop) * scaleH));

                // Automatic vertical handling for Single-Sided vs Double-Sided labels:
                if (SingleSidePrintOnLowerFlap && isSingleSideLower)
                {
                    if (SingleSideOffsetY > 0)
                    {
                        targetY = SingleSideOffsetY;
                    }
                    else
                    {
                        targetY = PrintOffsetY + verticalShift;

                        // Ensure content cleanly lands on lower flap (lower 50% of label)
                        int lowerFlapStart = LabelLengthDots / 2;
                        int lowerFlapHeight = LabelLengthDots - lowerFlapStart;
                        if (targetY < lowerFlapStart)
                        {
                            targetY = lowerFlapStart + Math.Max(0, (lowerFlapHeight - resized.Height) / 2);
                        }
                    }

                    _logger.LogInformation("Single-sided label detected (ink in lower flap). Placing at Lower Flap Y={Y}", targetY);
                }
                else
                {
                    // Double-Sided label (3-line or 4-line reverse wing):
                    // Anchor to template coordinates so 3-line and 4-line fold margins never slide!
                    targetY = PrintOffsetY + verticalShift;

                    if (AutoCenterOnFlap)
                    {
                        targetY = Math.Max(0, (referenceFlapHeight - resized.Height) / 2);
                    }
                }

                // If AutoCenterOnFlap is enabled, auto-calculate horizontal center
                if (AutoCenterOnFlap)
                {
                    targetX = defaultFlapOffsetX + Math.Max(0, (referenceFlapWidth - resized.Width) / 2);
                }

                // Hard boundary safety clamp: ensure graphic never spills over right or bottom physical edges
                if (targetX + resized.Width > LabelWidthDots)
                {
                    targetX = Math.Max(0, LabelWidthDots - resized.Width);
                }

                if (targetY + resized.Height > LabelLengthDots)
                {
                    targetY = Math.Max(0, LabelLengthDots - resized.Height);
                }

                _logger.LogInformation("Placing graphic: X={X}, Y={Y}, Size={W}x{H}, LabelTopShift={LT}, IsSingleSideLower={SingleSide}",
                    targetX, targetY, resized.Width, resized.Height, labelTopShift, isSingleSideLower);

                // 4. Send graphic starting directly at ^FO{targetX},{targetY}
                return ConvertSkiaBitmapToZplGraphics(resized, targetX, targetY);
            }
            finally
            {
                skBitmap.Dispose();
            }
        }

        throw new Exception("Could not render PDF to image.");
    }

    private string ConvertSkiaBitmapToZplGraphics(SKBitmap bitmap, int xOffsetDots = 0, int yOffsetDots = 0)
    {
        string primary = BuildZplGraphics(bitmap, xOffsetDots, yOffsetDots, BlackThreshold, AlphaThreshold, out bool hasInkPrimary);
        if (hasInkPrimary)
            return primary;

        // Fallback for very light/thin source PDFs where strict thresholds produce blank output.
        int relaxedBlack = Math.Max(BlackThreshold, 245);
        int relaxedAlpha = Math.Min(AlphaThreshold, 5);
        string fallback = BuildZplGraphics(bitmap, xOffsetDots, yOffsetDots, relaxedBlack, relaxedAlpha, out bool hasInkFallback);
        if (hasInkFallback)
        {
            _logger.LogWarning("Primary thresholding produced blank label. Fallback thresholds used (Black={Black}, Alpha={Alpha}).", relaxedBlack, relaxedAlpha);
            return fallback;
        }

        throw new Exception("Rendered label appears blank after thresholding. Check source PDF and thresholds.");
    }

    private string BuildZplGraphics(SKBitmap bitmap, int xOffsetDots, int yOffsetDots, int blackThreshold, int alphaThreshold, out bool hasInk)
    {
        var sb = new StringBuilder();
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = (width + 7) / 8;
        int totalBytes = rowBytes * height;
        const int MaxHexCharsPerLine = 128; // Keep lines short to avoid parser issues
        int hexCharsOnLine = 0;
        int nonZeroBytes = 0;

        sb.Append($"^FO{xOffsetDots},{yOffsetDots}^GFA,{totalBytes},{totalBytes},{rowBytes},");

        unsafe
        {
            byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
            int bytesPerPixel = bitmap.BytesPerPixel;
            int stride = bitmap.RowBytes; // Correct memory pitch/stride

            for (int y = 0; y < height; y++)
            {
                byte* row = ptr + (y * stride);
                for (int x = 0; x < rowBytes; x++)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int px = x * 8 + bit;
                        if (px < width)
                        {
                            byte* pixel = row + (px * bytesPerPixel);
                            byte blue = pixel[0];
                            byte green = pixel[1];
                            byte red = pixel[2];
                            byte alpha = pixel[3];

                            int gray = (red + green + blue) / 3;

                            if (alpha > alphaThreshold && gray < blackThreshold)
                                b |= (byte)(1 << (7 - bit));
                        }
                    }
                    if (b != 0)
                        nonZeroBytes++;

                    sb.Append(b.ToString("X2"));
                    hexCharsOnLine += 2;
                    if (hexCharsOnLine >= MaxHexCharsPerLine)
                    {
                        sb.Append("\n");
                        hexCharsOnLine = 0;
                    }
                }
            }
        }
        if (hexCharsOnLine > 0)
        {
            sb.Append("\n");
        }
        sb.Append("^FS\n");
        hasInk = nonZeroBytes > 0;
        return sb.ToString();
    }



    private SKBitmap ResizeForLabel(SKBitmap source, int maxAvailableWidth, int maxAvailableHeight)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return source.Copy();

        // Scale proportionally within both width AND height boundaries defined by PdfSizePercent
        float scaleFactor = Math.Clamp(PdfSizePercent, 10, 200) / 100.0f;
        int targetMaxWidth = Math.Max(1, (int)Math.Floor(maxAvailableWidth * scaleFactor));
        int targetMaxHeight = Math.Max(1, (int)Math.Floor(maxAvailableHeight * scaleFactor));

        // Uniform proportional scale (fit inside target box maintaining exact aspect ratio)
        float scaleW = (float)targetMaxWidth / source.Width;
        float scaleH = (float)targetMaxHeight / source.Height;
        float scale = Math.Min(scaleW, scaleH);

        int targetWidth = Math.Clamp((int)Math.Floor(source.Width * scale), 1, maxAvailableWidth);
        int targetHeight = Math.Clamp((int)Math.Floor(source.Height * scale), 1, maxAvailableHeight);

        var resized = new SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType);
        bool scaled = source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        if (!scaled)
        {
            using var canvas = new SKCanvas(resized);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(0, 0, targetWidth, targetHeight));
        }

        return resized;
    }

    private static int MmToDotsAtDpi(int mm, int dpi)
    {
        if (mm <= 0)
            return 0;

        int safeDpi = Math.Max(1, dpi);
        return Math.Max(1, (int)Math.Round(mm * safeDpi / 25.4));
    }

    private string ApplyLabelOffsets(string zpl)
    {
        if (PrintOffsetX == 0 && PrintOffsetY == 0)
            return zpl;

        var adjusted = zpl;
        adjusted = AdjustZplCommandCoordinates(adjusted, "FO");
        adjusted = AdjustZplCommandCoordinates(adjusted, "FT");
        return adjusted;
    }

    private string AdjustZplCommandCoordinates(string zpl, string command)
    {
        var pattern = $@"\^{command}(-?\d+),(-?\d+)(?:,([0-2]))?";
        int replacements = 0;

        var adjusted = Regex.Replace(zpl, pattern, match =>
        {
            int x = Math.Max(0, int.Parse(match.Groups[1].Value) + PrintOffsetX);
            int y = Math.Max(0, int.Parse(match.Groups[2].Value) + PrintOffsetY);
            string extra = match.Groups[3].Success ? $",{match.Groups[3].Value}" : "";

            replacements++;
            return $"^{command}{x},{y}{extra}";
        });

        if (replacements > 0)
        {
            _logger.LogDebug("Applied label offset to {Count} ^{Command} fields (X={X}, Y={Y})",
                replacements, command, PrintOffsetX, PrintOffsetY);
        }

        return adjusted;
    }

    private SKBitmap TrimWhitespaceBorders(SKBitmap source)
    {
        return TrimWhitespaceBorders(source, out _, out _, out _, out _);
    }

    private SKBitmap TrimWhitespaceBorders(SKBitmap source, out int cropTop, out int cropBottom, out int cropLeft, out int cropRight)
    {
        cropTop = 0;
        cropBottom = source?.Height ?? 0;
        cropLeft = 0;
        cropRight = source?.Width ?? 0;

        if (source == null || source.Width <= 0 || source.Height <= 0)
            return source?.Copy() ?? new SKBitmap();

        unsafe
        {
            byte* ptr = (byte*)source.GetPixels().ToPointer();
            int bytesPerPixel = source.BytesPerPixel;
            int stride = source.RowBytes; // Correct memory pitch/stride

            int left = source.Width;
            int right = -1;
            int top = source.Height;
            int bottom = -1;

            for (int y = 0; y < source.Height; y++)
            {
                byte* row = ptr + (y * stride);
                for (int x = 0; x < source.Width; x++)
                {
                    byte* pixel = row + (x * bytesPerPixel);
                    byte blue = pixel[0];
                    byte green = pixel[1];
                    byte red = pixel[2];
                    byte alpha = pixel[3];

                    int gray = (red + green + blue) / 3;
                    bool isInk = alpha > AlphaThreshold && gray < BlackThreshold;

                    if (!isInk)
                        continue;

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            if (right < left || bottom < top)
                return source.Copy();

            int paddingX = TrimPaddingDots;
            int paddingY = TrimPaddingDots;

            cropLeft = Math.Max(0, left - paddingX);
            cropTop = Math.Max(0, top - paddingY);
            cropRight = Math.Min(source.Width, right + paddingX + 1);
            cropBottom = Math.Min(source.Height, bottom + paddingY + 1);

            int cropWidth = Math.Max(1, cropRight - cropLeft);
            int cropHeight = Math.Max(1, cropBottom - cropTop);

            if (cropWidth == source.Width && cropHeight == source.Height && cropLeft == 0 && cropTop == 0)
                return source.Copy();

            var cropped = new SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(cropped);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                source,
                new SKRectI(cropLeft, cropTop, cropRight, cropBottom),
                new SKRectI(0, 0, cropWidth, cropHeight));

            return cropped;
        }
    }

    private string BuildSimpleBarcodeLabel(string barcode, string rfidCmd)
    {
        int fontSize = PrinterDpi >= 300 ? 40 : 30;
        int barcodeHeight = PrinterDpi >= 300 ? 160 : 120;
        int x = PrintOffsetX > 0 ? PrintOffsetX : 40;
        return $"^XA\n^PQ1\n^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}^CF0,{fontSize}\n^FO{x},40^FD{barcode}^FS\n^BY3,2,{barcodeHeight}\n^FO{x},120^BCN,{barcodeHeight},Y,N,N\n^FD{barcode}^FS\n^XZ";
    }

    private async Task SendToPrinterAsync(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            _logger.LogWarning("SendToPrinterAsync called with null or empty printer data. Skipping send.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PrinterIp))
            throw new InvalidOperationException("PrinterIp is not configured in appsettings.json.");

        const int maxAttempts = 3;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var client = new TcpClient
                {
                    NoDelay = true,
                    SendTimeout = 10000,
                    ReceiveTimeout = 10000
                };

                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(PrinterIp, PrinterPort, connectCts.Token);

                using var stream = client.GetStream();
                await stream.WriteAsync(data, 0, data.Length);
                await stream.FlushAsync();

                _logger.LogInformation("Printer send success on attempt {Attempt}, bytes={ByteCount}", attempt, data.Length);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Printer send failed on attempt {Attempt}/{MaxAttempts}", attempt, maxAttempts);

                if (attempt < maxAttempts)
                    await Task.Delay(200);
            }
        }

        throw new Exception("Failed to send ZPL to printer after retries.", lastError);
    }

    private async Task UpdateStatusAsync(int jobId, string status, string? error)
    {
        try
        {
            var response = await _httpClient.PatchAsJsonAsync($"{CloudApiUrl}/{jobId}/status", new { Status = status, ErrorMessage = error });
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Status update for Job {Id} to {Status} failed. API returned HTTP {StatusCode}", jobId, status, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update status for Job {Id} to {Status}: {Message}", jobId, status, ex.Message);
        }
    }
}

public class PrintJobDto
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public string LabelZpl { get; set; } = "";
}
