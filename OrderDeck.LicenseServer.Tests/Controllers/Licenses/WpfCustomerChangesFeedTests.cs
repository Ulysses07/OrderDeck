using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>rowversion yalnız gerçek SQL Server'da üretilir; InMemory'de
/// ChangeSeq hep 0. Canlı yayın sırasında ÇALIŞTIRILMAZ.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class WpfCustomerChangesFeedTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public WpfCustomerChangesFeedTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private sealed record Item(Guid Id, string Platform, string Username, Guid? MergedIntoId,
        DateTimeOffset? PurgedAt, string? City, string? Tckn, long ChangeSeq);
    private sealed record Page(List<Item> Items, long NextAfterSeq);

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        return (client, await AddLicenseAsync(customerId));
    }

    private async Task<Guid> AddLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = $"LDK-CHG-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private static async Task PushAsync(HttpClient client, Guid licenseId, object item)
        => (await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
                new { customers = new[] { item } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private static async Task<Page> GetPageAsync(HttpClient client, Guid licenseId, long afterSeq, int take = 100)
        => (await client.GetFromJsonAsync<Page>(
               $"/api/v1/licenses/{licenseId}/wpf-customers/changes?afterSeq={afterSeq}&take={take}"))!;

    [Fact]
    public async Task Gec_gelen_eski_tarihli_satir_da_akista_gorunur()
    {
        var (client, licenseId) = await SetupAsync();
        var first = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = first, platform = "tiktok", username = "ilk", updatedAt = DateTimeOffset.UtcNow, format = 2 } } });

        var page1 = await client.GetFromJsonAsync<Page>($"/api/v1/licenses/{licenseId}/wpf-customers/changes?afterSeq=0&take=100");
        page1!.Items.Select(i => i.Id).Should().Contain(first);

        // İstemci saatine göre çok ESKİ bir satır, imleç ilerledikten sonra geliyor.
        var late = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = late, platform = "tiktok", username = "gec", updatedAt = DateTimeOffset.UtcNow.AddYears(-1), format = 2 } } });

        var page2 = await client.GetFromJsonAsync<Page>($"/api/v1/licenses/{licenseId}/wpf-customers/changes?afterSeq={page1.NextAfterSeq}&take=100");
        page2!.Items.Select(i => i.Id).Should().Equal(late);
    }

    [Fact]
    public async Task Akis_TCKN_yi_duz_dondurur()
    {
        var (client, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        var tc = TestTckn.NewValid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id, platform = "tiktok", username = "akis-tckn", updatedAt = DateTimeOffset.UtcNow, format = 2, tckn = tc, tcknChangedAt = DateTimeOffset.UtcNow } } });

        var page = await client.GetFromJsonAsync<Page>($"/api/v1/licenses/{licenseId}/wpf-customers/changes?afterSeq=0&take=100");
        page!.Items.Single(i => i.Id == id).Tckn.Should().Be(tc);
    }

    [Fact]
    public async Task Kopya_yonlendirme_olarak_akista_gorunur()
    {
        var (client, licenseId) = await SetupAsync();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = a, platform = "tiktok", username = "ayse", updatedAt = DateTimeOffset.UtcNow, format = 2 } } });
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = b, platform = "tiktok", username = "Ayse", updatedAt = DateTimeOffset.UtcNow, format = 2 } } });

        var page = await client.GetFromJsonAsync<Page>($"/api/v1/licenses/{licenseId}/wpf-customers/changes?afterSeq=0&take=100");
        page!.Items.Should().Contain(i => i.Id == b && i.MergedIntoId == a);
    }

    /// <summary>
    /// Akış kopyaları görmek için sorgu filtresini kaldırıyor
    /// (IgnoreQueryFilters); lisans sınırı yalnız açık LicenseId koşulunda.
    /// Aynı yayıncının İKİNCİ lisansı sahiplik denetiminden geçer — onun aynı
    /// kullanıcı adlı satırı bile bu lisansın akışına sızmamalı. Başka
    /// yayıncının lisansı 404.
    /// </summary>
    [Fact]
    public async Task Akis_baska_lisansin_satirlarini_dondurmez()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = await AddLicenseAsync(customerId);
        var siblingLicenseId = await AddLicenseAsync(customerId);
        var (otherClient, otherLicenseId) = await SetupAsync();

        var mine = Guid.NewGuid(); var sibling = Guid.NewGuid(); var theirs = Guid.NewGuid();
        await PushAsync(client, licenseId,
            new { id = mine, platform = "tiktok", username = "ayni-kisi", updatedAt = DateTimeOffset.UtcNow, format = 2 });
        await PushAsync(client, siblingLicenseId,
            new { id = sibling, platform = "tiktok", username = "ayni-kisi", updatedAt = DateTimeOffset.UtcNow, format = 2 });
        await PushAsync(otherClient, otherLicenseId,
            new { id = theirs, platform = "tiktok", username = "ayni-kisi", updatedAt = DateTimeOffset.UtcNow, format = 2 });

        (await GetPageAsync(client, licenseId, afterSeq: 0)).Items.Select(i => i.Id).Should().Equal(mine);

        (await client.GetAsync($"/api/v1/licenses/{otherLicenseId}/wpf-customers/changes?afterSeq=0"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// KVKK: silme, satırı ZATEN çekmiş bilgisayarlara da inmeli. Silme bir
    /// yazımdır, rowversion'ı ilerletir; satır imlecin ilerisinde PurgedAt
    /// dolu, kişisel alanları boş olarak yeniden görünür. Akış silineni
    /// eleseydi silme yayıncının diskinde kalırdı (eski since ucuyla aynı
    /// sözleşme).
    /// </summary>
    [Fact]
    public async Task Silinen_satir_imlecin_ilerisinde_PurgedAt_ile_yeniden_iner()
    {
        var (client, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await PushAsync(client, licenseId, new
        {
            id, platform = "tiktok", username = "silinecek", updatedAt = now, format = 2,
            city = "İzmir", addressChangedAt = now, tckn = TestTckn.NewValid(), tcknChangedAt = now,
        });
        var page1 = await GetPageAsync(client, licenseId, afterSeq: 0);
        page1.Items.Single(i => i.Id == id).City.Should().Be("İzmir");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = await db.WpfCustomerProjections.SingleAsync(p => p.Id == id);
            row.MarkPurged(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var page2 = await GetPageAsync(client, licenseId, page1.NextAfterSeq);
        var purged = page2.Items.Should().ContainSingle(i => i.Id == id).Which;
        purged.PurgedAt.Should().NotBeNull("yerel kopyayı temizletecek tek işaret bu");
        purged.City.Should().BeNull();
        purged.Tckn.Should().BeNull();
        purged.Username.Should().Be("silinecek", "WPF eşleşmeyi (platform, kullanıcı adı) ile yapıyor");
    }

    /// <summary>
    /// Sayfalar rowversion sırasıyla, kayıpsız iner; imleç sayfanın son
    /// satırı. Boş sayfa imleci olduğu yerde bırakır — geri alsa istemci her
    /// turda her şeyi yeniden indirirdi.
    /// </summary>
    [Fact]
    public async Task Sayfalar_yazim_sirasiyla_kayipsiz_iner_bos_sayfa_imleci_korur()
    {
        var (client, licenseId) = await SetupAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await PushAsync(client, licenseId,
                new { id, platform = "tiktok", username = $"sira{i}", updatedAt = DateTimeOffset.UtcNow, format = 2 });
        }

        var seen = new List<Guid>();
        var after = 0L;
        for (var i = 0; i < ids.Count; i++)
        {
            var page = await GetPageAsync(client, licenseId, after, take: 1);
            var item = page.Items.Should().ContainSingle().Which;
            page.NextAfterSeq.Should().Be(item.ChangeSeq).And.BeGreaterThan(after);
            seen.Add(item.Id);
            after = page.NextAfterSeq;
        }
        seen.Should().Equal(ids, "her gönderim kendi işleminde daha büyük bir rowversion alır");

        var empty = await GetPageAsync(client, licenseId, after, take: 1);
        empty.Items.Should().BeEmpty();
        empty.NextAfterSeq.Should().Be(after);
    }

    /// <summary>
    /// Ufuk: commit olmamış bir yazım daha KÜÇÜK bir rowversion almış olabilir.
    /// Ondan büyük, commit olmuş satır o işlem bitene kadar verilmez — verilse
    /// imleç onu geçer, uçuştaki satır commit olunca imlecin gerisinde kalıp
    /// hiç inmezdi.
    ///
    /// <para>MIN_ACTIVE_ROWVERSION() veritabanı geneli: rowversion kolonu olan
    /// HERHANGİ bir tabloya açık yazım ufku tutar. Uçuştaki yazım bu yüzden
    /// BarcodeCounters'a yapılıyor — projeksiyon satırına yapılsaydı okuyucu
    /// (kilitli READ COMMITTED) o satırın kilidinde commit'i bekler, test
    /// ufku değil kilidi sınardı.</para>
    /// </summary>
    [Fact]
    public async Task Ucustaki_yazimdan_sonra_commit_olan_satir_islem_bitene_kadar_verilmez()
    {
        var (client, licenseId) = await SetupAsync();
        await PushAsync(client, licenseId,
            new { id = Guid.NewGuid(), platform = "tiktok", username = "once", updatedAt = DateTimeOffset.UtcNow, format = 2 });
        var cursor = (await GetPageAsync(client, licenseId, afterSeq: 0)).NextAfterSeq;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        await using var inFlight = conn.BeginTransaction();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = inFlight;
            cmd.CommandText = "INSERT INTO BarcodeCounters (LicenseId, Next) VALUES (@id, 1)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync();
        }

        var later = Guid.NewGuid();
        await PushAsync(client, licenseId,
            new { id = later, platform = "tiktok", username = "sonra", updatedAt = DateTimeOffset.UtcNow, format = 2 });

        var held = await GetPageAsync(client, licenseId, cursor);
        held.Items.Should().BeEmpty("açık işlemin rowversion'ı daha küçük; ufuk onun altında kalır");
        held.NextAfterSeq.Should().Be(cursor);

        await inFlight.CommitAsync();

        (await GetPageAsync(client, licenseId, cursor)).Items.Select(i => i.Id).Should().Equal(later);
    }
}
