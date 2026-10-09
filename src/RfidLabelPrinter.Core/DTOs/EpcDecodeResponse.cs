namespace RfidLabelPrinter.Core.DTOs;

public class EpcDecodeResponse
{
    public string EpcHex { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
}
