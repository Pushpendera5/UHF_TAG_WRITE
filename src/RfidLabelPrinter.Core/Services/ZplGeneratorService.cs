using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RfidLabelPrinter.Core.Interfaces;

namespace RfidLabelPrinter.Core.Services;

/// <summary>
/// Professional ZPL generator that can parse HTML jewellery tags and convert them to ZPL.
/// Also handles RFID encoding for printers that support it.
/// </summary>
public class ZplGeneratorService : IZplGenerator
{
    private static string ConvertAsciiToHex(string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        return BitConverter.ToString(bytes).Replace("-", "");
    }

    private static bool IsHexString(string value)
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

    private static string NormalizeHexPayload(string hexValue, out int wordCount)
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

        wordCount = (hex.Length + 3) / 4;
        int paddedLength = wordCount * 4;
        if (hex.Length < paddedLength)
        {
            hex = hex.PadRight(paddedLength, '0');
        }

        return hex.ToUpperInvariant();
    }

    private static string BuildRfidCommand(string barcode)
    {
        string trimmed = barcode.Trim();
        
        // Validate barcode is not empty
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }
        
        string hexPayload = IsHexString(trimmed)
            ? NormalizeHexPayload(trimmed, out var wordCount)
            : NormalizeHexPayload(ConvertAsciiToHex(trimmed), out wordCount);

        // RFID Write Command for Zebra ZD621T:
        // ^RFW = RFID Field Write command
        // ,H = Hex format
        // ,1 = Bank 1 (EPC bank - standard for RFID EPC)
        // ,2 = Start address (offset 2 in EPC)
        // ,{wordCount} = Number of 16-bit words to write
        // ^FD{hexPayload}^FS = Field data with terminator
        // 
        // Removed ^RS8 as it's not a standard Zebra command and causes printer to void the label
        return $"^RFW,H,1,2,{wordCount}^FD{hexPayload}^FS\n";
    }

    /// <inheritdoc />
    public string InjectRfidCommands(string input, string barcode)
    {
        // Check if input is HTML
        if (input.Trim().StartsWith("<"))
        {
            return ConvertHtmlToZpl(input, barcode);
        }

        // Otherwise treat as ZPL and inject RFID
        return InjectRfidIntoZpl(input, barcode);
    }

    private string ConvertHtmlToZpl(string html, string barcode)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 1. Extract Data from HTML
        // Note: Based on your provided HTML structure
        string productName = doc.DocumentNode.SelectSingleNode("//div[contains(text(),'Silver')]")?.InnerText.Trim() ?? "Product";
        string itemCode = doc.DocumentNode.SelectSingleNode("//div[contains(text(),'PUJARI')]")?.InnerText.Split(' ')[1].Trim() ?? barcode;
        string category = doc.DocumentNode.SelectSingleNode("//div[contains(text(),'K-EAR')]")?.InnerText.Trim() ?? "";

        // Cleanup productName (it often has &nbsp; or <br>)
        productName = Regex.Replace(productName, @"\s+", " ").Trim();

        // 2. Build Professional ZPL Template
        var sb = new StringBuilder();
        sb.AppendLine("^XA");
        sb.AppendLine("^CI28"); // Support for special characters

        // --- RFID Section (EPC Write) ---
        sb.AppendLine(BuildRfidCommand(barcode));

        // --- Visual Layout Section ---
        
        // Product Name (Bold)
        sb.AppendLine("^CF0,35");
        sb.AppendLine($"^FO50,40^FD{productName}^FS");

        // Item Code / Category
        sb.AppendLine("^CF0,25");
        sb.AppendLine($"^FO50,90^FDCat: {category}^FS");
        sb.AppendLine($"^FO50,125^FDCode: {itemCode}^FS");

        // Barcode (Code 128)
        sb.AppendLine("^BY2,3,60"); // Bar width, ratio, height
        sb.AppendLine($"^FO50,170^BCN,60,Y,N,N^FD{barcode}^FS");

        sb.AppendLine("^XZ");

        return sb.ToString();
    }

    private string InjectRfidIntoZpl(string originalZpl, string barcode)
    {
        string rfidCommands = BuildRfidCommand(barcode);
        
        // If no RFID commands (empty barcode), return original ZPL
        if (string.IsNullOrEmpty(rfidCommands))
        {
            return originalZpl;
        }

        // Inject RFID commands right after ^XA (label start) for proper execution order
        // This ensures RFID encoding happens before visual elements are printed
        if (originalZpl.Contains("^XA", StringComparison.OrdinalIgnoreCase))
        {
            return originalZpl.Replace("^XA", $"^XA\n{rfidCommands}", StringComparison.OrdinalIgnoreCase);
        }

        // If no ^XA found, wrap with proper ZPL format
        return $"^XA\n{rfidCommands}{originalZpl}\n^XZ";
    }

    public string GenerateStandaloneLabel(string barcode)
    {
        return InjectRfidIntoZpl("", barcode);
    }
}
