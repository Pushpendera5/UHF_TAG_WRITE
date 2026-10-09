using Microsoft.EntityFrameworkCore;
using RfidLabelPrinter.Core.Models;

namespace RfidLabelPrinter.Infrastructure.Data;

/// <summary>
/// Entity Framework Core database context for the RFID Label Printer system.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<PrintLog> PrintLogs => Set<PrintLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // PrintJob configuration
        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.ToTable("PrintJobs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Barcode)
                .IsRequired()
                .HasMaxLength(256);

            entity.Property(e => e.LabelZpl)
                .IsRequired()
                .HasColumnType("nvarchar(max)");

            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion<string>();

            entity.Property(e => e.ErrorMessage)
                .HasMaxLength(1000);

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            // Index for worker polling
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("IX_PrintJobs_Status_CreatedAt");

            // Index for batch lookups
            entity.HasIndex(e => e.BatchId)
                .HasDatabaseName("IX_PrintJobs_BatchId");
        });

        // PrintLog configuration
        modelBuilder.Entity<PrintLog>(entity =>
        {
            entity.ToTable("PrintLogs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Action)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(e => e.Message)
                .HasMaxLength(2000);

            entity.Property(e => e.Timestamp)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.HasOne(e => e.PrintJob)
                .WithMany()
                .HasForeignKey(e => e.PrintJobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.PrintJobId)
                .HasDatabaseName("IX_PrintLogs_PrintJobId");

            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("IX_PrintLogs_Timestamp")
                .IsDescending();
        });
    }
}
