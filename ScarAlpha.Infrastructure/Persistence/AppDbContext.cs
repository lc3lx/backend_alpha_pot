using Microsoft.EntityFrameworkCore;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<BinollaLink> BinollaLinks => Set<BinollaLink>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TelegramUserId).IsUnique();
            e.Property(x => x.Username).HasMaxLength(128);
            e.Property(x => x.FullName).HasMaxLength(256);
            e.Property(x => x.Country).HasMaxLength(128);
            e.Property(x => x.Role).HasConversion<int>();
            e.HasOne(x => x.BinollaLink).WithOne(x => x.User).HasForeignKey<BinollaLink>(x => x.UserId);
        });

        modelBuilder.Entity<BinollaLink>(e =>
        {
            e.ToTable("binolla_links");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.ApprovalStatus);
            e.Property(x => x.EncryptedSsid).IsRequired();
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
            e.Property(x => x.Asset).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.BinollaOrderId).HasMaxLength(128);
            e.Property(x => x.Amount).HasPrecision(18, 8);
            e.Property(x => x.Pnl).HasPrecision(18, 8);
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
    }
}
