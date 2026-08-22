using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Aynı sohbete ait iki webhook paketi eşzamanlı işlendiğinde sohbet
/// toplamlarının kaybolmadığını doğrular.
///
/// <para><b>Kusur neydi:</b> <c>UnreadCount++</c> ve "en yeni zaman damgası"
/// izlenen varlık üzerinde güncelleniyordu; EF bunu
/// <c>SET UnreadCount = &lt;okunan+1&gt;</c> diye yazar. Meta aynı müşteriden arka
/// arkaya paket gönderebiliyor ve Hangfire bunları paralel çalışanlarda işliyor
/// — ikisi de aynı değeri okuyup birbirinin katkısını siliyordu.</para>
///
/// <para><b>Neden önemli:</b> kaybolan artış panelde okunmamış rozetini eksik
/// gösterir, müşteri mesajı gözden kaçar. Geriye giden <c>LastInboundAt</c> ise
/// daha pahalı: 24 saatlik hizmet penceresi erken kapanır ve sunucu serbest
/// metin hâlâ mümkünken <b>ücretli</b> şablona düşer.</para>
///
/// <para><b>Neden gerçek SQL Server:</b> düzeltme tek atomik UPDATE'e
/// (<c>ExecuteUpdate</c>) dayanıyor; InMemory sağlayıcısında ne o var ne de
/// eşzamanlılık semantiği.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class WhatsAppInboundConcurrencyTests : IAsyncLifetime
{
    private const string Pnid = "PNID_CONC_1";
    private const string CustomerPhone = "905321234567";
    private const int ParallelWorkers = 6;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public WhatsAppInboundConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ic_ice_gecen_iki_paket_okunmamis_sayacini_kaybetmez()
    {
        var licenseId = await SeedAccountAsync();
        var convoId = await SeedConversationAsync(licenseId);

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();

        // İki çalışan da sohbeti KARŞI TARAF yazmadan önce okumuş olsun —
        // sahadaki iç içe geçme tam olarak bu. Önceden okunan satır her iki
        // işin de değişiklik izleyicisinde bayat kalıyor.
        await LoadConversationAsync(scopeA, convoId);
        await LoadConversationAsync(scopeB, convoId);

        await Job(scopeA).ProcessAsync(TextPayload("wamid.CONC.A", ts: 1753440000));
        await Job(scopeB).ProcessAsync(TextPayload("wamid.CONC.B", ts: 1753440060));

        var convo = await ReadConversationAsync(convoId);
        convo.UnreadCount.Should().Be(2,
            "iki müşteri mesajı geldi; biri silinirse panel rozeti eksik sayar ve mesaj gözden kaçar");
    }

    [Fact]
    public async Task Gec_islenen_eski_paket_hizmet_penceresini_geri_almaz()
    {
        var licenseId = await SeedAccountAsync();
        var convoId = await SeedConversationAsync(licenseId);

        var older = DateTimeOffset.FromUnixTimeSeconds(1753440000);
        var newer = DateTimeOffset.FromUnixTimeSeconds(1753526400);   // +24 saat

        using var scopeNew = _factory.Services.CreateScope();
        using var scopeOld = _factory.Services.CreateScope();
        await LoadConversationAsync(scopeNew, convoId);
        await LoadConversationAsync(scopeOld, convoId);

        // Yeni paket önce işleniyor, ESKİ paket sonra: Meta'nın sırası garanti
        // değil, retry'da da eski bir olay yeniden düşebiliyor.
        await Job(scopeNew).ProcessAsync(
            TextPayload("wamid.WIN.NEW", ts: newer.ToUnixTimeSeconds()));
        await Job(scopeOld).ProcessAsync(
            TextPayload("wamid.WIN.OLD", ts: older.ToUnixTimeSeconds()));

        var convo = await ReadConversationAsync(convoId);
        convo.LastInboundAt.Should().Be(newer,
            "pencere geriye alınırsa serbest metin hâlâ mümkünken ÜCRETLİ şablona düşeriz");
        convo.LastMessageAt.Should().Be(newer,
            "liste sıralaması da geriye gitmemeli");
    }

    [Fact]
    public async Task Paralel_calisanlarin_tumunun_artisi_sayilir()
    {
        var licenseId = await SeedAccountAsync();
        var convoId = await SeedConversationAsync(licenseId);

        // Her çalışan kendi kapsamında (kendi DbContext'iyle) — Hangfire'daki
        // gibi. Her biri AYRI bir müşteri mesajı işliyor.
        var scopes = Enumerable.Range(0, ParallelWorkers)
            .Select(_ => _factory.Services.CreateScope())
            .ToArray();
        try
        {
            await Task.WhenAll(scopes.Select((s, i) =>
                Job(s).ProcessAsync(TextPayload($"wamid.PAR.{i}", ts: 1753440000 + i))));
        }
        finally
        {
            foreach (var s in scopes) s.Dispose();
        }

        var convo = await ReadConversationAsync(convoId);
        convo.UnreadCount.Should().Be(ParallelWorkers);
        convo.LastInboundAt.Should().Be(
            DateTimeOffset.FromUnixTimeSeconds(1753440000 + ParallelWorkers - 1));
        convo.Status.Should().Be("open");
    }

    // ── Yardımcılar ─────────────────────────────────────────────────────────

    private static WhatsAppInboundJob Job(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<WhatsAppInboundJob>();

    private static async Task LoadConversationAsync(IServiceScope scope, Guid convoId)
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await db.WaConversations.FirstAsync(c => c.Id == convoId);
    }

    private async Task<WaConversation> ReadConversationAsync(Guid convoId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversations.AsNoTracking().SingleAsync(c => c.Id == convoId);
    }

    private static string TextPayload(string wamId, long ts) => $$$"""
    {
      "entry": [{ "changes": [{ "field": "messages", "value": {
        "metadata": { "phone_number_id": "{{{Pnid}}}" },
        "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "{{{CustomerPhone}}}" }],
        "messages": [{ "from": "{{{CustomerPhone}}}", "id": "{{{wamId}}}", "timestamp": "{{{ts}}}",
                       "type": "text", "text": { "body": "merhaba" } }]
      }}]}]
    }
    """;

    private async Task<Guid> SeedAccountAsync()
    {
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WAI-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            WabaId = "waba-1",
            PhoneNumberId = Pnid,
            DisplayPhoneNumber = "905550000000",
            AccessTokenProtected = accounts.ProtectToken("token-1234"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary>Sohbeti önceden yazar: paralel testte N çalışanın aynı anda
    /// EKLEMEYE kalkışması (LicenseId, CustomerPhone) unique index'ine takılır
    /// ve ölçmek istediğimiz şeyi gölgeler. O yarışın sahadaki karşılığı
    /// Hangfire retry'ı, ayrı bir konu.</summary>
    private async Task<Guid> SeedConversationAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = CustomerPhone,
            PhoneNumberId = Pnid,
            Status = "closed",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.WaConversations.Add(convo);
        await db.SaveChangesAsync();
        return convo.Id;
    }
}
