using Microsoft.AspNetCore.Mvc;
using RfidLabelPrinter.Core.Interfaces;

namespace RfidLabelPrinter.Api.Controllers;

/// <summary>
/// Health check and printer status controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IPrinterService _printerService;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IPrinterService printerService,
        ILogger<HealthController> logger)
    {
        _printerService = printerService;
        _logger = logger;
    }

    /// <summary>
    /// Basic health check endpoint.
    /// </summary>
    [HttpGet]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            status = "Healthy",
            service = "RFID Label Printer System",
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Tests connectivity to the Zebra printer.
    /// </summary>
    [HttpGet("printer")]
    public async Task<IActionResult> TestPrinter()
    {
        var connected = await _printerService.TestConnectionAsync();
        return Ok(new
        {
            printerConnected = connected,
            timestamp = DateTime.UtcNow
        });
    }
}
