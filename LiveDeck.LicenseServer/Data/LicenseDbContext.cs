using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Data;

public sealed class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<EmailConfirmationToken> EmailConfirmationTokens => Set<EmailConfirmationToken>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<IntakeFormConfig> IntakeFormConfigs => Set<IntakeFormConfig>();
    public DbSet<IntakeFormSubmission> IntakeFormSubmissions => Set<IntakeFormSubmission>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Customer>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Email).HasMaxLength(256).IsRequired();
            b.HasIndex(c => c.Email).IsUnique();
            b.Property(c => c.Name).HasMaxLength(200).IsRequired();
            b.Property(c => c.PasswordHash).HasMaxLength(256).IsRequired();
        });

        mb.Entity<AdminUser>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Username).HasMaxLength(64).IsRequired();
            b.HasIndex(a => a.Username).IsUnique();
            b.Property(a => a.PasswordHash).HasMaxLength(256).IsRequired();
        });

        mb.Entity<Sku>(b =>
        {
            b.HasKey(s => s.Code);
            b.Property(s => s.Code).HasMaxLength(16);
            b.Property(s => s.DisplayName).HasMaxLength(80).IsRequired();
            b.Property(s => s.Description).HasMaxLength(500);
        });

        mb.Entity<License>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.LicenseKey).HasMaxLength(40).IsRequired();
            b.HasIndex(l => l.LicenseKey).IsUnique();
            b.HasOne(l => l.Customer).WithMany(c => c.Licenses)
                .HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(l => l.Sku).WithMany()
                .HasForeignKey(l => l.SkuCode).OnDelete(DeleteBehavior.Restrict);
            b.Property(l => l.RevokeReason).HasMaxLength(500);
        });

        mb.Entity<Activation>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.HardwareFingerprint).HasMaxLength(128).IsRequired();
            b.Property(a => a.MachineName).HasMaxLength(128);
            b.HasOne(a => a.License).WithMany(l => l.Activations)
                .HasForeignKey(a => a.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Filtered unique index — only enforce uniqueness for active rows
            b.HasIndex(a => new { a.LicenseId, a.HardwareFingerprint })
                .HasFilter("[DeactivatedAt] IS NULL")
                .IsUnique();
        });

        mb.Entity<EmailConfirmationToken>(b =>
        {
            b.HasKey(t => t.Token);
            b.HasOne(t => t.Customer).WithMany()
                .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CustomerId, t.UsedAt });
        });

        mb.Entity<AuditLogEntry>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.AdminUsername).HasMaxLength(64).IsRequired();
            b.Property(a => a.EventType).HasMaxLength(64).IsRequired();
            b.Property(a => a.TargetType).HasMaxLength(32).IsRequired();
            b.Property(a => a.TargetId).HasMaxLength(64);
            b.Property(a => a.Details).HasMaxLength(4000);
            b.Property(a => a.IpAddress).HasMaxLength(64);
            b.HasIndex(a => a.OccurredAt);
            b.HasIndex(a => new { a.AdminId, a.OccurredAt });
            b.HasIndex(a => new { a.TargetType, a.TargetId });
        });

        mb.Entity<EmailLog>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.TemplateKey).HasMaxLength(64).IsRequired();
            b.Property(e => e.ContextKey).HasMaxLength(64);
            b.Property(e => e.Error).HasMaxLength(2000);
            b.HasIndex(e => new { e.CustomerId, e.TemplateKey, e.ContextKey });
            b.HasIndex(e => e.SentAt);
        });

        mb.Entity<PasswordResetToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.Customer).WithMany()
                .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CustomerId, t.UsedAt });
        });

        mb.Entity<IntakeFormConfig>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.Customer).WithOne()
                .HasForeignKey<IntakeFormConfig>(c => c.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.Slug).HasMaxLength(32).IsRequired();
            b.HasIndex(c => c.Slug).IsUnique();
            b.Property(c => c.WhatsAppPhone).HasMaxLength(20).IsRequired();
            b.Property(c => c.CustomTitle).HasMaxLength(100);
        });

        mb.Entity<IntakeFormSubmission>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasOne(s => s.Config).WithMany()
                .HasForeignKey(s => s.IntakeFormConfigId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(s => s.Username).HasMaxLength(64).IsRequired();
            b.Property(s => s.FullName).HasMaxLength(200).IsRequired();
            b.Property(s => s.Address).HasMaxLength(500).IsRequired();
            b.Property(s => s.IpAddress).HasMaxLength(64);
            b.Property(s => s.UserAgent).HasMaxLength(500);
            b.HasIndex(s => new { s.IntakeFormConfigId, s.SubmittedAt });
        });

        // Seed SKUs
        mb.Entity<Sku>().HasData(
            new Sku { Code = "STD", DisplayName = "Standard",
                      DefaultDurationDays = 365, DefaultActivationSlots = 1,
                      Description = "Tek cihaz, 1 yıl" },
            new Sku { Code = "PRO", DisplayName = "Professional",
                      DefaultDurationDays = 365, DefaultActivationSlots = 3,
                      Description = "3 cihaz, 1 yıl" });
    }
}
