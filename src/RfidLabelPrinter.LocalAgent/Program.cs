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
                logging.AddEventLog();
            })
            .Build();

        await host.RunAsync();
    }
}

public class PrinterWorker : BackgroundService
{
    private const int DefaultLabelWidthDots = 600;
    private const int DefaultLabelLengthDots = 400;
    private const int DefaultEpcWordCount = 6;

    private readonly ILogger<PrinterWorker> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;

    private string CloudApiUrl = "";
    private string PrinterIp = "";
    private int PrinterPort = 9100;
    private int PollingIntervalMs = 3000;
    private int ClaimBatchSize = 5;
    private int ClaimMaxLoopsPerCycle = 5;
    private bool EnableRfidWrite = true;
    private int PdfRenderDpi = 300;
    private int PdfDownloadRetryCount = 3;
    private int PdfDownloadRetryDelayMs = 500;
    private int PrinterDpi = 203;
    private int LabelWidthMm = 0;
    private int LabelHeightMm = 0;
    private int BlackThreshold = 230;
    private int AlphaThreshold = 20;
    private int LabelWidthDots = DefaultLabelWidthDots;
    private int LabelLengthDots = DefaultLabelLengthDots;
    private bool PrintPdfAsGraphics = true;
    private string RfidMode = "H";
    private int RfidBlockNumber = 1;
    private int RfidStartWord = 2;
    private int RfidWordCount = 12;
    private bool RfidClearTrailingEpcWords = false;
    private int RfidTotalEpcWordsToClear = DefaultEpcWordCount;
    private bool UseEpcBankWriteFormat = true;
    private string RfidHexInputMode = "Never";
    private bool DumpZplToFile = false;
    private string DumpZplPath = "";
    private int InterJobDelayMs = 800;
    private int PrintRegionOffsetMm = 0;
    private int PrintRegionWidthMm = 0;
    private int LabelShiftXDots = 0;
    private int LabelShiftYDots = 0;
    private int PdfScalePercent = 100;
    private int PdfBaseXDots = 0;
    private int PdfBaseYDots = 40;
    private int PdfOffsetXDots = 0;
    private int PdfOffsetYDots = 0;
    private int PrintSafeLeftMm = 0;
    private int PrintSafeRightMm = 0;
    private int PrintSafeTopMm = 0;
    private int PrintSafeBottomMm = 0;
    private bool TrimPdfWhitespace = false;
    private string PdfFitReference = "Page";
    private bool ClampToSafeArea = false;
    private bool PreferPdfTextToZpl = true;

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
        _logger.LogInformation("PDF->Graphics: {Status}", PrintPdfAsGraphics ? "Enabled" : "Disabled");
        _logger.LogInformation("PDF->Text ZPL: {Status}", PreferPdfTextToZpl ? "Enabled" : "Disabled");
        _logger.LogInformation("Render DPI: {Dpi}, BlackThreshold: {Black}, AlphaThreshold: {Alpha}", PdfRenderDpi, BlackThreshold, AlphaThreshold);
        _logger.LogInformation("PDF Download Retry: Count={Count}, DelayMs={Delay}", PdfDownloadRetryCount, PdfDownloadRetryDelayMs);
        _logger.LogInformation("Printer DPI: {Dpi}", PrinterDpi);
        _logger.LogInformation("Label Size: {W}x{H}", LabelWidthDots, LabelLengthDots);
        _logger.LogInformation("PDF Print Region: Offset={Offset}mm, Width={Width}mm (0 width = full label)", PrintRegionOffsetMm, PrintRegionWidthMm);
        _logger.LogInformation("Label Shift: X={X} dots, Y={Y} dots", LabelShiftXDots, LabelShiftYDots);
        _logger.LogInformation("PDF Controls: Scale={Scale}%, BaseX={BaseX}, BaseY={BaseY}, X={X}, Y={Y}",
            PdfScalePercent, PdfBaseXDots, PdfBaseYDots, PdfOffsetXDots, PdfOffsetYDots);
        _logger.LogInformation("Print Safe Margins(mm): L={L}, R={R}, T={T}, B={B}", PrintSafeLeftMm, PrintSafeRightMm, PrintSafeTopMm, PrintSafeBottomMm);
        _logger.LogInformation("Trim PDF whitespace: {Enabled}", TrimPdfWhitespace ? "Enabled" : "Disabled");
        _logger.LogInformation("PDF Fit Reference: {Mode}, ClampToSafeArea: {Clamp}", PdfFitReference, ClampToSafeArea ? "Enabled" : "Disabled");
        _logger.LogInformation("RFID Params: ^RFW,{Mode},{Block},{Start},{Words}", RfidMode, RfidBlockNumber, RfidStartWord, RfidWordCount);
        _logger.LogInformation("EPC Bank Format: {Status}", UseEpcBankWriteFormat ? "Enabled" : "Disabled");
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

    private void LoadConfig()
    {
        CloudApiUrl = _config["AgentSettings:CloudApiUrl"] ?? "";
        PrinterIp = _config["AgentSettings:PrinterIp"] ?? "";
        PrinterPort = int.Parse(_config["AgentSettings:PrinterPort"] ?? "9100");
        PollingIntervalMs = int.Parse(_config["AgentSettings:PollingIntervalMs"] ?? "3000");
        ClaimBatchSize = int.Parse(_config["AgentSettings:ClaimBatchSize"] ?? "5");
        ClaimMaxLoopsPerCycle = int.Parse(_config["AgentSettings:ClaimMaxLoopsPerCycle"] ?? "5");
        EnableRfidWrite = bool.TryParse(_config["AgentSettings:EnableRfidWrite"], out var enableRfid) ? enableRfid : true;
        PdfRenderDpi = int.Parse(_config["AgentSettings:PdfRenderDpi"] ?? "300");
        PdfDownloadRetryCount = Math.Max(1, int.Parse(_config["AgentSettings:PdfDownloadRetryCount"] ?? "3"));
        PdfDownloadRetryDelayMs = Math.Max(0, int.Parse(_config["AgentSettings:PdfDownloadRetryDelayMs"] ?? "500"));
        PrinterDpi = int.Parse(_config["AgentSettings:PrinterDpi"] ?? PdfRenderDpi.ToString());
        LabelWidthMm = int.Parse(_config["AgentSettings:LabelWidthMm"] ?? "0");
        LabelHeightMm = int.Parse(_config["AgentSettings:LabelHeightMm"] ?? "0");
        BlackThreshold = int.Parse(_config["AgentSettings:BlackThreshold"] ?? "230");
        AlphaThreshold = int.Parse(_config["AgentSettings:AlphaThreshold"] ?? "20");
        LabelWidthDots = int.Parse(_config["AgentSettings:LabelWidthDots"] ?? DefaultLabelWidthDots.ToString());
        LabelLengthDots = int.Parse(_config["AgentSettings:LabelLengthDots"] ?? DefaultLabelLengthDots.ToString());

        // If physical label size is configured, derive dots from mm for printer portability.
        if (LabelWidthMm > 0)
            LabelWidthDots = MmToDotsAtDpi(LabelWidthMm, PrinterDpi);
        if (LabelHeightMm > 0)
            LabelLengthDots = MmToDotsAtDpi(LabelHeightMm, PrinterDpi);
        PrintPdfAsGraphics = bool.TryParse(_config["AgentSettings:PrintPdfAsGraphics"], out var printPdf) ? printPdf : true;
        RfidMode = (_config["AgentSettings:RfidMode"] ?? "H").Trim().ToUpperInvariant();
        RfidBlockNumber = int.Parse(_config["AgentSettings:RfidBlockNumber"] ?? "1");
        RfidStartWord = int.Parse(_config["AgentSettings:RfidStartWord"] ?? "2");
        RfidWordCount = int.Parse(_config["AgentSettings:RfidWordCount"] ?? "12");
        RfidClearTrailingEpcWords = bool.TryParse(_config["AgentSettings:RfidClearTrailingEpcWords"], out var clearEpc) ? clearEpc : false;
        RfidTotalEpcWordsToClear = int.Parse(_config["AgentSettings:RfidTotalEpcWordsToClear"] ?? DefaultEpcWordCount.ToString());
        UseEpcBankWriteFormat = bool.TryParse(_config["AgentSettings:UseEpcBankWriteFormat"], out var useEpcBank) ? useEpcBank : true;
        RfidHexInputMode = (_config["AgentSettings:RfidHexInputMode"] ?? "Never").Trim();
        DumpZplToFile = bool.TryParse(_config["AgentSettings:DumpZplToFile"], out var dumpZpl) ? dumpZpl : false;
        DumpZplPath = _config["AgentSettings:DumpZplPath"] ?? "";
        InterJobDelayMs = int.Parse(_config["AgentSettings:InterJobDelayMs"] ?? "800");
        PrintRegionOffsetMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintRegionOffsetMm"] ?? "0"));
        PrintRegionWidthMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintRegionWidthMm"] ?? "0"));
        LabelShiftXDots = int.Parse(_config["AgentSettings:LabelShiftXDots"] ?? "0");
        LabelShiftYDots = int.Parse(_config["AgentSettings:LabelShiftYDots"] ?? "0");
        PdfScalePercent = Math.Clamp(
            int.Parse(_config["AgentSettings:PdfSizePercent"] ?? _config["AgentSettings:PdfScalePercent"] ?? "100"),
            30,
            200);
        PdfBaseXDots = int.Parse(_config["AgentSettings:PdfBaseXDots"] ?? "0");
        PdfBaseYDots = int.Parse(_config["AgentSettings:PdfBaseYDots"] ?? "40");
        PdfOffsetXDots = int.Parse(_config["AgentSettings:PdfLeftRightDots"] ?? _config["AgentSettings:PdfOffsetXDots"] ?? "0");
        PdfOffsetYDots = int.Parse(_config["AgentSettings:PdfUpDownDots"] ?? _config["AgentSettings:PdfOffsetYDots"] ?? "0");
        PrintSafeLeftMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintSafeLeftMm"] ?? "0"));
        PrintSafeRightMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintSafeRightMm"] ?? "0"));
        PrintSafeTopMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintSafeTopMm"] ?? "0"));
        PrintSafeBottomMm = Math.Max(0, int.Parse(_config["AgentSettings:PrintSafeBottomMm"] ?? "0"));
        TrimPdfWhitespace = bool.TryParse(_config["AgentSettings:TrimPdfWhitespace"], out var trimWhitespace) ? trimWhitespace : false;
        PdfFitReference = (_config["AgentSettings:PdfFitReference"] ?? "Page").Trim();
        ClampToSafeArea = bool.TryParse(_config["AgentSettings:ClampToSafeArea"], out var clampToSafe) ? clampToSafe : false;
        PreferPdfTextToZpl = bool.TryParse(_config["AgentSettings:PreferPdfTextToZpl"], out var preferText) ? preferText : true;
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        int loopCount = 0;
        while (loopCount < ClaimMaxLoopsPerCycle)
        {
            var response = await _httpClient.PostAsync($"{CloudApiUrl}/claim?batchSize={ClaimBatchSize}", null, stoppingToken);
            if (!response.IsSuccessStatusCode) return;

            var jobs = await response.Content.ReadFromJsonAsync<List<PrintJobDto>>(stoppingToken);
            if (jobs == null || jobs.Count == 0) return;

            _logger.LogInformation("Claimed {Count} jobs.", jobs.Count);

            for (int i = 0; i < jobs.Count; i++)
            {
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

                if (i < jobs.Count - 1 && InterJobDelayMs > 0)
                    await Task.Delay(InterJobDelayMs, stoppingToken);
            }

            loopCount++;
        }
    }

    private async Task<byte[]> PreparePrinterDataAsync(PrintJobDto job)
    {
        string rfidCmd = GetRfidZpl(job.Barcode);

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
            _logger.LogInformation("Job {Id}: PDF URL detected. Downloading...", job.Id);
            var pdfBytes = await DownloadPdfBytesAsync(job.LabelZpl);
            string zplPayload = BuildPdfJobZpl(job, pdfBytes, rfidCmd);
            WriteZplDump(job, zplPayload);
            return Encoding.ASCII.GetBytes(zplPayload);
        }

        if (IsPdfBase64(job.LabelZpl))
        {
            _logger.LogInformation("Job {Id}: PDF Base64 detected. Converting...", job.Id);
            var pdfBytes = DecodePdfBase64ToBytes(job.LabelZpl);
            string zplPayload = BuildPdfJobZpl(job, pdfBytes, rfidCmd);
            WriteZplDump(job, zplPayload);
            return Encoding.ASCII.GetBytes(zplPayload);
        }

        string zplFinal = job.LabelZpl.Contains("^XA") 
            ? ApplyLabelOffsets(job.LabelZpl.Replace("^XA", $"^XA\n^PQ1\n{rfidCmd}")) 
            : $"^XA\n^PQ1\n{rfidCmd}{ApplyLabelOffsets(job.LabelZpl)}\n^XZ";

        WriteZplDump(job, zplFinal);
        return Encoding.ASCII.GetBytes(zplFinal);
    }

    private string BuildPdfJobZpl(PrintJobDto job, byte[] pdfBytes, string rfidCmd)
    {
        if (!PreferPdfTextToZpl && !PrintPdfAsGraphics)
            throw new Exception("Both PDF rendering modes are disabled. Enable PreferPdfTextToZpl or PrintPdfAsGraphics.");

        if (PreferPdfTextToZpl)
        {
            try
            {
                string textZplBody = ConvertPdfBytesToUniversalTextZpl(pdfBytes, job.Barcode);
                _logger.LogInformation("Job {Id}: Generated universal text-based ZPL from PDF.", job.Id);
                return $"^XA\n^PQ1\n^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}{textZplBody}\n^XZ";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job {Id}: PDF text extraction failed, falling back to graphics mode.", job.Id);

                if (!PrintPdfAsGraphics)
                    throw;
            }
        }

        string graphicsZpl = ConvertPdfBytesToZplGraphics(pdfBytes);
        _logger.LogInformation("Job {Id}: Generated graphics-based ZPL from PDF.", job.Id);
        return $"^XA\n^PQ1\n^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}{graphicsZpl}\n^XZ";
    }

    private async Task<byte[]> DownloadPdfBytesAsync(string pdfUrl)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= PdfDownloadRetryCount; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(pdfUrl);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new Exception($"PDF URL returned 404 (Not Found): {pdfUrl}");

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "PDF download failed on attempt {Attempt}/{Max}: {Url}", attempt, PdfDownloadRetryCount, pdfUrl);

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

        int left = 24 + LabelShiftXDots;
        int top = 24 + LabelShiftYDots;
        int lineHeight = 30;
        int maxTextHeight = Math.Max(80, LabelLengthDots - 150);
        int maxLines = Math.Max(1, maxTextHeight / lineHeight);

        sb.Append("^CF0,28\n");

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
            int barcodeY = Math.Max(top + (printed * lineHeight) + 8, LabelLengthDots - 120 + LabelShiftYDots);
            sb.Append("^BY2,2,80\n");
            sb.Append($"^FO{left},{barcodeY}^BCN,80,Y,N,N^FD{barcodeData}^FS\n");
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

        string targetPath = DumpZplPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zpl-dump.txt");
        }

        string header = $"\n----- {DateTime.Now:yyyy-MM-dd HH:mm:ss} JobId={job.Id} Barcode={job.Barcode} -----\n";
        File.AppendAllText(targetPath, header + zpl + "\n");
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

    private static bool IsPdfBase64(string content) // pure utility, stays static
    {
        return content.Trim().StartsWith("JVBER");
    }

    private static bool IsPdfUrl(string content) // pure utility, stays static
    {
        if (!Uri.TryCreate(content?.Trim(), UriKind.Absolute, out var uri))
            return false;

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               uri.AbsoluteUri.Contains(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private string ConvertPdfToZplGraphics(string base64Pdf)
    {
        byte[] pdfBytes = DecodePdfBase64ToBytes(base64Pdf);
        return ConvertPdfBytesToZplGraphics(pdfBytes);
    }

    private string ConvertPdfBytesToZplGraphics(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        
        // Force White background to avoid black labels
        var options = new RenderOptions { 
            Dpi = PdfRenderDpi, 
            BackgroundColor = SKColors.White 
        };
        
        var skBitmaps = Conversion.ToImages(stream, options: options);
        var (regionOffsetDots, regionWidthDots) = GetPdfPrintRegionDots();
        bool cropEnabled = regionWidthDots > 0;
        
        foreach (var skBitmap in skBitmaps)
        {
            _logger.LogDebug("Rendered Image: {W}x{H} pixels", skBitmap.Width, skBitmap.Height);
            bool useContentReference = string.Equals(PdfFitReference, "Content", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(PdfFitReference, "Auto", StringComparison.OrdinalIgnoreCase) && TrimPdfWhitespace);
            using var prepared = useContentReference ? TrimWhitespaceBorders(skBitmap) : skBitmap.Copy();
            
            if (cropEnabled)
            {
                // Crop mode: extract region, resize to region width, position at offset
                using var cropped = CropToConfiguredPdfRegion(prepared, regionOffsetDots, regionWidthDots);
                using var resized = ResizeForLabel(cropped, regionWidthDots);
                int x = regionOffsetDots + LabelShiftXDots + PdfBaseXDots + PdfOffsetXDots;
                int y = LabelShiftYDots + PdfBaseYDots + PdfOffsetYDots;
                using var positioned = PositionBitmapOnLabelCanvas(resized, x, y);
                return ConvertSkiaBitmapToZplGraphics(positioned, 0, 0);
            }
            else
            {
                // Full PDF mode: trim empty margins first, then fit to the label width.
                using var resized = ResizeForLabel(prepared);
                int x = LabelShiftXDots + PdfBaseXDots + PdfOffsetXDots;
                int y = LabelShiftYDots + PdfBaseYDots + PdfOffsetYDots;
                using var positioned = PositionBitmapOnLabelCanvas(resized, x, y);
                return ConvertSkiaBitmapToZplGraphics(positioned, 0, 0);
            }
        }

        throw new Exception("Could not render PDF to image.");
    }

    private SKBitmap PositionBitmapOnLabelCanvas(SKBitmap content, int xDots, int yDots)
    {
        int canvasW = Math.Max(1, LabelWidthDots);
        int canvasH = Math.Max(1, LabelLengthDots);

        int drawX;
        int drawY;

        if (!ClampToSafeArea)
        {
            drawX = xDots;
            drawY = yDots;
        }
        else
        {
            int safeLeft = MmToDotsAtDpi(PrintSafeLeftMm, PrinterDpi);
            int safeRight = MmToDotsAtDpi(PrintSafeRightMm, PrinterDpi);
            int safeTop = MmToDotsAtDpi(PrintSafeTopMm, PrinterDpi);
            int safeBottom = MmToDotsAtDpi(PrintSafeBottomMm, PrinterDpi);

            int minX = safeLeft;
            int maxX = Math.Max(minX, canvasW - safeRight - content.Width);
            int minY = safeTop;
            int maxY = Math.Max(minY, canvasH - safeBottom - content.Height);

            drawX = Math.Clamp(xDots, minX, maxX);
            drawY = Math.Clamp(yDots, minY, maxY);
        }

        var canvasBitmap = new SKBitmap(canvasW, canvasH, content.ColorType, content.AlphaType);
        using var canvas = new SKCanvas(canvasBitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(content, drawX, drawY);

        return canvasBitmap;
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

        // Fast bit manipulation
        unsafe
        {
            byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
            int bytesPerPixel = bitmap.BytesPerPixel;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < rowBytes; x++)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int px = x * 8 + bit;
                        if (px < width)
                        {
                            int offset = (y * width + px) * bytesPerPixel;
                            
                            // Get Colors (Order is usually B-G-R-A)
                            byte blue = ptr[offset + 0];
                            byte green = ptr[offset + 1];
                            byte red = ptr[offset + 2];
                            byte alpha = ptr[offset + 3];

                            // Simple Grayscale: If it's dark and not transparent, it's black
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
        sb.Append("^FS\n");
        hasInk = nonZeroBytes > 0;
        return sb.ToString();
    }

    private (int OffsetDots, int WidthDots) GetPdfPrintRegionDots()
    {
        int offsetDots = MmToDots(PrintRegionOffsetMm);
        int widthDots = MmToDots(PrintRegionWidthMm);

        if (offsetDots >= LabelWidthDots)
        {
            offsetDots = Math.Max(0, LabelWidthDots - 1);
        }

        if (widthDots > 0)
        {
            int maxWidth = Math.Max(1, LabelWidthDots - offsetDots);
            widthDots = Math.Min(widthDots, maxWidth);
        }

        return (offsetDots, widthDots);
    }

    private int MmToDots(int mm)
    {
        return MmToDotsAtDpi(mm, PrinterDpi);
    }

    private static int MmToDotsAtDpi(int mm, int dpi)
    {
        if (mm <= 0)
            return 0;

        int safeDpi = Math.Max(1, dpi);
        return Math.Max(1, (int)Math.Round(mm * safeDpi / 25.4));
    }

    private SKBitmap CropToConfiguredPdfRegion(SKBitmap source, int offsetDots, int widthDots)
    {
        if (widthDots <= 0 || source.Width <= 0 || source.Height <= 0 || LabelWidthDots <= 0)
            return source;

        // Map configured label-region dots to source-image pixels by ratio.
        // This avoids over-cropping when source PDF render width differs from label width.
        float offsetRatio = Math.Clamp((float)offsetDots / LabelWidthDots, 0f, 1f);
        float widthRatio = Math.Clamp((float)widthDots / LabelWidthDots, 0f, 1f - offsetRatio);

        int sourceOffsetX = (int)Math.Round(source.Width * offsetRatio);
        sourceOffsetX = Math.Clamp(sourceOffsetX, 0, Math.Max(0, source.Width - 1));

        int sourceWidth = Math.Max(1, (int)Math.Round(source.Width * widthRatio));
        sourceWidth = Math.Clamp(sourceWidth, 1, source.Width - sourceOffsetX);

        if (sourceOffsetX == 0 && sourceWidth == source.Width)
            return source;

        var cropped = new SKBitmap(sourceWidth, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(
            source,
            new SKRectI(sourceOffsetX, 0, sourceOffsetX + sourceWidth, source.Height),
            new SKRectI(0, 0, sourceWidth, source.Height));

        return cropped;
    }

    private SKBitmap ResizeForLabel(SKBitmap source, int targetWidthOverride = 0)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return source;

        var targetWidth = Math.Max(1, targetWidthOverride > 0 ? targetWidthOverride : LabelWidthDots);
        var scale = (float)targetWidth / source.Width;
        var targetHeight = Math.Max(1, (int)(source.Height * scale));

        if (targetHeight > LabelLengthDots)
        {
            targetHeight = LabelLengthDots;
            scale = (float)targetHeight / source.Height;
            targetWidth = Math.Max(1, (int)(source.Width * scale));
        }

        if (PdfScalePercent != 100)
        {
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * (PdfScalePercent / 100.0)));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * (PdfScalePercent / 100.0)));
        }

        // Never return the original oversized bitmap here; that can overflow to next label.
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

    private string ApplyLabelOffsets(string zpl)
    {
        // Shift positioning commands by configured X/Y offsets.
        if (LabelShiftXDots == 0 && LabelShiftYDots == 0)
            return zpl;

        var adjusted = zpl;

        // ^FO = Field Origin, ^FT = Field Typeset. Both can define print position.
        adjusted = AdjustZplCommandCoordinates(adjusted, "FO");
        adjusted = AdjustZplCommandCoordinates(adjusted, "FT");

        return adjusted;
    }

    private string AdjustZplCommandCoordinates(string zpl, string command)
    {
        var pattern = $@"\^{command}(-?\d+),(-?\d+)";
        int replacements = 0;

        var adjusted = Regex.Replace(zpl, pattern, match =>
        {
            int x = int.Parse(match.Groups[1].Value) + LabelShiftXDots;
            int y = int.Parse(match.Groups[2].Value) + LabelShiftYDots;

            replacements++;
            return $"^{command}{x},{y}";
        });

        if (replacements > 0)
        {
            _logger.LogDebug("Applied label offset to {Count} ^{Command} fields (X={X}, Y={Y})",
                replacements, command, LabelShiftXDots, LabelShiftYDots);
        }

        return adjusted;
    }

    private SKBitmap TrimWhitespaceBorders(SKBitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return source;

        unsafe
        {
            byte* ptr = (byte*)source.GetPixels().ToPointer();
            int bytesPerPixel = source.BytesPerPixel;

            int left = source.Width;
            int right = -1;
            int top = source.Height;
            int bottom = -1;

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    int offset = (y * source.Width + x) * bytesPerPixel;
                    byte blue = ptr[offset + 0];
                    byte green = ptr[offset + 1];
                    byte red = ptr[offset + 2];
                    byte alpha = ptr[offset + 3];

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
                return source;

            int paddingX = Math.Max(4, source.Width / 100);
            int paddingY = Math.Max(4, source.Height / 100);

            int cropLeft = Math.Max(0, left - paddingX);
            int cropTop = Math.Max(0, top - paddingY);
            int cropRight = Math.Min(source.Width, right + paddingX + 1);
            int cropBottom = Math.Min(source.Height, bottom + paddingY + 1);

            int cropWidth = Math.Max(1, cropRight - cropLeft);
            int cropHeight = Math.Max(1, cropBottom - cropTop);

            if (cropWidth == source.Width && cropHeight == source.Height && cropLeft == 0 && cropTop == 0)
                return source;

            var cropped = new SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(cropped);
            canvas.DrawBitmap(
                source,
                new SKRectI(cropLeft, cropTop, cropRight, cropBottom),
                new SKRectI(0, 0, cropWidth, cropHeight));

            return cropped;
        }
    }

    private string BuildSimpleBarcodeLabel(string barcode, string rfidCmd)
    {
        return $"^XA\n^PQ1\n^PW{LabelWidthDots}\n^LL{LabelLengthDots}\n{rfidCmd}^CF0,30\n^FO40,40^FD{barcode}^FS\n^BY3,2,120\n^FO40,100^BCN,120,Y,N,N\n^FD{barcode}^FS\n^XZ";
    }

    private async Task SendToPrinterAsync(byte[] data)
    {
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
        await _httpClient.PatchAsJsonAsync($"{CloudApiUrl}/{jobId}/status", new { Status = status, ErrorMessage = error });
    }
}

public class PrintJobDto
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public string LabelZpl { get; set; } = "";
}
