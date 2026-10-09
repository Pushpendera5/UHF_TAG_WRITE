using Microsoft.EntityFrameworkCore;
using RfidLabelPrinter.Api.Workers;
using RfidLabelPrinter.Core.Configuration;
using RfidLabelPrinter.Core.Interfaces;
using RfidLabelPrinter.Core.Services;
using RfidLabelPrinter.Infrastructure.Data;
using RfidLabelPrinter.Infrastructure.Repositories;
using RfidLabelPrinter.Infrastructure.Services;
using Serilog;

// ============================================
// RFID Label Printer System
// ASP.NET Core Web API + Background Worker
// ============================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting RFID Label Printer System...");

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ──────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs/rfid-printer-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

    // ── Configuration ────────────────────────────────────────
    builder.Services.Configure<PrinterSettings>(
        builder.Configuration.GetSection(PrinterSettings.SectionName));

    // ── Database ─────────────────────────────────────────────
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseSqlServer(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(30);
            });
    });

    // ── Services (DI) ────────────────────────────────────────
    builder.Services.AddScoped<IPrintJobRepository, PrintJobRepository>();
    builder.Services.AddSingleton<IZplGenerator, ZplGeneratorService>();
    builder.Services.AddSingleton<IPrinterService, ZebraPrinterService>();

    // ── Background Worker ────────────────────────────────────
    // builder.Services.AddHostedService<PrintJobWorker>();

    // ── Controllers ──────────────────────────────────────────
    builder.Services.AddControllers();

    // ── CORS (for dashboard and external clients) ──────────────────
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // ── Static Files (for dashboard) ─────────────────────────
    builder.Services.AddDirectoryBrowser();

    var app = builder.Build();

    // ── Middleware Pipeline ───────────────────────────────────
    app.UseSerilogRequestLogging();

    app.UseCors("AllowAll");

    // Serve the dashboard from wwwroot
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();

    // ── Ensure Database Exists ───────────────────────────────
    _ = Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dbContext.Database.EnsureCreatedAsync(cts.Token);
            Log.Information("Database connection verified and schema ensured");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Database ensure/create timed out. API is still running.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not auto-create database. Ensure SQL Server is running and the connection string is correct. You can run sql/schema.sql manually.");
        }
    });

    Log.Information("RFID Label Printer System started successfully");
    Log.Information("Dashboard: http://localhost:5170");
    Log.Information("API: http://localhost:5170/api/print-jobs");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
