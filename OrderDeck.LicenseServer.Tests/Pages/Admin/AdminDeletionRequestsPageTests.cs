using System.Net;
using AngleSharp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>Müşteri silme talepleri sayfası (admin/shoppers/deletion-requests). Listede son tarih (talep + 30 gün, KVKK m. 13/2)
/// ve gecikme işareti.</summary>
public sealed class AdminDeletionRequestsPageTests : IClassFixture<ApiFactory>
{
    private const string PagePath = "/admin/shoppers/deletion-requests";

    private readonly ApiFactory _factory;
    public AdminDeletionRequestsPageTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Satirin_Sil_formu_silme_isleyicisine_gider_silme_yapilir_liste_sayfasina_donulur()
    {
        // Sayfa numarası "page" adıyla taşınırken (Razor Pages'in ayrılmış rota değeri, sayfanın adı) satırın formu action=""
        // ile çıkıyordu: "Sil" sayfayı yeniden yüklüyor, silme hiç yapılmıyordu (KVKK m. 7, K-7).
        var (shopperId, requestId) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var resp = await PurgeViaFormAsync(client, requestId);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.OriginalString.Should().StartWith(PagePath);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == shopperId)).FullName.Should().Be("[Silindi]");
        (await db.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == requestId)).HandledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Silmenin_imha_kaydinda_yapan_talebi_isleyen_yoneticinin_kimligi_talep_kimligi_de_yazilir()
    {
        // KVKK Ö-13/Ö-14 (Görev 9 kuralı): imha kaydı budanmaz; silmeyi bir yönetici elle işler, kayıt "system" demez. Yapan:
        // yöneticinin sub kimliği (DataDestruction.ActorOf), kullanıcı adı değil (adı talebin HandledBy'ında ve denetim satırında).
        // Ö-13'ün alan listesindeki talep kimliği de yazılır: süresiz kayıt başvuruya birleştirme olmadan bağlanır.
        var (shopperId, requestId) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        (await PurgeViaFormAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var adminId = (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id;
        (await db.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == shopperId).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(r => r.Trigger == DataDestruction.Triggers.ShopperPurge
                && r.Actor == adminId.ToString() && r.RequestId == requestId);
    }

    [Fact]
    public async Task Bekleyen_talepte_son_tarih_gosterilir_gecikeni_isaretlenir()
    {
        var late = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-31) };
        var fresh = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperDeletionRequests.AddRange(late, fresh);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var html = await client.GetStringAsync(PagePath);
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        doc.QuerySelector($"tr[data-request='{late.Id}'] [data-cell='due']")!.TextContent.Should()
            .Contain(late.RequestedAt.AddDays(30).ToString("dd.MM.yyyy")).And.Contain("gecikti");
        doc.QuerySelector($"tr[data-request='{fresh.Id}'] [data-cell='due']")!.TextContent.Should().NotContain("gecikti");
    }

    [Fact]
    public async Task Islenmis_talepte_bosaltilmis_telefon_silindi_diye_gosterilir_satir_kalir()
    {
        // KVKK D-5: işlenmiş talebin telefonu 30 gün sonra boşalır (SecurityDataRetentionJob); talebin satırı kanıt olarak kalır,
        // listede boş hücre değil "(silindi)" görünür.
        var now = DateTimeOffset.UtcNow;
        var cleared = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = "",
            RequestedAt = now, HandledAt = now.AddDays(-31), HandledBy = "admin" };
        var kept = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = now, HandledAt = now.AddDays(-1), HandledBy = "admin" };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperDeletionRequests.AddRange(cleared, kept);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var html = await client.GetStringAsync(PagePath + "?handled=true");
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        doc.QuerySelector($"tr[data-request='{cleared.Id}'] [data-cell='phone']")!.TextContent.Trim().Should().Be("(silindi)");
        doc.QuerySelector($"tr[data-request='{cleared.Id}'] [data-cell='due']").Should().NotBeNull();
        doc.QuerySelector($"tr[data-request='{kept.Id}'] [data-cell='phone']")!.TextContent.Trim().Should().Be(kept.PhoneAtRequest);
    }

    private async Task<(Guid ShopperId, Guid RequestId)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ornek Musteri", Phone = TestPhone.NewE164(), PasswordHash = $"h-{Guid.NewGuid():N}",
            Address = "-", CreatedAt = now, UpdatedAt = now,
        };
        var request = new ShopperDeletionRequest
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, PhoneAtRequest = shopper.Phone, RequestedAt = now,
        };
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Shoppers.Add(shopper);
        db.ShopperDeletionRequests.Add(request);
        await db.SaveChangesAsync();
        return (shopper.Id, request.Id);
    }

    /// <summary>Tarayıcının yaptığı: listeyi açar, talebin satırındaki "Sil" formunun action'ına jetonla gönderir.</summary>
    private static async Task<HttpResponseMessage> PurgeViaFormAsync(HttpClient client, Guid requestId)
    {
        var html = await client.GetStringAsync(PagePath);
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));
        var actions = doc.QuerySelectorAll("form").Select(f => f.GetAttribute("action") ?? "").ToList();
        var action = actions.Where(a => a.Contains("handler=Purge") && a.Contains(requestId.ToString())).Should()
            .ContainSingle($"satırın formu silme işleyicisine gitmeli; formlar: {string.Join(" | ", actions)}").Subject;
        var token = AdminLoginHelper.ExtractAntiForgeryToken(html);
        return await client.PostAsync(action,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
    }
}
