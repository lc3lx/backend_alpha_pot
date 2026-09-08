using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    private readonly bool _useMySql;

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : this(options, new ConfigurationBuilder().Build())
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration)
        : base(options)
    {
        _useMySql = DatabaseProviderHelper.IsMySql(configuration);
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<BinollaLink> BinollaLinks => Set<BinollaLink>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ReferralQualification> ReferralQualifications => Set<ReferralQualification>();
    public DbSet<ReferralCommission> ReferralCommissions => Set<ReferralCommission>();
    public DbSet<ReferralReward> ReferralRewards => Set<ReferralReward>();
    public DbSet<ReferralPayout> ReferralPayouts => Set<ReferralPayout>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(1024);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            var telegramFilter = _useMySql ? "`TelegramUserId` IS NOT NULL" : "\"TelegramUserId\" IS NOT NULL";
            var emailFilter = _useMySql ? "`Email` IS NOT NULL" : "\"Email\" IS NOT NULL";
            var referralCodeFilter = _useMySql ? "`ReferralCode` IS NOT NULL" : "\"ReferralCode\" IS NOT NULL";
            e.HasIndex(x => x.TelegramUserId)
                .IsUnique()
                .HasFilter(telegramFilter);
            e.HasIndex(x => x.Email)
                .IsUnique()
                .HasFilter(emailFilter);
            e.HasIndex(x => x.ReferralCode)
                .IsUnique()
                .HasFilter(referralCodeFilter);
            e.HasIndex(x => x.ReferredByUserId);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.ReferralCode).HasMaxLength(16);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.EncryptedLoginPassword).HasColumnType("text");
            e.Property(x => x.Username).HasMaxLength(128);
            e.Property(x => x.FullName).HasMaxLength(256);
            e.Property(x => x.Country).HasMaxLength(128);
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.IsMarketingDemo).HasDefaultValue(false);
            // Locked by default — an admin has to open demo for a specific user.
            e.Property(x => x.DemoAllowed).HasDefaultValue(false);
            e.HasIndex(x => x.IsMarketingDemo);
            e.Property(x => x.MarketingDemoConfigJson).HasColumnType("text");
            e.Property(x => x.BotRuntimeJson).HasColumnType("text");
            e.HasOne(x => x.BinollaLink).WithOne(x => x.User).HasForeignKey<BinollaLink>(x => x.UserId);
        });

        modelBuilder.Entity<BinollaLink>(e =>
        {
            e.ToTable("binolla_links");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.ApprovalStatus);
            e.Property(x => x.EncryptedSsid).IsRequired();
            e.Property(x => x.EncryptedCookieHeader);
            e.Property(x => x.EncryptedBinollaEmail);
            e.Property(x => x.EncryptedBinollaPassword);
            e.Property(x => x.BinollaAccountIdentifier).HasMaxLength(128);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
            e.Property(x => x.ApprovalStatus).HasConversion<int>();
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.ActivationKey).HasMaxLength(128).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.Subscriptions).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.UserId, x.AccountType });
            e.Property(x => x.Asset).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.BinollaOrderId).HasMaxLength(128);
            e.Property(x => x.Amount).HasPrecision(18, 8);
            e.Property(x => x.Pnl).HasPrecision(18, 8);
            e.Property(x => x.AccountType).HasConversion<int>();
            e.HasOne(x => x.User).WithMany(x => x.Trades).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ActorUserId);
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.PreviousState).HasMaxLength(128);
            e.Property(x => x.NewState).HasMaxLength(128);
            e.Property(x => x.Detail).HasMaxLength(512);
        });

        modelBuilder.Entity<UserNotification>(e =>
        {
            e.ToTable("user_notifications");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Read });
            e.Property(x => x.Variant).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1024).IsRequired();
            e.Property(x => x.ActionPath).HasMaxLength(256);
            e.HasOne(x => x.User).WithMany(x => x.Notifications).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<ReferralQualification>(e =>
        {
            e.ToTable("referral_qualifications");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ReferredUserId).IsUnique();
            e.HasIndex(x => x.ReferrerUserId);
            e.HasIndex(x => x.Qualified);
            e.Property(x => x.PeakRealBalance).HasPrecision(18, 8);
            e.Property(x => x.AdminDepositNote).HasMaxLength(512);
            e.Property(x => x.AdminDepositBy).HasMaxLength(256);
        });

        modelBuilder.Entity<ReferralCommission>(e =>
        {
            e.ToTable("referral_commissions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ReferrerUserId);
            e.HasIndex(x => x.ReferredUserId);
            e.HasIndex(x => x.TradeId).IsUnique();
            e.Property(x => x.TradeAmount).HasPrecision(18, 8);
            e.Property(x => x.RatePercent).HasPrecision(9, 4);
            e.Property(x => x.Amount).HasPrecision(18, 8);
        });

        modelBuilder.Entity<ReferralReward>(e =>
        {
            e.ToTable("referral_rewards");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Kind, x.Tier }).IsUnique();
            e.Property(x => x.Amount).HasPrecision(18, 8);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.PaidBy).HasMaxLength(256);
            e.Property(x => x.Note).HasMaxLength(512);
        });

        modelBuilder.Entity<ReferralPayout>(e =>
        {
            e.ToTable("referral_payouts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Amount).HasPrecision(18, 8);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Method).HasMaxLength(64);
            e.Property(x => x.Destination).HasMaxLength(256);
            e.Property(x => x.DecidedBy).HasMaxLength(256);
            e.Property(x => x.AdminNote).HasMaxLength(512);
        });
    }
}
