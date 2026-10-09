namespace RfidLabelPrinter.Core.Configuration;

/// <summary>
/// Configuration settings for the Zebra printer connection.
/// </summary>
public class PrinterSettings
{
    public const string SectionName = "PrinterSettings";

    /// <summary>
    /// IP address of the Zebra ZD621T printer.
    /// </summary>
    public string PrinterIp { get; set; } = "192.168.1.100";

    /// <summary>
    /// TCP port for raw ZPL communication (default: 9100).
    /// </summary>
    public int PrinterPort { get; set; } = 9100;

    /// <summary>
    /// TCP connection timeout in milliseconds.
    /// </summary>
    public int ConnectionTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// TCP send timeout in milliseconds.
    /// </summary>
    public int SendTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Maximum number of retries for failed RFID writes.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Number of labels to process in each worker batch.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Delay in milliseconds between worker polling cycles.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 2000;

    /// <summary>
    /// Delay in milliseconds between sending individual labels
    /// to give the printer time to process RFID encoding.
    /// </summary>
    public int InterLabelDelayMs { get; set; } = 200;
}
