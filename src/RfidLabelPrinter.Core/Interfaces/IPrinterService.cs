namespace RfidLabelPrinter.Core.Interfaces;

/// <summary>
/// Handles communication with the Zebra RFID printer via TCP/IP.
/// </summary>
public interface IPrinterService
{
    /// <summary>
    /// Sends a ZPL command string to the printer.
    /// </summary>
    /// <param name="zplData">The ZPL command(s) to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the data was sent successfully.</returns>
    Task<bool> SendZplAsync(string zplData, CancellationToken ct = default);

    /// <summary>
    /// Tests connectivity to the printer.
    /// </summary>
    /// <returns>True if the printer is reachable.</returns>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}
