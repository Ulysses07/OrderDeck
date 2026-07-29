using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppMessagingServiceTests
{
    /// <summary>Graph'a hiç gitmeyen sahte gönderen — çağrıları kaydeder.</summary>
    private sealed class RecordingSender : IWhatsAppSender
    {
        public List<(WhatsAppSendContext Ctx, string To, string Text)> Texts { get; } = new();
        public List<(WhatsAppSendContext Ctx, string To, WhatsAppTemplate Tpl)> Templates { get; } = new();
        public WhatsAppSendResult NextResult { get; set; } = WhatsAppSendResult.Success("wamid.TEST1");

        public Task<WhatsAppSendResult> SendTextAsync(
            WhatsAppSendContext ctx, string to, string text, CancellationToken ct = default)
        {
            Texts.Add((ctx, to, text));
            return Task.FromResult(NextResult);
        }

        public Task<WhatsAppSendResult> SendTemplateAsync(
            WhatsAppSendContext ctx, string to, WhatsAppTemplate tpl, CancellationToken ct = default)
        {
            Templates.Add((ctx, to, tpl));
            return Task.FromResult(NextResult);
        }
    }

    private static (LicenseDbContext Db, WhatsAppMessagingService Svc, RecordingSender Sender, Guid LicenseId)
        Build(bool withAccount = true)
    {
        var opts = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wa-{Guid.NewGuid():N}")
            .Options;
        var db = new LicenseDbContext(opts);

        var licenseId = Guid.NewGuid();
        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        if (withAccount)
        {
            db.WhatsAppAccounts.Add(new WhatsAppAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WabaId = "waba-1",
                PhoneNumberId = "pnid-1",
                DisplayPhoneNumber = "+905550000000",
                AccessTokenProtected = accounts.ProtectToken("secret-token"),
                Status = "active",
                ConnectedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        var sender = new RecordingSender();
        var svc = new WhatsAppMessagingService(
            db, sender, accounts, NullLogger<WhatsAppMessagingService>.Instance);
        return (db, svc, sender, licenseId);
    }

    private static void SeedConversation(
        LicenseDbContext db, Guid licenseId, string phone, DateTimeOffset? lastInboundAt)
    {
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = phone,
            PhoneNumberId = "pnid-1",
            Status = "open",
            LastInboundAt = lastInboundAt,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task SendText_fails_without_account_and_never_calls_graph()
    {
        var (_, svc, sender, licenseId) = Build(withAccount: false);

        var result = await svc.SendTextAsync(licenseId, "+905321234567", "merhaba", "panel", default);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(WhatsAppMessagingService.ErrNoAccount);
        sender.Texts.Should().BeEmpty();
    }

    [Fact]
    public async Task SendText_blocked_when_window_closed_and_never_calls_graph()
    {
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddHours(-25));

        var result = await svc.SendTextAsync(licenseId, "+90 532 123 45 67", "merhaba", "panel", default);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(WhatsAppMessagingService.ErrWindowClosed);
        sender.Texts.Should().BeEmpty();
        db.WaMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendText_blocked_when_no_inbound_ever()
    {
        var (_, svc, sender, licenseId) = Build();

        var result = await svc.SendTextAsync(licenseId, "905321234567", "merhaba", "panel", default);

        result.ErrorCode.Should().Be(WhatsAppMessagingService.ErrWindowClosed);
        sender.Texts.Should().BeEmpty();
    }

    [Fact]
    public async Task SendText_succeeds_within_window_and_persists_message()
    {
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddHours(-2));

        var result = await svc.SendTextAsync(licenseId, "+905321234567", "merhaba", "panel", default);

        result.Ok.Should().BeTrue();
        sender.Texts.Should().ContainSingle();
        sender.Texts[0].To.Should().Be("905321234567");
        sender.Texts[0].Ctx.PhoneNumberId.Should().Be("pnid-1");
        sender.Texts[0].Ctx.AccessToken.Should().Be("secret-token");

        var msg = db.WaMessages.Single();
        msg.Direction.Should().Be("out");
        msg.Origin.Should().Be("panel");
        msg.Type.Should().Be("text");
        msg.Body.Should().Be("merhaba");
        msg.Status.Should().Be("sent");
        msg.WamId.Should().Be("wamid.TEST1");
    }

    [Fact]
    public async Task Outbound_does_not_open_the_window()
    {
        var (db, svc, _, licenseId) = Build();
        var inbound = DateTimeOffset.UtcNow.AddHours(-2);
        SeedConversation(db, licenseId, "905321234567", inbound);

        await svc.SendTextAsync(licenseId, "905321234567", "merhaba", "panel", default);

        var convo = db.WaConversations.Single();
        convo.LastInboundAt.Should().BeCloseTo(inbound, TimeSpan.FromSeconds(1));
        convo.LastMessageAt.Should().NotBeNull();
        convo.LastMessageAt!.Value.Should().BeAfter(inbound);
    }

    [Fact]
    public async Task Failed_send_is_persisted_with_error_and_local_wamid()
    {
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddHours(-1));
        sender.NextResult = WhatsAppSendResult.Failure("131026", "Message undeliverable");

        var result = await svc.SendTextAsync(licenseId, "905321234567", "merhaba", "panel", default);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("131026");

        var msg = db.WaMessages.Single();
        msg.Status.Should().Be("failed");
        msg.ErrorCode.Should().Be("131026");
        msg.WamId.Should().StartWith("local:");
    }

    [Fact]
    public async Task SendTemplate_works_even_when_window_closed()
    {
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddDays(-5));
        var tpl = new WhatsAppTemplate("odeme_hatirlatma", "tr", new[] { "Ayşe", "250 TL" });

        var result = await svc.SendTemplateAsync(licenseId, "905321234567", tpl, "panel", default);

        result.Ok.Should().BeTrue();
        sender.Templates.Should().ContainSingle();
        sender.Templates[0].Tpl.Name.Should().Be("odeme_hatirlatma");

        var msg = db.WaMessages.Single();
        msg.Type.Should().Be("template");
        msg.TemplateName.Should().Be("odeme_hatirlatma");
    }

    [Fact]
    public async Task Send_creates_conversation_when_missing()
    {
        var (db, svc, _, licenseId) = Build();
        var tpl = new WhatsAppTemplate("hosgeldin", "tr", Array.Empty<string>());

        await svc.SendTemplateAsync(licenseId, "+90 532 999 88 77", tpl, "panel", default);

        var convo = db.WaConversations.Single();
        convo.CustomerPhone.Should().Be("905329998877");
        convo.LicenseId.Should().Be(licenseId);
        convo.PhoneNumberId.Should().Be("pnid-1");
    }

    // ── Pencere kapalıyken şablona düşme ──

    [Fact]
    public async Task SendWithFallback_uses_template_when_window_closed()
    {
        // Prodda asıl yol bu: müşteri son 24 saatte yazmadıysa serbest metin
        // Meta tarafından reddediliyor, gönderim ancak onaylı şablonla olur.
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddDays(-3));
        var tpl = new WhatsAppTemplate("odeme_hatirlatma", "tr", new[] { "Ayşe", "250,00" });

        var result = await svc.SendWithFallbackAsync(
            licenseId, "905321234567", "serbest metin", tpl, "wpf-payment", default);

        result.Ok.Should().BeTrue();
        sender.Texts.Should().BeEmpty();
        sender.Templates.Should().ContainSingle();
        sender.Templates[0].Tpl.Name.Should().Be("odeme_hatirlatma");

        var msg = db.WaMessages.Single();
        msg.Type.Should().Be("template");
        msg.TemplateName.Should().Be("odeme_hatirlatma");
        // Onaylı gövde Meta'da; burada yalnız hangi değerlerle gittiği duruyor.
        msg.Body.Should().Be("Ayşe | 250,00");
    }

    [Fact]
    public async Task SendWithFallback_prefers_free_text_while_window_is_open()
    {
        // Pencere açıkken serbest metin ÜCRETSİZ servis mesajı; şablona düşmek
        // gereksiz yere para harcamak olurdu.
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddHours(-2));
        var tpl = new WhatsAppTemplate("odeme_hatirlatma", "tr", new[] { "Ayşe" });

        var result = await svc.SendWithFallbackAsync(
            licenseId, "905321234567", "serbest metin", tpl, "wpf-payment", default);

        result.Ok.Should().BeTrue();
        sender.Templates.Should().BeEmpty();
        sender.Texts.Should().ContainSingle();
        db.WaMessages.Single().Type.Should().Be("text");
    }

    [Fact]
    public async Task SendWithFallback_without_template_keeps_window_closed_behaviour()
    {
        // Şablon göndermeyen eski WPF sürümleri eski cevabı almalı — yoksa
        // wa.me'ye düşemez ve müşteriye hiçbir şey ulaşmaz.
        var (db, svc, sender, licenseId) = Build();
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddDays(-3));

        var result = await svc.SendWithFallbackAsync(
            licenseId, "905321234567", "serbest metin", null, "wpf-payment", default);

        result.ErrorCode.Should().Be(WhatsAppMessagingService.ErrWindowClosed);
        sender.Texts.Should().BeEmpty();
        sender.Templates.Should().BeEmpty();
        db.WaMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendWithFallback_creates_exactly_one_conversation_for_a_new_phone()
    {
        // Hiç yazışmamış bir numarada pencere kapalıdır ve sohbet satırı da
        // yoktur. Karar "önce SendTextAsync dene, window_closed görürsen
        // SendTemplateAsync çağır" diye kurulsaydı sohbet İKİ KEZ çözülürdü:
        // ilk çağrının kaydedilmemiş Add'i ikinci çağrının DB sorgusunda
        // görünmez ve aynı (lisans, numara) için ikinci satır eklenirdi.
        var (db, svc, sender, licenseId) = Build();
        var tpl = new WhatsAppTemplate("odeme_hatirlatma", "tr", new[] { "Ayşe" });

        var result = await svc.SendWithFallbackAsync(
            licenseId, "+90 532 999 88 77", "serbest metin", tpl, "wpf-payment", default);

        result.Ok.Should().BeTrue();
        sender.Templates.Should().ContainSingle();
        db.WaConversations.Should().ContainSingle()
            .Which.CustomerPhone.Should().Be("905329998877");
    }

    [Fact]
    public async Task SendText_rejects_blank_phone_and_body()
    {
        var (_, svc, sender, licenseId) = Build();

        (await svc.SendTextAsync(licenseId, "   ", "merhaba", "panel", default))
            .ErrorCode.Should().Be("bad_phone");
        (await svc.SendTextAsync(licenseId, "905321234567", "  ", "panel", default))
            .ErrorCode.Should().Be("empty_body");
        sender.Texts.Should().BeEmpty();
    }

    /// <summary>Gönderim tam sürerken çağıranın token'ını iptal eder ve kendi
    /// aldığı token'ın da iptal edilip edilmediğine bakar.</summary>
    private sealed class CallerCancellingSender : IWhatsAppSender
    {
        public CancellationTokenSource? CallerCts { get; set; }
        public bool SawCancelledToken { get; private set; }

        private Task<WhatsAppSendResult> Send(CancellationToken ct)
        {
            // İstemci tam bu anda koptu → ASP.NET Core RequestAborted'ı iptal eder.
            CallerCts!.Cancel();
            SawCancelledToken = ct.IsCancellationRequested;
            // Çağıranın token'ı taşınıyorsa Graph POST'u burada yarıda kesilir:
            // Meta mesajı çoktan kabul etmiş (faturalamış) olabilir, biz hiç
            // WaMessage yazmayız.
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WhatsAppSendResult.Success("wamid.NOTCANCELLED"));
        }

        public Task<WhatsAppSendResult> SendTextAsync(
            WhatsAppSendContext ctx, string to, string text, CancellationToken ct = default) => Send(ct);

        public Task<WhatsAppSendResult> SendTemplateAsync(
            WhatsAppSendContext ctx, string to, WhatsAppTemplate tpl, CancellationToken ct = default) => Send(ct);
    }

    private static (LicenseDbContext Db, WhatsAppMessagingService Svc, Guid LicenseId) BuildWithSender(
        IWhatsAppSender sender)
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wa-{Guid.NewGuid():N}").Options);
        var licenseId = Guid.NewGuid();
        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = "pnid-1",
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("secret-token"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        var svc = new WhatsAppMessagingService(
            db, sender, accounts, NullLogger<WhatsAppMessagingService>.Instance);
        return (db, svc, licenseId);
    }

    [Fact]
    public async Task SendText_does_not_hand_the_callers_token_to_graph()
    {
        // İstemci koptuğunda (WPF'in 10 sn'lik HttpClient timeout'u →
        // RequestAborted) Graph POST'u bilinmeyen bir noktada kesiliyordu: Meta
        // mesajı kabul edip faturalamış olabilir ve elimizde hiç kayıt kalmaz.
        // "Sonucu bilemiyoruz" hâllerinin kök nedeni buydu — çağrının kendi
        // bütçesi olmalı, çağıranın iptali onu etkilememeli.
        var sender = new CallerCancellingSender();
        var (db, svc, licenseId) = BuildWithSender(sender);
        using var _db = db;
        SeedConversation(db, licenseId, "905321234567", DateTimeOffset.UtcNow.AddMinutes(-5));

        using var callerCts = new CancellationTokenSource();
        sender.CallerCts = callerCts;

        var result = await svc.SendTextAsync(
            licenseId, "905321234567", "merhaba", "panel", callerCts.Token);

        sender.SawCancelledToken.Should().BeFalse();
        result.Ok.Should().BeTrue();
        // Sonuç kaydedilebilir kaldı — asıl kazanç bu.
        db.WaMessages.Single().WamId.Should().Be("wamid.NOTCANCELLED");
    }

    [Fact]
    public async Task SendTemplate_does_not_hand_the_callers_token_to_graph()
    {
        // Template gönderimi business-initiated ve ÜCRETLİ; yarıda kesilmesi
        // serbest metinden de pahalı.
        var sender = new CallerCancellingSender();
        var (db, svc, licenseId) = BuildWithSender(sender);
        using var _db = db;

        using var callerCts = new CancellationTokenSource();
        sender.CallerCts = callerCts;

        var result = await svc.SendTemplateAsync(
            licenseId, "905321234567",
            new WhatsAppTemplate("odeme_hatirlatma", "tr", Array.Empty<string>()),
            "panel", callerCts.Token);

        sender.SawCancelledToken.Should().BeFalse();
        result.Ok.Should().BeTrue();
    }
}

public sealed class WhatsAppAccountServiceTests
{
    private static WhatsAppAccountService Build(LicenseDbContext db, WhatsAppOptions? opt = null) =>
        new(db, new EphemeralDataProtectionProvider(), Options.Create(opt ?? new WhatsAppOptions()));

    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"waacc-{Guid.NewGuid():N}").Options);

    [Fact]
    public void Token_roundtrips_through_protector()
    {
        var svc = Build(NewDb());
        var sealedToken = svc.ProtectToken("EAAG-secret");

        sealedToken.Should().NotContain("EAAG-secret");
        svc.TryUnprotectToken(sealedToken).Should().Be("EAAG-secret");
    }

    [Fact]
    public void TryUnprotect_returns_null_for_garbage_instead_of_throwing()
    {
        Build(NewDb()).TryUnprotectToken("not-a-real-payload").Should().BeNull();
    }

    [Fact]
    public async Task ResolveSendContext_falls_back_to_config_default_when_no_account()
    {
        var svc = Build(NewDb(), new WhatsAppOptions
        {
            DefaultPhoneNumberId = "pnid-default",
            DefaultAccessToken = "token-default",
        });

        var ctx = await svc.ResolveSendContextAsync(Guid.NewGuid(), default);

        ctx.Should().NotBeNull();
        ctx!.PhoneNumberId.Should().Be("pnid-default");
        ctx.AccessToken.Should().Be("token-default");
    }

    [Fact]
    public async Task ResolveSendContext_null_when_nothing_configured()
    {
        (await Build(NewDb()).ResolveSendContextAsync(Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task GetByPhoneNumberId_routes_webhook_to_the_right_tenant()
    {
        var db = NewDb();
        var svc = Build(db);
        var mine = Guid.NewGuid();
        db.WhatsAppAccounts.AddRange(
            new WhatsAppAccount
            {
                Id = Guid.NewGuid(), LicenseId = mine, WabaId = "w1", PhoneNumberId = "pnid-A",
                DisplayPhoneNumber = "+901", AccessTokenProtected = svc.ProtectToken("t1"),
                Status = "active", ConnectedAt = DateTimeOffset.UtcNow,
            },
            new WhatsAppAccount
            {
                Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), WabaId = "w2", PhoneNumberId = "pnid-B",
                DisplayPhoneNumber = "+902", AccessTokenProtected = svc.ProtectToken("t2"),
                Status = "active", ConnectedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var found = await svc.GetByPhoneNumberIdAsync("pnid-A", default);

        found.Should().NotBeNull();
        found!.LicenseId.Should().Be(mine);
    }
}
