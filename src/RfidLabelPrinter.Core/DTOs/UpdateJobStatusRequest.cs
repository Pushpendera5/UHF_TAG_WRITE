namespace RfidLabelPrinter.Core.DTOs;

public class UpdateJobStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
