using System.Net;
using AngleSharp;
using AngleSharp.Html.Dom;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Onay kutularına (bool) "true"/"false" dışında değer gelmesi. Tarayıcı
/// işaretli kutuda tag helper'ın bastığı "true"yu gönderiyor, yani müşteri bu
/// yola düşmüyor; düşen bot ya da elle kurulmuş istek. Eskiden bağlayıcı hatası
/// ModelState'te ham değerle kalıyor, sayfa yeniden çizilirken checkbox tag
/// helper o değeri bool'a çevirmeye çalışıp patlıyordu (FormatException → 500).
///
/// Beklenen: bozuk değer işaretsiz kutuyla aynı muamele görür — izin
/// verilmemiş sayılır, kutunun kendi kuralı (varsa) işler, sayfa normal satır
/// içi hatalarla döner ya da kayıt açılır. Hiçbir koşulda 500 yok.
/// </summary>
public sealed class IntakeFormMalformedCheckboxTests : IClassFixture<YouTubeIdentityFactory>
{
    private readonly YouTubeIdentityFactory _factory;
    public IntakeFormMalformedCheckboxTests(YouTubeIdentityFactory factory) => _factory = factory;

    /// <summary>Alan adı → sayfadaki kutunun id'si. YouTube kutusunun id'si
    /// tag helper'ın ürettiği değil, sayfanın JS'i için verilmiş olanı.</summary>
    private static readonly (string Field, string Id)[] Boxes =
    {
        ("Input.WhatsAppConsent", "Input_WhatsAppConsent"),
        ("Input.SmsConsent", "Input_SmsConsent"),
        ("Input.YouTubeConfirmed", "ytConfirm"),
    };

    private static readonly string[] Malformed = { "on", "1", "evet", "" };

    public static TheoryData<string, string, string> MalformedRendered()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (field, id) in Boxes)
        foreach (var value in Malformed)
            data.Add(field, id, value);
        return data;
    }

    public static TheoryData<string, string> MalformedValues()
    {
        var data = new TheoryData<string, string>();
        foreach (var (field, _) in Boxes)
        foreach (var value in Malformed)
            data.Add(field, value);
        return data;
    }

    /// <summary>
    /// Asıl çökme yolu: başka bir alan yüzünden sayfa yeniden çiziliyor ve
    /// bozuk değerli kutu da çiziliyor. Normal satır içi hata dönmeli, kutu
    /// işaretsiz gelmeli, bağlayıcının İngilizce mesajı sızmamalı.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedRendered))]
    public async Task Baska_alan_hatasinda_bozuk_deger_sayfayi_cokertmez(string field, string id, string value)
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var page = await OpenFormAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(page, slug, ("Input.Phone", ""), (field, value)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "normal satır içi doğrulama yanıtı");
        var raw = await resp.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(raw);
        html.Should().Contain("WhatsApp numarası zorunlu");
        html.Should().NotContain("is not valid", "bağlayıcının İngilizce mesajı müşteriye sızmamalı");
        html.Should().NotContain("is invalid");

        var box = (await ParseAsync(raw)).GetElementById(id) as IHtmlInputElement;
        box.Should().NotBeNull($"{field} kutusu sayfada olmalı");
        box!.IsChecked.Should().BeFalse("bozuk değer onay sayılmaz");

        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    /// <summary>
    /// Formun geri kalanı geçerliyse bozuk değer tek başına gönderimi
    /// durdurmaz — eksik alanla aynı: kayıt açılır, izin VERİLMEMİŞ yazılır.
    /// Eskiden yalnız bağlayıcı hatası ModelState'i geçersiz yapıyor, sayfa
    /// yeniden çiziliyor ve aynı 500'e düşüyordu.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedValues))]
    public async Task Gecerli_formda_bozuk_deger_isaretsiz_sayilir_kayit_acilir(string field, string value)
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var page = await OpenFormAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(page, slug, (field, value)));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var row = (await RowsAsync(customerId)).Should().ContainSingle().Subject;
        row.WhatsAppConsent.Should().BeFalse();
        row.SmsConsent.Should().BeFalse();
        (await IysEventsAsync(row.Phone!)).Should().BeFalse("bozuk değerden İYS onayı doğmamalı");
    }

    /// <summary>SMS kutusu kapalı yayıncıda kutu hiç çizilmiyor — çökme yok
    /// ama eskiden sayfa HİÇBİR hata göstermeden geri dönüyordu: müşteri
    /// neyin yanlış olduğunu göremeyeceği bir çıkmaza giriyordu.</summary>
    [Fact]
    public async Task Sms_kutusu_kapaliyken_bozuk_deger_sessiz_donuse_dusurmez()
    {
        var (slug, customerId) = await SeedConfigAsync(smsEnabled: false);
        var client = NewClient();
        var page = await OpenFormAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(page, slug, ("Input.SmsConsent", "on")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await RowsAsync(customerId)).Should().ContainSingle()
            .Which.SmsConsent.Should().BeFalse();
    }

    /// <summary>Bozuk değeri gönderen tam da bot; tuzağa düşen botun yanıtı
    /// sessiz 200 olmalı, 500 değil (tuzak sayfayı aynen yeniden çiziyor).</summary>
    [Fact]
    public async Task Honeypot_ve_bozuk_deger_birlikte_sessiz_200_doner()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var page = await OpenFormAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(page, slug, ("website", "http://spam.example"), ("Input.WhatsAppConsent", "on")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    // ─── Yardımcılar ────────────────────────────────────────────────────────

    private async Task<(string Slug, Guid CustomerId)> SeedConfigAsync(bool smsEnabled = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"mc-{Guid.NewGuid():N}@x",
            Name = "Mc",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-MC-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        // SMS kutusu yalnız doğrulanmış kurulumda çiziliyor; çökme yolu kutunun
        // çizilmesini gerektirdiği için varsayılan açık.
        if (smsEnabled)
        {
            db.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
                PasswordProtected = $"pw-{Guid.NewGuid():N}",
                Header = "ORDERDECK",
                BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
                Status = NetgsmAccountStatus.Verified,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        var slug = $"mc-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = TestPhone.NewE164(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>Formun gönderimde geri yolladığı sayfa değerleri.
    /// <see cref="KvkkTextVersion"/>: zorunlu KVKK onayının (PR #511) sayfaya
    /// bastığı metin sürümü; o sürüm yoksa null.</summary>
    private sealed record FormPage(string Token, string? KvkkTextVersion);

    private static async Task<FormPage> OpenFormAsync(HttpClient client, string slug)
    {
        var html = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();
        var kvkk = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"Input.KvkkTextVersion\" value=\"([^\"]*)\"");
        return new FormPage(AdminLoginHelper.ExtractAntiForgeryToken(html),
            kvkk.Success ? kvkk.Groups[1].Value : null);
    }

    /// <summary>Geçerli bir gönderim; <paramref name="overrides"/> alanı ezer
    /// ya da ekler. YouTube bilerek boş: kanal çözülmeyince onay kutusunun
    /// kendi kuralı devreye girmiyor, kutu yine de sayfada çiziliyor.
    ///
    /// KVKK onayı tarayıcının yaptığı gibi gönderiliyor: kutu işaretli, sürüm
    /// sayfanın gizli alanından. Bu dosya PR #511'den bağımsız yazıldı; o PR
    /// birleşince "geçerli form" onay ister, birleşmeden önce iki alan da
    /// bağlayıcıda karşılığı olmadığı için yok sayılır — birleşme sırası ne
    /// olursa olsun testler aynı şeyi ölçer.</summary>
    private static FormUrlEncodedContent Form(FormPage page, string slug, params (string Key, string Value)[] overrides)
    {
        var d = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = page.Token,
            ["Slug"] = slug,
            ["Input.KvkkAcknowledged"] = "true",
            ["Input.InstagramUsername"] = $"ig_{Guid.NewGuid():N}"[..16],
            ["Input.FullName"] = "Örnek Müşteri",
            ["Input.Email"] = $"m-{Guid.NewGuid():N}@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = TestPhone.NewNational()
        };
        if (page.KvkkTextVersion is not null) d["Input.KvkkTextVersion"] = page.KvkkTextVersion;
        foreach (var (k, v) in overrides) d[k] = v;
        return new FormUrlEncodedContent(d);
    }

    private async Task<List<IntakeFormSubmission>> RowsAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IntakeFormSubmissions.AsNoTracking()
            .Where(s => s.Config.CustomerId == customerId)
            .ToListAsync();
    }

    private async Task<bool> IysEventsAsync(string phone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IysConsentEvents.AsNoTracking().AnyAsync(e => e.Recipient == phone);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));
}
