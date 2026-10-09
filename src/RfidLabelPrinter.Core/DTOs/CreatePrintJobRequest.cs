using System.ComponentModel.DataAnnotations;

namespace RfidLabelPrinter.Core.DTOs;

/// <summary>
/// A single label item sent from the client application.
/// Contains the full ZPL label data (product info, barcode visual, etc.)
/// and the barcode value to encode into the RFID tag.
/// </summary>
public class LabelItem
{
    /// <summary>
    /// The barcode value that will be written to the RFID tag EPC memory.
    /// This is also used for tracking/identification in our system.
    /// </summary>
    [Required]
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    /// Optional PDF URL for this label item.
    /// If provided, server maps this to LabelZpl for downstream worker processing.
    /// </summary>
    public string? PdfUrl { get; set; }

    /// <summary>
    /// Alias for PdfUrl.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Alias for PdfUrl.
    /// </summary>
    public string? BarcodePdfUrl { get; set; }

    /// <summary>
    /// Full ZPL data from the client application for the label.
    /// This contains the complete label layout: product name, weight,
    /// purity, price, barcode image, etc. — everything the client wants printed.
    /// 
    /// Our system will inject RFID encoding commands into this ZPL
    /// before sending to the printer.
    /// 
    /// Example from upstream:
    /// ^XA
    /// ^FO50,30^CF0,30^FDGold Ring 22K^FS
    /// ^FO50,70^CF0,25^FDWt: 5.2g | Price: ₹28,500^FS
    /// ^FO50,120^BCN,80,Y,N,N^FD100234^FS
    /// ^XZ
    /// </summary>
    public string LabelZpl { get; set; } = string.Empty;
}

/// <summary>
/// Request body for creating a batch of print jobs.
/// Sent from the client application when user clicks "Print".
/// </summary>
public class CreatePrintJobRequest
{
    /// <summary>
    /// Array of label items, each containing ZPL data + barcode for RFID.
    /// Maximum 500 labels per batch.
    /// </summary>
    public List<LabelItem> Labels { get; set; } = new();

    /// <summary>
    /// Optional single-job barcode value for simple integrations.
    /// </summary>
    public string? Barcode { get; set; }

    /// <summary>
    /// Optional PDF URL field for simple integrations.
    /// Accepted keys in JSON can map to this via case-insensitive binder,
    /// for example: url, pdfUrl, barcodePdfUrl.
    /// </summary>
    public string? PdfUrl { get; set; }

    /// <summary>
    /// Backward/alternate alias for PDF URL payloads.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Backward/alternate alias for PDF URL payloads.
    /// </summary>
    public string? BarcodePdfUrl { get; set; }
}
