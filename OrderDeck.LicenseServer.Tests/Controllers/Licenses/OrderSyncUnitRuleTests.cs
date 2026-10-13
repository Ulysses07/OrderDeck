using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>orders/sync</c> güncellemesinin birim kuralı, bugünkü (v1, ≤0.9.10) istemcinin gözünden — spec 2026-10-13
/// §4.2 K1–K3. Kural moddan bağımsızdır; bu testler mod Off'ta (varsayılan) koşar. Kuralın kendisi ortak
/// vektörlerde (<c>LabelRules.vectors.json</c>, <c>orderUnitMerge</c>); burada ucun onu gerçekten uyguladığı
/// sabitlenir.
/// </summary>
public sealed class OrderSyncUnitRuleTests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset AddedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;

    public OrderSyncUnitRuleTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// K1: oluşum alanları yalnız eklemede yazılır. Bugün güncelleme SessionId, MessageText, Code, IsShippingFee,
    /// IsBackupPromoted, ProductId ve ProductVariantId'yi pakettekiyle eziyordu; bayat ya da katalog bilmeyen bir
    /// gönderim siparişi başka yayına taşıyabiliyor, ürün bağını silebiliyordu. Birim (fiyat) yine yazılır.
    /// </summary>
    [Fact]
    public async Task Guncelleme_olusum_alanlarini_yazmaz()
    {
        var (client, licenseId) = await SetupAsync();
        var firstSession = await SeedSessionAsync(licenseId);
        var otherSession = await SeedSessionAsync(licenseId);
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var original = NewItem(firstSession) with { ProductId = productId, ProductVariantId = variantId };
        (await PushAsync(client, licenseId, original)).StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await PushAsync(client, licenseId, original with
        {
            SessionId = otherSession,
            CustomerId = Guid.NewGuid().ToString("N"),
            Platform = "tiktok",
            Username = "baska-izleyici",
            DisplayName = "Başka",
            MessageText = "B2 L",
            Code = "B2",
            AddedAt = AddedAt.AddHours(1),
            IsShippingFee = true,
            IsBackupPromoted = true,
            ProductId = Guid.NewGuid(),
            ProductVariantId = null,
            Price = 150m,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await LoadAsync(original.Id);
        saved.SessionId.Should().Be(firstSession, "sipariş başka yayına taşınamaz");
        saved.CustomerId.Should().Be(original.CustomerId);
        saved.Platform.Should().Be(original.Platform);
        saved.Username.Should().Be(original.Username);
        saved.DisplayName.Should().Be(original.DisplayName);
        saved.MessageText.Should().Be(original.MessageText);
        saved.Code.Should().Be(original.Code);
        saved.AddedAt.Should().Be(original.AddedAt);
        saved.IsShippingFee.Should().BeFalse();
        saved.IsBackupPromoted.Should().BeFalse();
        saved.ProductId.Should().Be(productId);
        saved.ProductVariantId.Should().Be(variantId);
        saved.Price.Should().Be(150m, "fiyat birimi damgasız: son gelen kazanır");
    }

    /// <summary>
    /// K3: PrintedAt hiçbir yoldan silinmez (yerelde de: 048 yeniden baskısı dokunmaz). Bugün baskısız bayat bir
    /// gönderim basılmış siparişi kuyruğa geri döndürüyordu. v1 baskı grubunu taşımaz; sunucudaki grup da kalır.
    /// </summary>
    [Fact]
    public async Task Baski_bos_gonderimle_silinmez()
    {
        var (client, licenseId) = await SetupAsync();
        var printedAt = AddedAt.AddMinutes(5);
        var item = NewItem(null) with { PrintedAt = printedAt };
        (await PushAsync(client, licenseId, item)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SeedAsync(item.Id, o => { o.PrintBatchId = "b-" + Guid.NewGuid().ToString("N")[..8]; o.PrintSeq = 3; });
        var seeded = await LoadAsync(item.Id);

        (await PushAsync(client, licenseId, item with { PrintedAt = null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await LoadAsync(item.Id);
        saved.PrintedAt.Should().Be(printedAt);
        saved.PrintBatchId.Should().Be(seeded.PrintBatchId);
        saved.PrintSeq.Should().Be(3);
    }

    /// <summary>K3: basılmış siparişin PrintedAt'i sonraki bir değerle kaymaz; ilk baskı anı kalır.</summary>
    [Fact]
    public async Task Baski_sonraki_degerle_kaymaz()
    {
        var (client, licenseId) = await SetupAsync();
        var printedAt = AddedAt.AddMinutes(5);
        var item = NewItem(null) with { PrintedAt = printedAt };
        (await PushAsync(client, licenseId, item)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PushAsync(client, licenseId, item with { PrintedAt = printedAt.AddHours(2) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoadAsync(item.Id)).PrintedAt.Should().Be(printedAt);
    }

    /// <summary>
    /// K2: yedek birimi tek yönlüdür — IsTentativeBackup yalnız 1'den 0'a gider (onay). Masaüstünde 0'dan 1'e
    /// giden güncelleme yok; böyle bir gönderim ancak bayattır ve onaylanmış yedeği toplamlardan düşürürdü.
    /// </summary>
    [Fact]
    public async Task Gecici_yedek_bayragi_yalniz_birden_sifira_gider()
    {
        var (client, licenseId) = await SetupAsync();
        var confirmed = NewItem(null);
        var tentative = NewItem(null) with { IsTentativeBackup = true };
        (await PushAsync(client, licenseId, confirmed, tentative)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PushAsync(client, licenseId,
                confirmed with { IsTentativeBackup = true },
                tentative with { IsTentativeBackup = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoadAsync(confirmed.Id)).IsTentativeBackup.Should().BeFalse("0'dan 1'e dönüş yok sayılır");
        (await LoadAsync(tentative.Id)).IsTentativeBackup.Should().BeFalse("onay (1'den 0'a) yazılır");
    }

    /// <summary>
    /// K2: komutun damgaladığı birimi v1 gönderimi değiştiremez (damgasız birimde bugünkü son-gelen-kazanır sürer;
    /// bkz. <see cref="Guncelleme_olusum_alanlarini_yazmaz"/>). Damga S2'nin komutlarıyla gelir; burada elle
    /// basılır.
    /// </summary>
    [Fact]
    public async Task Damgali_fiyati_v1_gonderimi_degistiremez()
    {
        var (client, licenseId) = await SetupAsync();
        var item = NewItem(null) with { PrintedAt = AddedAt.AddMinutes(5) };
        (await PushAsync(client, licenseId, item)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SeedAsync(item.Id, o => { o.Price = 80m; o.PriceSetAt = DateTimeOffset.UtcNow; });

        (await PushAsync(client, licenseId, item with { Price = 100m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoadAsync(item.Id)).Price.Should().Be(80m);
    }

    /// <summary>
    /// K3: şu anda komutla iptal edilmiş siparişe (CancelledAt ∧ CancelSetAt) push'tan baskı alınmaz — kâğıt imha
    /// edilmeli (<c>mark-printed</c>'ın "iptal edilmemiş" koşuluyla aynı sonuç). Komutla iptal edilip komutla geri
    /// alınmış siparişin (CancelSetAt dolu, CancelledAt boş) baskısı alınır; aksi hâlde etiket hiç basılmış
    /// sayılmaz, kuyruğa döner ve döngü bitmezdi (§15-40).
    /// </summary>
    [Fact]
    public async Task Komutla_iptal_edilmis_siparise_baski_alinmaz_geri_alinmisa_alinir()
    {
        var (client, licenseId) = await SetupAsync();
        var cancelled = NewItem(null);
        var uncancelled = NewItem(null);
        (await PushAsync(client, licenseId, cancelled, uncancelled)).StatusCode.Should().Be(HttpStatusCode.OK);
        var commandAt = DateTimeOffset.UtcNow;
        await SeedAsync(cancelled.Id, o =>
        {
            o.CancelledAt = commandAt;
            o.CancelReason = "komut";
            o.CancelSetAt = commandAt;
        });
        await SeedAsync(uncancelled.Id, o => o.CancelSetAt = commandAt);

        var printedAt = AddedAt.AddMinutes(5);
        (await PushAsync(client, licenseId,
                cancelled with { PrintedAt = printedAt },
                uncancelled with { PrintedAt = printedAt }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var a = await LoadAsync(cancelled.Id);
        a.PrintedAt.Should().BeNull("şu anda komutla iptal edilmiş");
        a.CancelledAt.Should().Be(commandAt, "damgalı iptali v1 geri alamaz");
        (await LoadAsync(uncancelled.Id)).PrintedAt.Should().Be(printedAt, "komutla geri alınmış");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>Sunucu satırını elle değiştirir (komutların S2'de basacağı damgalar gibi).</summary>
    private async Task SeedAsync(Guid orderId, Action<Order> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        change(await db.Orders.SingleAsync(o => o.Id == orderId));
        await db.SaveChangesAsync();
    }

    /// <summary>v1 (≤0.9.10) sipariş öğesi; telde camelCase.</summary>
    private sealed record V1Item(
        Guid Id,
        Guid? SessionId,
        string CustomerId,
        string Platform,
        string Username,
        string? DisplayName,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        string? CancelReason,
        bool IsShippingFee,
        bool IsBackupPromoted,
        bool IsTentativeBackup,
        Guid? ProductId = null,
        Guid? ProductVariantId = null);

    private static V1Item NewItem(Guid? sessionId) => new(
        Id: Guid.NewGuid(),
        SessionId: sessionId,
        CustomerId: Guid.NewGuid().ToString("N"),
        Platform: "youtube",
        Username: "izleyici",
        DisplayName: "İzleyici",
        MessageText: "A1 M",
        Code: "A1",
        Price: 100m,
        AddedAt: AddedAt,
        PrintedAt: null,
        CancelledAt: null,
        CancelReason: null,
        IsShippingFee: false,
        IsBackupPromoted: false,
        IsTentativeBackup: false);

    private static Task<HttpResponseMessage> PushAsync(HttpClient client, Guid licenseId, params V1Item[] items)
        => client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync", new { orders = items });

    private async Task<Order> LoadAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-BIRIM-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private async Task<Guid> SeedSessionAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Yayın",
            StartedAt = AddedAt.AddHours(-1),
            Platforms = "youtube",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }
}
