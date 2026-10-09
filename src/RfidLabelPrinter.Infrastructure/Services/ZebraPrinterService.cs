using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RfidLabelPrinter.Core.Configuration;
using RfidLabelPrinter.Core.Interfaces;

namespace RfidLabelPrinter.Infrastructure.Services;

/// <summary>
/// Communicates with the Zebra ZD621T RFID printer via TCP/IP on port 9100.
/// Sends raw ZPL commands for barcode printing and RFID EPC encoding.
/// 
/// Connection Strategy:
/// - Opens a new TCP connection for each batch send
/// - Implements connection timeout and send timeout
/// - Handles graceful disconnection and error recovery
/// </summary>
public class ZebraPrinterService : IPrinterService
{
    private readonly PrinterSettings _settings;
    private readonly ILogger<ZebraPrinterService> _logger;

    public ZebraPrinterService(
        IOptions<PrinterSettings> settings,
        ILogger<ZebraPrinterService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendZplAsync(string zplData, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(zplData))
        {
            _logger.LogWarning("Attempted to send empty ZPL data");
            return false;
        }

        TcpClient? client = null;
        NetworkStream? stream = null;

        try
        {
            _logger.LogDebug("Connecting to printer at {Ip}:{Port}",
                _settings.PrinterIp, _settings.PrinterPort);

            client = new TcpClient
            {
                SendTimeout = _settings.SendTimeoutMs,
                ReceiveTimeout = _settings.ConnectionTimeoutMs,
                NoDelay = true // Disable Nagle's algorithm for immediate sends
            };

            // Connect with timeout
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_settings.ConnectionTimeoutMs);

            await client.ConnectAsync(
                _settings.PrinterIp,
                _settings.PrinterPort,
                connectCts.Token);

            if (!client.Connected)
            {
                _logger.LogError("Failed to establish TCP connection to printer");
                return false;
            }

            stream = client.GetStream();

            // Convert ZPL to bytes and send
            byte[] data = Encoding.UTF8.GetBytes(zplData);

            _logger.LogDebug("Sending {ByteCount} bytes of ZPL data to printer", data.Length);

            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);

            _logger.LogInformation("Successfully sent {ByteCount} bytes to printer at {Ip}:{Port}",
                data.Length, _settings.PrinterIp, _settings.PrinterPort);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Print operation was cancelled");
            throw;
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Socket error communicating with printer at {Ip}:{Port}. Error: {ErrorCode}",
                _settings.PrinterIp, _settings.PrinterPort, ex.SocketErrorCode);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error sending data to printer at {Ip}:{Port}",
                _settings.PrinterIp, _settings.PrinterPort);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error communicating with printer");
            return false;
        }
        finally
        {
            // Ensure clean disconnection
            if (stream != null)
            {
                try { stream.Close(); } catch { /* ignore cleanup errors */ }
                await stream.DisposeAsync();
            }

            if (client != null)
            {
                try { client.Close(); } catch { /* ignore cleanup errors */ }
                client.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_settings.ConnectionTimeoutMs);

            await client.ConnectAsync(
                _settings.PrinterIp,
                _settings.PrinterPort,
                connectCts.Token);

            bool connected = client.Connected;
            _logger.LogInformation("Printer connectivity test: {Result}",
                connected ? "SUCCESS" : "FAILED");

            return connected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Printer connectivity test failed for {Ip}:{Port}",
                _settings.PrinterIp, _settings.PrinterPort);
            return false;
        }
        finally
        {
            if (client != null)
            {
                try { client.Close(); } catch { /* ignore */ }
                client.Dispose();
            }
        }
    }
}
