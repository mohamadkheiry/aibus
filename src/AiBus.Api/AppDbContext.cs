using Microsoft.EntityFrameworkCore;

namespace AiBus.Api;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AiProvider> Providers => Set<AiProvider>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<AiModel> Models => Set<AiModel>();
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<SystemSetting> Settings => Set<SystemSetting>();
    public DbSet<VisitEvent> Visits => Set<VisitEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Mobile).IsUnique();
        b.Entity<AiProvider>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<AiModel>().HasIndex(x => x.ModelId).IsUnique();
        b.Entity<UserApiKey>().HasIndex(x => x.KeyHash).IsUnique();
        b.Entity<WalletTransaction>().HasIndex(x => x.Authority);
        b.Entity<UsageRecord>().HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        b.Entity<VisitEvent>().HasIndex(x => x.CreatedAtUtc);
        b.Entity<SupportTicket>().HasIndex(x => x.ReferenceCode).IsUnique();
        b.Entity<SupportTicket>().HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        b.Entity<SupportTicket>().HasIndex(x => new { x.Status, x.Priority, x.UpdatedAtUtc });
        b.Entity<TicketMessage>().HasIndex(x => new { x.TicketId, x.CreatedAtUtc });

        b.Entity<AppUser>().Property(x => x.WalletUsd).HasPrecision(18, 8);
        b.Entity<UserApiKey>().Property(x => x.SpentUsd).HasPrecision(18, 8);
        b.Entity<UserApiKey>().Property(x => x.SpendLimitUsd).HasPrecision(18, 8);
        b.Entity<UsageRecord>().Property(x => x.CostUsd).HasPrecision(18, 8);
        b.Entity<AiModel>().Property(x => x.InputPricePerMillionUsd).HasPrecision(18, 8);
        b.Entity<AiModel>().Property(x => x.OutputPricePerMillionUsd).HasPrecision(18, 8);
        b.Entity<AiModel>().Property(x => x.CachedInputPricePerMillionUsd).HasPrecision(18, 8);
        b.Entity<ProviderCredential>().Property(x => x.InitialBalanceUsd).HasPrecision(18, 8);
        b.Entity<ProviderCredential>().Property(x => x.RemainingBalanceUsd).HasPrecision(18, 8);
        b.Entity<ProviderCredential>().Property(x => x.AlertThresholdUsd).HasPrecision(18, 8);
    }
}
