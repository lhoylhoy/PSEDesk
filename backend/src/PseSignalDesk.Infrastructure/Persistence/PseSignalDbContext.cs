using Microsoft.EntityFrameworkCore;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Infrastructure.Persistence;

/// <summary>
/// EF Core SQLite context for every persisted artefact: daily snapshots, per-ticker
/// metrics, signal runs, the ranked items and the drop explanations.
/// </summary>
public class PseSignalDbContext : DbContext
{
    public PseSignalDbContext(DbContextOptions<PseSignalDbContext> options) : base(options)
    {
    }

    public DbSet<DailyMarketSnapshot> DailyMarketSnapshots => Set<DailyMarketSnapshot>();

    public DbSet<StockDailyMetric> StockDailyMetrics => Set<StockDailyMetric>();

    public DbSet<SignalRun> SignalRuns => Set<SignalRun>();

    public DbSet<SignalItem> SignalItems => Set<SignalItem>();

    public DbSet<SignalDrop> SignalDrops => Set<SignalDrop>();

    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<PortfolioAccount> PortfolioAccounts => Set<PortfolioAccount>();

    public DbSet<PortfolioTrade> PortfolioTrades => Set<PortfolioTrade>();

    public DbSet<TrainedHorizonModel> TrainedHorizonModels => Set<TrainedHorizonModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DailyMarketSnapshot>(entity =>
        {
            entity.ToTable("DailyMarketSnapshots");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TradeDate).IsUnique();
            entity.Property(e => e.TradeDate).IsRequired();
            entity.Property(e => e.MarketRegime).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.RegimeNote).HasMaxLength(900).IsRequired();
            entity.Property(e => e.SectorRotationJson).HasMaxLength(4000);
        });

        modelBuilder.Entity<StockDailyMetric>(entity =>
        {
            entity.ToTable("StockDailyMetrics");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CompanyName).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Sector).HasMaxLength(80).IsRequired();
            entity.Property(e => e.RecentDisclosuresJson).IsRequired();

            // Robust liquidity columns. Decimal precision is explicit because SQLite ignores it
            // but a future PostgreSQL target will not.
            entity.Property(e => e.AvgTurnover20dPhp).HasPrecision(18, 2);
            entity.Property(e => e.MedianTurnover20dPhp).HasPrecision(18, 2);
            entity.Property(e => e.WeinsteinStage).HasMaxLength(16);
            entity.HasIndex(e => new { e.TradeDate, e.Ticker }).IsUnique();
            entity.HasIndex(e => e.Ticker);
        });

        modelBuilder.Entity<KnowledgeDocument>(entity =>
        {
            entity.ToTable("KnowledgeDocuments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Slug).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(16).IsRequired();
            entity.Property(e => e.SourcePath).HasMaxLength(400).IsRequired();
            entity.Property(e => e.HorizonsCsv).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CategoriesCsv).HasMaxLength(160).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.HasIndex(e => e.Source);

            entity.HasMany(e => e.Chunks)
                .WithOne(c => c.Document)
                .HasForeignKey(c => c.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeChunk>(entity =>
        {
            entity.ToTable("KnowledgeChunks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SectionTitle).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Reference).HasMaxLength(240).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.HorizonsCsv).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CategoriesCsv).HasMaxLength(160).IsRequired();
            entity.HasIndex(e => e.Reference).IsUnique();
            entity.HasIndex(e => e.KnowledgeDocumentId);
        });

        modelBuilder.Entity<SignalRun>(entity =>
        {
            entity.ToTable("SignalRuns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Horizon).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.RawModelResponseJson).IsRequired();
            entity.Property(e => e.RecommendedCashPct).HasDefaultValue(0m);
            entity.HasIndex(e => new { e.RunDate, e.Horizon, e.CreatedAtUtc });

            entity.HasMany(e => e.Items)
                .WithOne(i => i.SignalRun)
                .HasForeignKey(i => i.SignalRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Drops)
                .WithOne(d => d.SignalRun)
                .HasForeignKey(d => d.SignalRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SignalItem>(entity =>
        {
            entity.ToTable("SignalItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(16).IsRequired();
            entity.Property(e => e.DriftStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Thesis).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.DeviationReason).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.CitedReferencesJson).HasMaxLength(600).IsRequired();
            entity.Property(e => e.EdgeVerdict).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.BearCase).HasMaxLength(400).IsRequired();
            entity.Property(e => e.OutcomeStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasIndex(e => new { e.SignalRunId, e.Rank }).IsUnique();
            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.OutcomeStatus);
        });

        modelBuilder.Entity<SignalDrop>(entity =>
        {
            entity.ToTable("SignalDrops");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(16).IsRequired();
            entity.Property(e => e.DropCategory).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(e => e.Ticker);
        });

        modelBuilder.Entity<PortfolioAccount>(entity =>
        {
            entity.ToTable("PortfolioAccounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StartingCapitalPhp).HasDefaultValue(100_000m);
            entity.Property(e => e.CashBalancePhp).HasDefaultValue(100_000m);
            entity.Property(e => e.MaxRiskPerTradePct).HasDefaultValue(1.0m);
            entity.Property(e => e.SmallSizeRiskMultiplier).HasDefaultValue(0.5m);
            entity.Property(e => e.MaxPositionPct).HasDefaultValue(20.0m);
            entity.Property(e => e.MaxPortfolioHeatPct).HasDefaultValue(6.0m);
        });

        modelBuilder.Entity<PortfolioTrade>(entity =>
        {
            entity.ToTable("PortfolioTrades");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CompanyName).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Sector).HasMaxLength(80).IsRequired();
            entity.Property(e => e.Horizon).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.EdgeVerdict).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(1000).IsRequired();
            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TrainedHorizonModel>(entity =>
        {
            entity.ToTable("TrainedHorizonModels");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Horizon).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.WeightsJson).IsRequired();
            entity.Property(e => e.FeatureStatsJson).IsRequired();
            entity.Property(e => e.LiveDrift).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasIndex(e => e.Horizon).IsUnique();
        });
    }
}
