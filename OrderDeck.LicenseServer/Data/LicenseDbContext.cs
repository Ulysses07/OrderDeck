using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Data;

// Non-sealed so LicenseReadOnlyDbContext can derive — see the Phase 5e HA work.
// All other DbContexts in this project should still be sealed; only this one
// is intentionally extensible.
public class LicenseDbContext : DbContext
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
    public DbSet<CustomerBackup> CustomerBackups => Set<CustomerBackup>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OperatorUser> OperatorUsers => Set<OperatorUser>();
    public DbSet<WhatsAppTemplateSettings> WhatsAppTemplateSettings => Set<WhatsAppTemplateSettings>();
    public DbSet<BroadcastPost> BroadcastPosts => Set<BroadcastPost>();
    public DbSet<Shopper> Shoppers => Set<Shopper>();
    public DbSet<ShopperBroadcasterLink> ShopperBroadcasterLinks => Set<ShopperBroadcasterLink>();
    public DbSet<WpfCustomerProjection> WpfCustomerProjections => Set<WpfCustomerProjection>();
    public DbSet<ShopperPushDevice> ShopperPushDevices => Set<ShopperPushDevice>();
    public DbSet<PaymentSubmissionAudit> PaymentSubmissionAudits => Set<PaymentSubmissionAudit>();
    public DbSet<ShopperRefreshToken> ShopperRefreshTokens => Set<ShopperRefreshToken>();
    public DbSet<ShopperSupportRequest> ShopperSupportRequests => Set<ShopperSupportRequest>();
    public DbSet<CustomerBalance> CustomerBalances => Set<CustomerBalance>();
    public DbSet<CustomerBalanceTransaction> CustomerBalanceTransactions => Set<CustomerBalanceTransaction>();
    public DbSet<ShopperPasswordResetCode> ShopperPasswordResetCodes => Set<ShopperPasswordResetCode>();
    public DbSet<LicenseSmsBalance> LicenseSmsBalances => Set<LicenseSmsBalance>();
    public DbSet<LicenseSmsTransaction> LicenseSmsTransactions => Set<LicenseSmsTransaction>();
    public DbSet<SmsCampaign> SmsCampaigns => Set<SmsCampaign>();
    public DbSet<SmsCampaignRecipient> SmsCampaignRecipients => Set<SmsCampaignRecipient>();
    public DbSet<WhatsAppAccount> WhatsAppAccounts => Set<WhatsAppAccount>();
    public DbSet<WaConversation> WaConversations => Set<WaConversation>();
    public DbSet<WaMessage> WaMessages => Set<WaMessage>();
    public DbSet<WaSendAttempt> WaSendAttempts => Set<WaSendAttempt>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

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
            b.Property(l => l.ShopperCode).HasMaxLength(20);
            b.HasIndex(l => l.ShopperCode).IsUnique();
            b.Property(l => l.PaymentIban).HasMaxLength(34);
            b.Property(l => l.PaymentAccountHolder).HasMaxLength(200);
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
            b.Property(s => s.YouTubeUsername).HasMaxLength(64);
            b.Property(s => s.InstagramUsername).HasMaxLength(64);
            b.Property(s => s.FacebookUsername).HasMaxLength(64);
            b.Property(s => s.TikTokUsername).HasMaxLength(64);
            b.Property(s => s.YouTubeChannelId).HasMaxLength(48);
            b.Property(s => s.FullName).HasMaxLength(200).IsRequired();
            b.Property(s => s.Address).HasMaxLength(500).IsRequired();
            b.Property(s => s.City).HasMaxLength(50);
            b.Property(s => s.District).HasMaxLength(50);
            b.Property(s => s.Phone).HasMaxLength(20);
            b.Property(s => s.Email).HasMaxLength(200);
            b.Property(s => s.Tckn).HasMaxLength(11);
            b.Property(s => s.IpAddress).HasMaxLength(64);
            b.Property(s => s.UserAgent).HasMaxLength(500);
            b.HasIndex(s => new { s.IntakeFormConfigId, s.SubmittedAt });
        });

        mb.Entity<CustomerBackup>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.BlobPath).HasMaxLength(500).IsRequired();
            e.Property(b => b.ChecksumSha256).HasMaxLength(64).IsRequired();
            e.Property(b => b.UserAgent).HasMaxLength(200);
            e.Property(b => b.MachineName).HasMaxLength(100);
            e.HasOne(b => b.Customer)
                .WithMany()
                .HasForeignKey(b => b.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => new { b.CustomerId, b.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("IX_CustomerBackups_CustomerId_CreatedAt_DESC");
        });

        mb.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
            b.Property(t => t.CreatedByIp).HasMaxLength(64);
            b.HasOne(t => t.Customer).WithMany()
                .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => new { t.CustomerId, t.RevokedAt });
        });

        mb.Entity<PushDevice>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.DeviceId).HasMaxLength(64).IsRequired();
            b.Property(d => d.Platform).HasMaxLength(16).IsRequired();
            b.Property(d => d.PushToken).HasMaxLength(512).IsRequired();
            b.HasOne(d => d.Customer).WithMany()
                .HasForeignKey(d => d.CustomerId).OnDelete(DeleteBehavior.Cascade);
            // Same customer + device → upsert (no duplicate per device).
            b.HasIndex(d => new { d.CustomerId, d.DeviceId }).IsUnique();
            b.HasIndex(d => d.PushToken);
        });

        mb.Entity<Payment>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.PayerName).HasMaxLength(200).IsRequired();
            b.Property(p => p.Amount).HasPrecision(18, 2);
            b.Property(p => p.ReferansNo).HasMaxLength(64).IsRequired();
            b.Property(p => p.PdfHash).HasMaxLength(64);
            b.HasIndex(p => p.PdfHash).IsUnique();
            b.Property(p => p.RejectReason).HasMaxLength(500);
            b.Property(p => p.Status).HasConversion<int>();
            b.Property(p => p.ShipmentDirective).HasConversion<int>();
            b.HasOne(p => p.License).WithMany()
                .HasForeignKey(p => p.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Duplicate dekont protection: same license + same referansNo → reject.
            b.HasIndex(p => new { p.LicenseId, p.ReferansNo }).IsUnique();
            // Common query: list pending by license, newest first.
            b.HasIndex(p => new { p.LicenseId, p.Status, p.CreatedAt });
            // Kargo PR E: mobile Panel "Bekleyen kargolar" / "Alıcı ödemeli" tab filtreleri.
            b.HasIndex(p => new { p.LicenseId, p.ShipmentDirective, p.Status });
            // Shopper upload alanları — Faz 0a, 2026-05-20.
            b.Property(p => p.MediaObjectKey).HasMaxLength(256);
            b.Property(p => p.MediaContentType).HasMaxLength(128);
            b.Property(p => p.MetadataHash).HasMaxLength(64);
            b.HasIndex(p => p.MetadataHash);
            b.Property(p => p.RecipientIban).HasMaxLength(34);
            b.Property(p => p.RecipientName).HasMaxLength(200);
            b.Property(p => p.FraudFlags).HasMaxLength(256).IsRequired();
            b.Property(p => p.ParserConfidence).HasMaxLength(16).IsRequired();
            b.HasIndex(p => p.ShopperId);
        });

        // Siparişler (PR siparis-sync 2026-05-13): WPF lokal StreamSession + Label'ların
        // server replikası, mobile Panel Siparişler ekranı için.
        mb.Entity<StreamSession>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Title).HasMaxLength(200);
            b.Property(s => s.Platforms).HasMaxLength(200);
            b.Property(s => s.Notes).HasMaxLength(2000);
            b.HasOne(s => s.License).WithMany()
                .HasForeignKey(s => s.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Mobile "yayın listesi" en yeni başta.
            b.HasIndex(s => new { s.LicenseId, s.StartedAt });
            b.HasIndex(s => new { s.LicenseId, s.UpdatedAt }); // reverse-sync cursor
        });

        mb.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerId).HasMaxLength(64).IsRequired();
            b.Property(o => o.Platform).HasMaxLength(32).IsRequired();
            b.Property(o => o.Username).HasMaxLength(200).IsRequired();
            b.Property(o => o.DisplayName).HasMaxLength(200);
            b.Property(o => o.MessageText).HasMaxLength(2000).IsRequired();
            b.Property(o => o.Code).HasMaxLength(64);
            b.Property(o => o.Price).HasPrecision(18, 2);
            b.Property(o => o.CancelReason).HasMaxLength(500);
            b.HasOne(o => o.License).WithMany()
                .HasForeignKey(o => o.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // 2026-05-15 fix: SQL Server iki cascade path (Order→License + Order→
            // Session→License) için "may cause cycles" verir. NoAction'a alındı;
            // StreamSession silinmediği sürece davranış aynı.
            b.HasOne(o => o.Session).WithMany()
                .HasForeignKey(o => o.SessionId).OnDelete(DeleteBehavior.NoAction);
            // "Belirli yayının siparişleri" sorgusu için.
            b.HasIndex(o => new { o.LicenseId, o.SessionId, o.AddedAt });
            // "Müşterinin tüm siparişleri" sorgusu için.
            b.HasIndex(o => new { o.LicenseId, o.CustomerId });
            // Reverse-sync cursor.
            b.HasIndex(o => new { o.LicenseId, o.UpdatedAt });
        });

        // WhatsApp template sync (PR 2026-05-15): WPF PaymentSettings replikası.
        mb.Entity<WhatsAppTemplateSettings>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.PaymentTemplate).HasMaxLength(2000).IsRequired();
            b.Property(s => s.ShippingWonTemplate).HasMaxLength(2000).IsRequired();
            b.HasOne(s => s.License).WithMany()
                .HasForeignKey(s => s.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // License başına en fazla bir satır.
            b.HasIndex(s => s.LicenseId).IsUnique();
        });

        mb.Entity<BroadcastPost>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Type).HasConversion<int>();
            b.Property(p => p.TextBody).HasMaxLength(2000);
            b.Property(p => p.MediaObjectKey).HasMaxLength(512);
            b.Property(p => p.MediaContentType).HasMaxLength(64);
            b.HasOne(p => p.License).WithMany()
                .HasForeignKey(p => p.LicenseId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => new { p.LicenseId, p.CreatedAt })
                .IsDescending(false, true);
            b.HasIndex(p => new { p.ExpiresAt, p.IsPinned });
        });

        // Multi-operator PR-5 (2026-05-14): yayıncı ekibinin ek üyeleri.
        mb.Entity<OperatorUser>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.Email).HasMaxLength(256).IsRequired();
            b.Property(o => o.Name).HasMaxLength(200).IsRequired();
            b.Property(o => o.PasswordHash).HasMaxLength(256).IsRequired();
            b.Property(o => o.Role).HasMaxLength(16).IsRequired();
            b.HasOne(o => o.License).WithMany()
                .HasForeignKey(o => o.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Aynı License altında aynı email'den ikinci kayıt olmasın.
            b.HasIndex(o => new { o.LicenseId, o.Email }).IsUnique();
        });

        // Kümülatif kargo PR-D (2026-05-13): WPF lokal Shipment'ların server replikası.
        mb.Entity<Shipment>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.CustomerId).HasMaxLength(64).IsRequired();
            b.Property(s => s.CumulativeAmount).HasPrecision(18, 2);
            b.Property(s => s.Status).HasConversion<int>();
            b.HasOne(s => s.License).WithMany()
                .HasForeignKey(s => s.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Mobile Panel "Bekleyen kargolar" / "Alıcı ödemeli" tab filtreleri.
            b.HasIndex(s => new { s.LicenseId, s.Status, s.CreatedAt });
            // Customer detay query (müşterinin tüm kargo dosyaları).
            b.HasIndex(s => new { s.LicenseId, s.CustomerId });
            // Reverse sync cursor.
            b.HasIndex(s => new { s.LicenseId, s.UpdatedAt });
        });

        mb.Entity<Shopper>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.FullName).HasMaxLength(200).IsRequired();
            b.Property(s => s.Phone).HasMaxLength(20).IsRequired();
            b.HasIndex(s => s.Phone).IsUnique();
            b.Property(s => s.PasswordHash).HasMaxLength(256).IsRequired();
            b.Property(s => s.Address).HasMaxLength(500).IsRequired();
            b.Property(s => s.Email).HasMaxLength(256);
            b.Property(s => s.Tc).HasMaxLength(11);
            // Opt-in: ticari ileti izni varsayılan kapalı. İzin yalnızca kayıt
            // ekranındaki açık onay kutusundan (register SmsConsent=true) gelir.
            b.Property(s => s.SmsConsent).HasDefaultValue(false);
        });

        mb.Entity<ShopperBroadcasterLink>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasOne(l => l.Shopper).WithMany().HasForeignKey(l => l.ShopperId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(l => l.License).WithMany().HasForeignKey(l => l.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            // Filtered unique: only one active link per (Shopper, License) pair.
            // Historical (left) rows are allowed to coexist so re-join creates a new row.
            b.HasIndex(l => new { l.ShopperId, l.LicenseId })
             .HasFilter("[LeftAt] IS NULL")
             .IsUnique();
            b.HasIndex(l => new { l.LicenseId, l.JoinedAt });
            b.Property(l => l.Platform).HasMaxLength(32).IsRequired();
            b.Property(l => l.Username).HasMaxLength(128).IsRequired();
        });

        mb.Entity<WpfCustomerProjection>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.Platform).HasMaxLength(32).IsRequired();
            b.Property(c => c.Username).HasMaxLength(128).IsRequired();
            b.Property(c => c.FullName).HasMaxLength(200);
            b.Property(c => c.Phone).HasMaxLength(20);
            b.Property(c => c.Address).HasMaxLength(500);
            b.HasIndex(c => new { c.LicenseId, c.Platform, c.Username });
        });

        mb.Entity<ShopperPushDevice>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasOne(d => d.Shopper).WithMany().HasForeignKey(d => d.ShopperId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(d => d.DeviceId).HasMaxLength(64).IsRequired();
            b.Property(d => d.Platform).HasMaxLength(16).IsRequired();
            b.Property(d => d.PushToken).HasMaxLength(512).IsRequired();
            b.HasIndex(d => new { d.ShopperId, d.DeviceId }).IsUnique();
        });

        mb.Entity<PaymentSubmissionAudit>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.IpAddress).HasMaxLength(45).IsRequired();
            b.Property(a => a.UserAgent).HasMaxLength(512).IsRequired();
            b.Property(a => a.FraudFlags).HasMaxLength(256).IsRequired();
            b.Property(a => a.ParserConfidence).HasMaxLength(16).IsRequired();
            b.HasIndex(a => a.PaymentId);
            b.HasIndex(a => a.CreatedAt);
            b.HasIndex(a => new { a.LicenseId, a.CreatedAt });   // for license-level rate limit window queries
        });

        mb.Entity<ShopperRefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.Shopper).WithMany().HasForeignKey(t => t.ShopperId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            b.HasIndex(t => t.TokenHash);
            b.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
            b.Property(t => t.CreatedByIp).HasMaxLength(45);
        });

        mb.Entity<ShopperSupportRequest>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasOne(r => r.Shopper).WithMany().HasForeignKey(r => r.ShopperId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(r => r.License).WithMany().HasForeignKey(r => r.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(r => r.Kind).HasMaxLength(32).IsRequired();
            b.HasIndex(r => new { r.LicenseId, r.ResolvedAt, r.CreatedAt });
        });

        mb.Entity<ShopperPasswordResetCode>(b =>
        {
            b.HasKey(c => c.Id);
            // FK→Shopper tek cascade yolu (Shopper kök entity, multiple-path yok).
            b.HasOne(c => c.Shopper).WithMany().HasForeignKey(c => c.ShopperId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.CodeHash).HasMaxLength(256).IsRequired();
            b.Property(c => c.RequestIp).HasMaxLength(45);
            // Rate-limit / en-son-kod sorguları için.
            b.HasIndex(c => new { c.ShopperId, c.CreatedAt });
            // Global günlük tavan sorgusu (CreatedAt >= bugün).
            b.HasIndex(c => c.CreatedAt);
        });

        mb.Entity<CustomerBalance>(b =>
        {
            b.HasKey(c => c.Id);
            // License cascade = primary cleanup path. WpfCustomer FK NoAction —
            // SQL Server "multiple cascade paths" hatasını önler (WpfCustomerProjection
            // de License'a cascade, çift yol oluşurdu). License silinince zaten
            // CustomerBalance bu cascade ile temizlenir; WpfCustomerProjection
            // tek başına silinmiyor (yalnızca License cascade'iyle).
            b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.WpfCustomer).WithMany().HasForeignKey(c => c.WpfCustomerId)
             .OnDelete(DeleteBehavior.NoAction);
            b.Property(c => c.Balance).HasPrecision(18, 2);
            b.HasIndex(c => new { c.LicenseId, c.WpfCustomerId }).IsUnique();
        });

        mb.Entity<CustomerBalanceTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.License).WithMany().HasForeignKey(t => t.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(t => t.WpfCustomer).WithMany().HasForeignKey(t => t.WpfCustomerId)
             .OnDelete(DeleteBehavior.NoAction);
            b.Property(t => t.Amount).HasPrecision(18, 2);
            b.Property(t => t.OriginalAmount).HasPrecision(18, 2);
            b.Property(t => t.ShippingDeducted).HasPrecision(18, 2);
            b.Property(t => t.Kind).HasMaxLength(32).IsRequired();
            b.Property(t => t.Reason).HasMaxLength(500);
            b.HasIndex(t => new { t.LicenseId, t.WpfCustomerId, t.CreatedAt });
        });

        mb.Entity<LicenseSmsBalance>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasOne(s => s.License).WithMany().HasForeignKey(s => s.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(s => s.LicenseId).IsUnique();
        });

        mb.Entity<LicenseSmsTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.License).WithMany().HasForeignKey(t => t.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(t => t.Kind).HasMaxLength(32).IsRequired();
            b.Property(t => t.Reason).HasMaxLength(500);
            b.HasIndex(t => new { t.LicenseId, t.CreatedAt });
        });

        mb.Entity<SmsCampaign>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.MessageBody).HasMaxLength(2000).IsRequired();
            b.Property(c => c.Status).HasMaxLength(16).IsRequired();
            b.HasIndex(c => new { c.LicenseId, c.CreatedAt });
        });

        mb.Entity<SmsCampaignRecipient>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasOne(r => r.Campaign).WithMany().HasForeignKey(r => r.CampaignId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(r => r.Phone).HasMaxLength(20).IsRequired();
            b.Property(r => r.Status).HasMaxLength(16).IsRequired();
            b.Property(r => r.Error).HasMaxLength(500);
            b.HasIndex(r => r.CampaignId);
        });

        mb.Entity<WhatsAppAccount>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasOne(a => a.License).WithMany().HasForeignKey(a => a.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(a => a.WabaId).HasMaxLength(64).IsRequired();
            b.Property(a => a.PhoneNumberId).HasMaxLength(64).IsRequired();
            b.Property(a => a.DisplayPhoneNumber).HasMaxLength(20).IsRequired();
            b.Property(a => a.VerifiedName).HasMaxLength(200);
            b.Property(a => a.AccessTokenProtected).HasMaxLength(4000).IsRequired();
            b.Property(a => a.Status).HasMaxLength(16).IsRequired();
            b.Property(a => a.LastError).HasMaxLength(500);
            // Webhook yönlendirmesi bu alandan tenant bulur → global unique.
            b.HasIndex(a => a.PhoneNumberId).IsUnique();
            b.HasIndex(a => a.LicenseId);
        });

        mb.Entity<WaConversation>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.CustomerPhone).HasMaxLength(20).IsRequired();
            b.Property(c => c.ProfileName).HasMaxLength(200);
            b.Property(c => c.PhoneNumberId).HasMaxLength(64).IsRequired();
            b.Property(c => c.Status).HasMaxLength(16).IsRequired();
            b.HasIndex(c => new { c.LicenseId, c.CustomerPhone }).IsUnique();
            b.HasIndex(c => new { c.LicenseId, c.LastMessageAt });
        });

        mb.Entity<WaMessage>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasOne(m => m.Conversation).WithMany().HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(m => m.WamId).HasMaxLength(128).IsRequired();
            b.Property(m => m.Direction).HasMaxLength(8).IsRequired();
            b.Property(m => m.Origin).HasMaxLength(16);
            b.Property(m => m.Type).HasMaxLength(24).IsRequired();
            b.Property(m => m.Body).HasMaxLength(8000);
            b.Property(m => m.MediaR2Key).HasMaxLength(500);
            b.Property(m => m.MediaMimeType).HasMaxLength(120);
            b.Property(m => m.TemplateName).HasMaxLength(200);
            b.Property(m => m.Status).HasMaxLength(16).IsRequired();
            b.Property(m => m.ErrorCode).HasMaxLength(32);
            b.Property(m => m.ErrorMessage).HasMaxLength(1000);
            // Webhook "at least once" teslim eder → tekrar gelen olay yazılamaz.
            b.HasIndex(m => m.WamId).IsUnique();
            b.HasIndex(m => new { m.ConversationId, m.Timestamp });
            b.HasIndex(m => new { m.LicenseId, m.Timestamp });
        });

        mb.Entity<WaSendAttempt>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasOne(a => a.License).WithMany().HasForeignKey(a => a.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(a => a.Status).HasMaxLength(16).IsRequired();
            b.Property(a => a.ErrorCode).HasMaxLength(32);
            b.Property(a => a.ErrorMessage).HasMaxLength(1000);
            b.HasIndex(a => a.StartedAt);
        });

        mb.Entity<Category>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(120).IsRequired();
            b.Property(c => c.Path).HasMaxLength(512).IsRequired();
            b.HasOne(c => c.License).WithMany()
                .HasForeignKey(c => c.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: alt kategorisi olan kategori silinemesin, controller 409
            // dönsün. Cascade olsaydı tek DELETE koca ağacı sessizce uçururdu.
            b.HasOne(c => c.ParentCategory).WithMany()
                .HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.LicenseId, c.Path });
            b.HasIndex(c => new { c.LicenseId, c.ParentCategoryId, c.SortOrder });
        });

        mb.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Code).HasMaxLength(32).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.DefaultPrice).HasPrecision(18, 2);
            b.Property(p => p.Cost).HasPrecision(18, 2);
            b.Property(p => p.Axis1Name).HasMaxLength(40);
            b.Property(p => p.Axis2Name).HasMaxLength(40);
            b.Property(p => p.Axis1Role).HasConversion<int>();
            b.Property(p => p.Axis2Role).HasConversion<int>();
            b.Property(p => p.PhotoObjectKey).HasMaxLength(512);
            b.Property(p => p.PhotoContentType).HasMaxLength(100);
            b.HasOne(p => p.License).WithMany()
                .HasForeignKey(p => p.LicenseId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Category).WithMany()
                .HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
            // Ürün kodu LİSANS BAŞINA benzersiz — her yayıncının kendi A1'i olur.
            b.HasIndex(p => new { p.LicenseId, p.Code }).IsUnique();
            b.HasIndex(p => new { p.LicenseId, p.IsArchived, p.UpdatedAt });
        });

        mb.Entity<ProductVariant>(b =>
        {
            b.HasKey(v => v.Id);
            b.Property(v => v.Axis1Value).HasMaxLength(60);
            b.Property(v => v.Axis2Value).HasMaxLength(60);
            b.Property(v => v.Axis1Code).HasMaxLength(8);
            b.Property(v => v.Axis2Code).HasMaxLength(8);
            b.Property(v => v.VariantCode).HasMaxLength(64).IsRequired();
            b.Property(v => v.Barcode).HasMaxLength(64);
            b.HasOne(v => v.Product).WithMany(p => p.Variants)
                .HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(v => new { v.ProductId, v.VariantCode }).IsUnique();
            b.HasIndex(v => new { v.LicenseId, v.VariantCode });
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
