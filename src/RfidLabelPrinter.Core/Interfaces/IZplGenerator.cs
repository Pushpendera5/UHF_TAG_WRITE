namespace RfidLabelPrinter.Core.Interfaces;

/// <summary>
/// Generates and modifies ZPL (Zebra Programming Language) commands for RFID label printing.
/// </summary>
public interface IZplGenerator
{
    /// <summary>
    /// Takes the original ZPL from the upstream application and injects RFID encoding commands.
    /// The barcode value is converted to hex and written to the EPC memory.
    /// The original label layout (product info, barcode visual, etc.) is preserved.
    /// </summary>
    /// <param name="originalZpl">Complete ZPL from the upstream application.</param>
    /// <param name="barcode">Barcode value to encode into RFID EPC memory.</param>
    /// <returns>Modified ZPL with RFID commands injected.</returns>
    string InjectRfidCommands(string originalZpl, string barcode);

    /// <summary>
    /// Generates a standalone ZPL label with barcode + RFID (fallback if the client doesn't send ZPL).
    /// </summary>
    /// <param name="barcode">The barcode value to print and encode.</param>
    /// <returns>ZPL string for one label.</returns>
    string GenerateStandaloneLabel(string barcode);
}
