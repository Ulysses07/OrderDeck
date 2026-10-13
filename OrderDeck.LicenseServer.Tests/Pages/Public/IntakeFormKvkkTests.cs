using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Instagram;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Kayıt formundaki ZORUNLU KVKK aydınlatma onayı (sahip kararı 2026-10-13):
/// açık rıza değil, "aydınlatma metnini ve Gizlilik Politikası'nı okudum"
/// beyanı. Yetkili kapı sunucu — kutu işaretli gelmeyen gönderim kayıt açmaz,
/// girilen veri ekranda kalır; kabul edilen gönderim onayın zamanını ve metin
/// sürümünü taşır.
/// </summary>
public sealed class IntakeFormKvkkTests : IClassFixture<ApiFactory>
{
    private const string KvkkField = "Input.KvkkAcknowledged";
    private const string VersionField = "Input.KvkkTextVersion";
    private const string RequiredMessage = "Devam etmek için aydınlatma metnini okuduğunu onaylamalısın.";
    private const string OutdatedMessage = "Aydınlatma metni güncellendi; yeniden okuyup onayla.";
    /// <summary>Politikanın web sayfası. Form artık oraya gitmiyor; metin
    /// formun içindeki kutuda (sahip kararı 2026-10-13).</summary>
    private const string PolicyPagePath = "orderdeckapp.com/gizlilik-politikasi";

    private readonly ApiFactory _factory;
    public IntakeFormKvkkTests(ApiFactory factory) => _factory = factory;

    private async Task<(string Slug, Guid CustomerId)> SeedConfigAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"kvkk-{Guid.NewGuid():N}@example.test",
            Name = "Kvkk",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = $"LDK-KVKK-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"k-{Guid.NewGuid():N}"[..10];
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

    private static async Task<string> TokenAsync(HttpClient client, string path)
        => AdminLoginHelper.ExtractAntiForgeryToken(
            await (await client.GetAsync(path)).Content.ReadAsStringAsync());

    /// <summary>Gönderimdeki alanlar — testler hangi alanın ne gittiğini
    /// karşılaştırabilsin diye tek yerde.</summary>
    private sealed record Entered(
        string InstagramUsername, string FullName, string Email, string Address,
        string Phone, string Tckn)
    {
        public static Entered New() => new(
            InstagramUsername: $"kvkk_{Guid.NewGuid():N}"[..20],
            FullName: "Örnek Müşteri",
            Email: $"musteri-{Guid.NewGuid():N}@example.com",
            Address: "Atatürk Cad. No:12",
            Phone: TestPhone.NewNational(),
            Tckn: TestTckn.NewValid());
    }

    /// <summary>
    /// Liste, sözlük değil: tarayıcı işaretli kutu için AYNI ada iki değer
    /// gönderiyor ("true" + tag helper'ın gizli "false"u). <paramref name="kvkk"/>
    /// tam olarak o alan için giden değerler; boşsa alan hiç gönderilmez.
    /// Metin sürümü güncel sayfanın bastığı değer; eski sekmeyi taklit eden
    /// testler <see cref="FormWithVersion"/>'ı kullanıyor.
    /// </summary>
    private static FormUrlEncodedContent Form(
        string token, string slug, Entered e, params string[] kvkk)
        => FormWithVersion(token, slug, e, KvkkNotice.TextVersion, kvkk);

    /// <param name="version">Sayfanın gizli alanından giden metin sürümü;
    /// null ise alan hiç gönderilmez.</param>
    private static FormUrlEncodedContent FormWithVersion(
        string token, string slug, Entered e, string? version, params string[] kvkk)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Slug", slug),
            new("Input.InstagramUsername", e.InstagramUsername),
            new("Input.FullName", e.FullName),
            new("Input.Email", e.Email),
            new("Input.City", "İstanbul"),
            new("Input.District", "Kadıköy"),
            new("Input.Address", e.Address),
            new("Input.Phone", e.Phone),
            new("Input.Tckn", e.Tckn),
            new("Input.WhatsAppConsent", "true"),
            new("Input.WhatsAppConsent", "false"),
        };
        fields.AddRange(kvkk.Select(v => new KeyValuePair<string, string>(KvkkField, v)));
        if (version is not null) fields.Add(new(VersionField, version));
        return new FormUrlEncodedContent(fields);
    }

    private async Task<List<IntakeFormSubmission>> RowsAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IntakeFormSubmissions.AsNoTracking()
            .Where(s => s.Config.CustomerId == customerId)
            .ToListAsync();
    }

    private static string Tag(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.Singleline);
        m.Success.Should().BeTrue($"sayfada şu kalıp olmalı: {pattern}");
        return m.Value;
    }

    private static string KvkkInputTag(string html)
        => Tag(html, "<input[^>]*type=\"checkbox\"[^>]*name=\"Input\\.KvkkAcknowledged\"[^>]*>");

    private static string VersionInputTag(string html)
        => Tag(html, "<input[^>]*name=\"Input\\.KvkkTextVersion\"[^>]*>");

    /// <summary>Sunucunun KVKK alanına yazdığı hata. Sayfanın tamamında aramak
    /// yanıltır: aynı cümle istemci tarafı kapının JS'inde de duruyor.</summary>
    private static string KvkkError(string decodedHtml)
        => Regex.Match(Tag(decodedHtml, "<span[^>]*id=\"kvkkErr\"[^>]*>[^<]*</span>"), ">([^<]*)<")
            .Groups[1].Value.Trim();

    // ─── Kabul ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Isaretli_kutu_kabul_edilir_ve_kanit_kayda_yazilir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");

        var before = DateTimeOffset.UtcNow;
        // Tarayıcının işaretli kutu için gerçekten gönderdiği biçim.
        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, Entered.New(), "true", "false"));
        var after = DateTimeOffset.UtcNow;

        // 302-siz onay akışı bozulmadı: POST kendi yoluna döner, WhatsApp'a
        // geçiş onay ekranındaki sayfa içi atlamada.
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be($"/r/{slug}");
        var ok = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();
        ok.Should().Contain("Kaydın alındı");
        ok.Should().Contain("id=\"waLink\"");

        var sub = (await RowsAsync(customerId)).Should().ContainSingle().Subject;
        sub.KvkkTextVersion.Should().Be(KvkkNotice.TextVersion);
        sub.KvkkAcknowledgedAt.Should().NotBeNull();
        sub.KvkkAcknowledgedAt!.Value.Offset.Should().Be(TimeSpan.Zero, "sunucu saati, UTC");
        sub.KvkkAcknowledgedAt.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        sub.KvkkAcknowledgedAt.Value.Should().Be(sub.SubmittedAt,
            "onay gönderimin kabul edildiği anda verilmiş sayılır");
        // İsteğe bağlı izinler ayrı alan, KVKK onayıyla karışmıyor.
        sub.WhatsAppConsent.Should().BeTrue();
        sub.SmsConsent.Should().BeFalse();
    }

    // ─── Ret: işaretsiz / eksik / bozuk ─────────────────────────────────────

    [Fact]
    public async Task Isaretsiz_kutu_reddedilir_veri_korunur_kayit_acilmaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");
        var entered = Entered.New();

        // İşaretsiz kutu: tarayıcı yalnız gizli "false"u gönderir.
        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, entered, "false"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "normal satır içi doğrulama hatası");
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(RequiredMessage);
        KvkkInputTag(html).Should().NotContain("checked");

        // Veri kaybı yok: her alan yazıldığı gibi geri geliyor.
        Tag(html, "<input[^>]*id=\"Input_InstagramUsername\"[^>]*>")
            .Should().Contain($"value=\"{entered.InstagramUsername}\"");
        Tag(html, "<input[^>]*id=\"Input_FullName\"[^>]*>").Should().Contain($"value=\"{entered.FullName}\"");
        Tag(html, "<input[^>]*id=\"Input_Email\"[^>]*>").Should().Contain($"value=\"{entered.Email}\"");
        Tag(html, "<input[^>]*id=\"Input_Phone\"[^>]*>").Should().Contain($"value=\"{entered.Phone}\"");
        Tag(html, "<input[^>]*id=\"Input_Tckn\"[^>]*>").Should().Contain($"value=\"{entered.Tckn}\"");
        Tag(html, "<textarea[^>]*id=\"Input_Address\"[^>]*>[^<]*</textarea>").Should().Contain(entered.Address);
        Tag(html, "<option[^>]*value=\"İstanbul\"[^>]*>").Should().Contain("selected");
        Tag(html, "<input[^>]*id=\"Input_WhatsAppConsent\"[^>]*>").Should().Contain("checked");

        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    /// <summary>Alanın hiç gelmemesi: deploy öncesinden açık kalmış bir sekme
    /// (sayfada kutu yoktu) ya da elle kurulmuş istek. İkisi de reddedilir;
    /// eski sekmedeki müşteri yeniden çizilen sayfada kutuyu görür.</summary>
    [Fact]
    public async Task Alan_hic_gelmezse_reddedilir_ve_yeniden_cizilen_sayfada_kutu_gorunur()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, Entered.New()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(RequiredMessage);
        KvkkInputTag(html).Should().Contain("required");
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    /// <summary>
    /// Bağlayıcının bool'a çeviremediği değer. Ham değer ModelState'te kalırsa
    /// yeniden çizimde checkbox tag helper patlıyordu — ret, 500 değil normal
    /// doğrulama hatası olmalı ve yine kayıt açılmamalı.
    /// </summary>
    [Theory]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("evet")]
    [InlineData("")]
    public async Task Bozuk_deger_reddedilir_sayfa_cokmez(string tampered)
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, Entered.New(), tampered));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(RequiredMessage);
        html.Should().NotContain("is not valid", "bağlayıcının İngilizce mesajı müşteriye sızmamalı");
        html.Should().NotContain("is invalid");
        KvkkInputTag(html).Should().NotContain("checked");
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    /// <summary>Başka bir alan yüzünden dönen sayfada işaretli kutu işaretli
    /// kalmalı — müşteri yalnız hatalı alanı düzeltip yeniden gönderebilsin.</summary>
    [Fact]
    public async Task Baska_alan_hatasinda_isaretli_kutu_korunur_ve_kvkk_hatasi_cikmaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");
        var entered = Entered.New() with { Phone = "" };

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, entered, "true", "false"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        html.Should().Contain("WhatsApp numarası zorunlu");
        KvkkError(html).Should().BeEmpty();
        KvkkInputTag(html).Should().Contain("checked");
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    /// <summary>Reddedilen gönderimden sonra kutuyu işaretleyip yeniden
    /// gönderen müşteri: tam olarak BİR kayıt, kanıtıyla.</summary>
    [Fact]
    public async Task Retten_sonra_isaretleyip_yeniden_gonderim_tek_kayit_acar()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var entered = Entered.New();

        var first = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(await TokenAsync(client, $"/r/{slug}"), slug, entered, "false"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await first.Content.ReadAsStringAsync());

        var second = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, entered, "true", "false"));

        second.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = (await RowsAsync(customerId)).Should().ContainSingle().Subject;
        sub.KvkkTextVersion.Should().Be(KvkkNotice.TextVersion);
        sub.KvkkAcknowledgedAt.Should().NotBeNull();
    }

    /// <summary>Bal küpü kuralı önce gelir: bot sessiz 200 alır, KVKK hatası
    /// bile görmez (formun gerçek kurallarını bota öğretmiyoruz).</summary>
    [Fact]
    public async Task Bal_kupu_onaysiz_gonderimde_de_sessiz_kalir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bot Bot",
            ["website"] = "http://bot-spam.example"
        });

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        KvkkError(WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync())).Should().BeEmpty();
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    // ─── Metin sürümü: müşterinin GÖRDÜĞÜ metin ─────────────────────────────

    /// <summary>
    /// Sayfa açıkken metin değişip sürüm artarsa (deploy arası), eski sayfadan
    /// gelen onay YENİ sürümle kaydedilmemeli: müşteri o metni hiç görmedi.
    /// Sunucu sayfanın bastığı sürümü güncel sürümle karşılaştırıyor; tutmazsa
    /// onay düşer, kutu işaretsiz ve güncel sürümle yeniden çizilir, veri kalır.
    /// </summary>
    [Fact]
    public async Task Eski_surumlu_sayfadan_gelen_onay_reddedilir_ve_guncel_metinle_yeniden_sorulur()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");
        var entered = Entered.New();

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            FormWithVersion(token, slug, entered, "kvkk-2000-01-01", "true", "false"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "normal satır içi doğrulama hatası");
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(OutdatedMessage);
        KvkkInputTag(html).Should().NotContain("checked", "onay yeni metin için yeniden verilmeli");
        VersionInputTag(html).Should().Contain($"value=\"{KvkkNotice.TextVersion}\"",
            "yeniden çizilen sayfa güncel metni ve sürümünü taşır");
        Tag(html, "<input[^>]*id=\"Input_FullName\"[^>]*>").Should().Contain($"value=\"{entered.FullName}\"");
        Tag(html, "<input[^>]*id=\"Input_Phone\"[^>]*>").Should().Contain($"value=\"{entered.Phone}\"");
        (await RowsAsync(customerId)).Should().BeEmpty();

        // Güncel sayfadan yeniden işaretleyip gönderen müşteri kabul edilir.
        var again = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(AdminLoginHelper.ExtractAntiForgeryToken(html), slug, entered, "true", "false"));
        again.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await RowsAsync(customerId)).Should().ContainSingle()
            .Which.KvkkTextVersion.Should().Be(KvkkNotice.TextVersion);
    }

    /// <summary>Sürüm alanı gelmeyen onay: hangi metnin görüldüğü bilinmiyor,
    /// kanıt kurulamaz, onay sayılmaz.</summary>
    [Fact]
    public async Task Surum_alani_gelmezse_onay_sayilmaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}");

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            FormWithVersion(token, slug, Entered.New(), null, "true", "false"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(OutdatedMessage);
        KvkkInputTag(html).Should().NotContain("checked");
        (await RowsAsync(customerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sayfa_gosterdigi_metnin_surumunu_formun_icinde_tasir()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        var tag = VersionInputTag(html);
        tag.Should().Contain("type=\"hidden\"");
        tag.Should().Contain($"value=\"{KvkkNotice.TextVersion}\"");
        html.IndexOf(tag, StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("</form>", StringComparison.Ordinal), "gönderimle birlikte gitmeli");
    }

    // ─── Diğer giriş yolları ────────────────────────────────────────────────

    /// <summary>
    /// "!kayıt → DM" linki (?ig=) kimliği bağlar ama kaydı açmaz; gönderim yine
    /// aynı kapıdan geçer. Ret bağlı kimliği tüketmemeli (müşteri kutuyu
    /// işaretleyip tekrar gönderebilsin) ve "Bağlantıyı kaldır" onaysız da
    /// çalışmalı — o ayrı bir handler, kayıt açmıyor.
    /// </summary>
    [Fact]
    public async Task Instagram_dm_linki_yolu_da_onay_ister()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var igUser = $"ig_{Guid.NewGuid():N}"[..20];
        var igToken = _factory.Services.GetRequiredService<IntakeIgTokenService>().Create(slug, igUser);
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}?ig={Uri.EscapeDataString(igToken)}");
        var entered = Entered.New() with { InstagramUsername = "" };

        var rejected = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(token, slug, entered, "false"));

        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
        KvkkError(html).Should().Be(RequiredMessage);
        html.Should().Contain(igUser, "ret bağlı kimliği tüketmemeli");
        (await RowsAsync(customerId)).Should().BeEmpty();

        var accepted = await client.PostAsync($"/r/{slug}?handler=Submit",
            Form(AdminLoginHelper.ExtractAntiForgeryToken(html), slug, entered, "true", "false"));

        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = (await RowsAsync(customerId)).Should().ContainSingle().Subject;
        sub.InstagramUsername.Should().Be(igUser);
        sub.KvkkTextVersion.Should().Be(KvkkNotice.TextVersion);
    }

    [Fact]
    public async Task Baglanti_kaldirma_onay_istemez()
    {
        var (slug, _) = await SeedConfigAsync();
        var igToken = _factory.Services.GetRequiredService<IntakeIgTokenService>()
            .Create(slug, $"ig_{Guid.NewGuid():N}"[..20]);
        var client = NewClient();
        var token = await TokenAsync(client, $"/r/{slug}?ig={Uri.EscapeDataString(igToken)}");

        var resp = await client.PostAsync($"/r/{slug}?handler=Unlink&platform=instagram",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Slug"] = slug
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync())
            .Should().NotContain("linked-chip");
    }

    // ─── Görünüm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sayfa_zorunlu_kutuyu_paragraf_ve_politika_kutusunun_hemen_altinda_cizer()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        var input = KvkkInputTag(html);
        input.Should().Contain("required", "JS kapalıyken de tarayıcı durdursun");
        input.Should().Contain("value=\"true\"");
        input.Should().NotContain("checked", "kutu işaretsiz gelmeli — onay müşterinin eylemi");
        input.Should().NotContain("hidden", "tag helper öznitelik bozulması (PR #361 sınıfı) kutuyu gizlememeli");

        var label = Tag(html, "<label class=\"check\">\\s*<input[^>]*name=\"Input\\.KvkkAcknowledged\".*?</label>");
        var text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(label, "<[^>]+>", "")), "\\s+", " ").Trim();
        text.Should().Be("Bu aydınlatma metnini ve Gizlilik Politikası'nı okudum. *");
        label.Should().Contain("<span class=\"req\">*</span>", "zorunluluk diğer alanlarla aynı işaretle görünür");

        // Yer: aydınlatma paragrafı → Gizlilik Politikası kutusu → onay; bal
        // küpü ve gönder butonundan önce. Müşteri "okudum" beyanına iki metni
        // de geçtikten sonra varır.
        var paragraphEnd = html.IndexOf("</p>", html.IndexOf("<p class=\"kvkk\">", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var detailsAt = html.IndexOf("<details class=\"policy\">", StringComparison.Ordinal);
        var detailsEnd = html.IndexOf("</details>", detailsAt, StringComparison.Ordinal) + "</details>".Length;
        var labelAt = html.IndexOf(label, StringComparison.Ordinal);
        paragraphEnd.Should().BePositive();
        detailsAt.Should().BeGreaterThan(paragraphEnd);
        labelAt.Should().BeGreaterThan(detailsEnd);
        html[(paragraphEnd + 4)..detailsAt].Trim().Should().BeEmpty("paragraf ile politika kutusu arasına başka öğe girmemeli");
        html[detailsEnd..labelAt].Trim().Should().BeEmpty("politika kutusu ile onay arasına başka öğe girmemeli");
        labelAt.Should().BeLessThan(html.IndexOf("name=\"website\"", StringComparison.Ordinal));
        labelAt.Should().BeLessThan(html.IndexOf("class=\"btn\"", StringComparison.Ordinal));

        // İşaretsiz kutunun "false" göndermesini sağlayan gizli alan formun içinde.
        var formEnd = html.IndexOf("</form>", StringComparison.Ordinal);
        Regex.Match(html[..formEnd], "<input name=\"Input\\.KvkkAcknowledged\" type=\"hidden\" value=\"false\"")
            .Success.Should().BeTrue();
    }

    /// <summary>
    /// Gizlilik Politikası formun içinde açılır kutuda okunur (sahip kararı
    /// 2026-10-13): yeni sekme yok, orderdeckapp.com'daki politika sayfasına
    /// bağlantı yok. Yerel details/summary — JS'siz açılıp kapanıyor.
    /// Başlık onay etiketinin DIŞINDA: etiketin içindeki dokunuş kutuyu da
    /// işaretlerdi, onay müşterinin bilinçli eylemi olmalı.
    /// </summary>
    [Fact]
    public async Task Gizlilik_politikasi_formun_icinde_acilir_kutuda_yeni_sekme_yok()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        var checkbox = doc.QuerySelector("input[type='checkbox'][name='Input.KvkkAcknowledged']");
        checkbox.Should().NotBeNull();
        var label = checkbox!.Closest("label");
        label.Should().NotBeNull();
        label!.QuerySelectorAll("a, button, details, summary").Should()
            .BeEmpty("etiketin içinde etkileşimli öğe yok; etikete dokunmak yalnız kutuyu işaretler");

        var details = doc.QuerySelectorAll("details.policy").Should().ContainSingle().Subject;
        details.Closest("label").Should().BeNull("başlık etiketin içinde olsaydı dokunuş onay kutusunu da değiştirirdi");
        details.Closest("form").Should().BeSameAs(checkbox.Closest("form"));
        details.HasAttribute("open").Should().BeFalse("kutu kapalı gelir, müşteri dokununca açılır");

        var summary = details.FirstElementChild;
        summary.Should().NotBeNull();
        summary!.LocalName.Should().Be("summary", "details'in ilk çocuğu başlık olmalı, yoksa tarayıcı kendi 'Ayrıntılar' başlığını basar");
        summary.TextContent.Trim().Should().Be("Gizlilik Politikası");
        summary.Children.Should().BeEmpty("başlık düz metin; bağlantı değil, sayfadan ayrılmıyor");

        // Metnin tamamı kutuda: ilk bölüm, form verisini anlatan bölüm, son bölüm.
        var box = details.QuerySelector(".policy-box");
        box.Should().NotBeNull();
        var boxText = Regex.Replace(box!.TextContent, "\\s+", " ");
        boxText.Should().Contain("1. Toplanan veriler")
            .And.Contain("1.3. Kayıt formu ile toplanan alıcı bilgileri")
            .And.Contain("10. İletişim");
        boxText.Should().Contain("support@orderdeckapp.com", "at-işareti &#64; ile yazılı, tarayıcıda @ görünmeli");

        // Eski davranış geri gelmesin.
        html.Should().NotContain(PolicyPagePath, "politika sayfasına giden bağlantı kalmadı");
        doc.QuerySelectorAll("a[target]").Where(a => a.Closest("details.policy") is null)
            .Should().BeEmpty("kutunun dışında yeni sekmede açılan bağlantı yok");

        // Yer: aydınlatma paragrafı ile onay ARASINDA. Etiketteki "Gizlilik
        // Politikası" düz metin, ona dokunmak kutuyu işaretler; politika
        // onayın altında kalsaydı müşteri onu "okudum"dan sonra keşfederdi.
        // Bitişiklik (araya metin girmemesi) yukarıdaki görünüm testinde.
        doc.QuerySelector("p.kvkk")!.NextElementSibling.Should().BeSameAs(details,
            "politika aydınlatma paragrafının hemen ardından gelir");
        details.NextElementSibling.Should().BeSameAs(label, "onay politikanın hemen ardından gelir");
    }

    /// <summary>
    /// Kutunun kendisi sayfadan ayrılmadığı için taslak yazmıyor. Kutunun
    /// İÇİNDEKİ bağlantılar (Veri Silme, Google izinleri…) ise sayfadan
    /// çıkabilir: yeni sekmede açılıyorlar, ama uygulama içi tarayıcı (Android
    /// WebView varsayılanı) <c>target="_blank"</c>'i aynı görünümde açabiliyor;
    /// sayfa no-store, geri dönüşte alanlar boşalırdı. Bu yüzden OAuth
    /// butonları gibi taslağı yazıyorlar. Taslak gönderimde silinmeli: bağlantı
    /// normal tarayıcıda yeni sekmede açıldığında taslak tüketilmiyor ve onay
    /// ekranından sonra aynı sekmede açılan form önceki kişinin bilgileriyle
    /// dolardı.
    ///
    /// JS motoru yok; test, taslağı yazan tıklama dinleyicisinin seçicisini
    /// betikten okuyup çizilen DOM'a uyguluyor.
    /// </summary>
    [Fact]
    public async Task Politika_kutusundaki_baglantilar_taslagi_kaydeder_gonderim_taslagi_siler()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        var saveAt = html.IndexOf("sessionStorage.setItem(DRAFT_KEY", StringComparison.Ordinal);
        saveAt.Should().BePositive("taslak kaydı sayfada olmalı");
        var selectors = Regex.Matches(html[..saveAt], "querySelectorAll\\('([^']+)'\\)");
        selectors.Should().NotBeEmpty();
        var draftSelector = selectors[^1].Groups[1].Value;
        draftSelector.Split(',').Select(s => s.Trim()).Should()
            .Contain(".link-start", "OAuth butonlarının taslağı yerinde kalmalı");

        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));
        var drafted = doc.QuerySelectorAll(draftSelector);
        drafted.Should().NotContain(doc.QuerySelector("details.policy > summary")!,
            "kutuyu açmak sayfadan ayrılmıyor");

        var links = doc.QuerySelectorAll("details.policy a[href]");
        links.Should().NotBeEmpty();
        foreach (var a in links)
        {
            var href = a.GetAttribute("href")!;
            drafted.Should().Contain(a, $"kutudaki bağlantı ({href}) taslağı yazan seçiciye ({draftSelector}) girmeli");
            a.ClassList.Should().NotContain("link-start", ".link-start buton stili taşıyor");
            if (href.StartsWith("mailto:", StringComparison.Ordinal)) continue;
            href.Should().StartWith("https://", "form başka alan adında; göreli adres lisans sunucusuna gider");
            a.GetAttribute("target").Should().Be("_blank", $"{href} formu terk ettirmemeli");
            a.GetAttribute("rel").Should().MatchRegex("noopener|noreferrer");
        }

        var submitAt = html.IndexOf("form.addEventListener('submit'", StringComparison.Ordinal);
        submitAt.Should().BePositive();
        html.IndexOf("sessionStorage.removeItem(DRAFT_KEY)", submitAt, StringComparison.Ordinal)
            .Should().BePositive("geçen gönderim taslağı silmeli");
    }

    /// <summary>
    /// Formun CSP'si (deploy/Caddyfile): <c>default-src 'self'</c>, dış kaynak
    /// yalnız YouTube avatarı, <c>form-action 'self'</c>. Yeni kutu bunlardan
    /// hiçbirine dokunmamalı: dış adres yalnız gezinme bağlantısı
    /// (<c>&lt;a href&gt;</c>, CSP'nin kapsamı dışında). Satır içi olay
    /// işleyicisi / style özniteliği de yok — CSP ileride
    /// <c>'unsafe-inline'</c>'ı bırakırsa kırılacak bir şey eklenmesin.
    /// </summary>
    [Fact]
    public async Task Sayfa_csp_ile_uyumlu_kalir()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        Regex.Matches(html, "<[a-z]+[^>]*\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)
            .Should().BeEmpty("satır içi olay işleyicisi yok");
        Regex.Matches(html, "<[a-z]+[^>]*\\sstyle\\s*=", RegexOptions.IgnoreCase)
            .Should().BeEmpty("style özniteliği yok");
        html.Should().NotContainEquivalentOf("javascript:");
        Regex.Matches(html, "<(script|img|iframe|link|embed|object)[^>]*\\s(src|href)\\s*=\\s*\"(https?:)?//",
                RegexOptions.IgnoreCase)
            .Should().BeEmpty("dış kaynak yüklenmiyor");
        foreach (Match form in Regex.Matches(html, "<form[^>]*>"))
        {
            var action = Regex.Match(form.Value, "action=\"([^\"]*)\"").Groups[1].Value;
            action.Should().StartWith("/", "form-action 'self'");
            action.Should().NotStartWith("//");
        }
        Regex.Matches(html, "formaction\\s*=\\s*\"(https?:)?//", RegexOptions.IgnoreCase)
            .Should().BeEmpty();
    }

    /// <summary>
    /// Kayıtlar onaylanan metnin SÜRÜMÜNÜ taşıyor; metin değişip sürüm aynı
    /// kalırsa eski ve yeni metne verilen onaylar ayırt edilemez. Bu test
    /// onaylanan metinlerin özetini sürüme bağlıyor:
    /// <list type="bullet">
    /// <item>formdaki aydınlatma paragrafı + onay etiketi;</item>
    /// <item>etiketin "okudum" dediği, formda açılan Gizlilik Politikası kutusu
    /// (<c>Pages/Public/_GizlilikPolitikasi.cshtml</c>), çizildiği hâliyle —
    /// metin ve bağlantı adresleri;</item>
    /// <item>kutunun kopyası olduğu web sayfasının kaynağı
    /// (<c>web/app/(tr)/gizlilik-politikasi/page.tsx</c>): web metni değişip
    /// form kopyası unutulmasın diye.</item>
    /// </list>
    /// İki kopyanın birbirine eşitliğini bu özetler değil
    /// <see cref="Formdaki_politika_kutusu_web_sayfasiyla_ayni_metni_gosterir"/>
    /// denetliyor. Kırmızıysa: politikanın iki kopyasını aynı tut; değişiklik anlamı
    /// etkiliyorsa <see cref="KvkkNotice.TextVersion"/>'ı artır (kural orada);
    /// her durumda aşağıdaki sabitleri birlikte güncelle. Özetler boşlukları
    /// tek boşluğa indirerek alınıyor: CRLF/LF ve yeniden biçimlendirme
    /// kırmızı yakmaz, metin ya da işaretleme değişikliği yakar.
    /// </summary>
    [Fact]
    public async Task Metin_degisirse_surum_de_degismeli()
    {
        const string ExpectedVersion = "kvkk-2026-10-14";
        const string ExpectedFormSha256 = "380EBEF1FE07D50042955D5BC34266E69600FBF89B481E831F4A008FCF9909B1";
        const string ExpectedPolicyBoxSha256 = "6C795F2F85DC3E6B3E9DCE91040D093636DC1FEAC7DFE07782C16932E6FEC79B";
        const string ExpectedPolicySourceSha256 = "CC7EB34EC20CA1F14C5AA63A3776793ABC922E2FF68BDA29BABCBB2788089BED";

        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        static string Text(string fragment)
            => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(fragment, "<[^>]+>", " ")), "\\s+", " ").Trim();
        static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        var paragraph = Tag(html, "<p class=\"kvkk\">.*?</p>");
        var label = Tag(html, "<label class=\"check\">\\s*<input[^>]*name=\"Input\\.KvkkAcknowledged\".*?</label>");
        var formCanonical = Text(paragraph) + "\n" + Text(label);
        var formSha = Sha(formCanonical);

        var details = Tag(html, "<details class=\"policy\">.*?</details>");
        var hrefs = Regex.Matches(details, "href=\"([^\"]+)\"")
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value));
        var boxCanonical = Text(details) + "\n" + string.Join("\n", hrefs);
        var boxSha = Sha(boxCanonical);

        var policyPath = FindRepoFile(PolicySourcePath);
        var policySha = Sha(Regex.Replace(await File.ReadAllTextAsync(policyPath), "\\s+", " ").Trim());

        KvkkNotice.TextVersion.Should().Be(ExpectedVersion,
            "sürüm değiştiyse bu testteki özetleri de yeni metne göre güncelle");
        formSha.Should().Be(ExpectedFormSha256,
            $"aydınlatma metni ya da onay etiketi değişti; anlam değiştiyse KvkkNotice.TextVersion'ı artır. Yeni özet {formSha}, metin:\n{formCanonical}");
        boxSha.Should().Be(ExpectedPolicyBoxSha256,
            "formdaki Gizlilik Politikası kutusu (Pages/Public/_GizlilikPolitikasi.cshtml) değişti; " +
            "web sayfasıyla (web/app/(tr)/gizlilik-politikasi/page.tsx) aynı tutulmalı, anlam değiştiyse " +
            $"KvkkNotice.TextVersion'ı artır. Yeni özet {boxSha}");
        policySha.Should().Be(ExpectedPolicySourceSha256,
            $"web Gizlilik Politikası değişti ({policyPath}); formdaki kopyayı " +
            "(Pages/Public/_GizlilikPolitikasi.cshtml) aynı değişiklikle güncelle, anlam değiştiyse " +
            $"KvkkNotice.TextVersion'ı artır. Yeni özet {policySha}");
    }

    // ─── İki kopya: web sayfası ↔ formdaki kutu ─────────────────────────────

    private const string PolicySourcePath = "web/app/(tr)/gizlilik-politikasi/page.tsx";
    private const string WebConstantsPath = "web/lib/i18n.ts";
    /// <summary>Web sitesinin kökü: sayfadaki göreli bağlantılar buraya çözülür
    /// (form başka alan adında, kutudaki kopyada tam adres yazılı).</summary>
    private const string WebOrigin = "https://orderdeckapp.com";

    private sealed record PolicyBlock(string Tag, string Text);

    /// <summary>
    /// Formdaki kutu web sayfasının birebir kopyası olmalı. Yukarıdaki özet
    /// testi iki kopyayı AYRI AYRI sürüme bağlıyor, birbirine bağlamıyor: biri
    /// güncellenip özeti yenilenir, öbürü unutulursa o test yeşile döner ve
    /// form eski metni gösterirken kayıtlar yeni sürümü taşır. Bu test web
    /// kaynağını React'in çizeceği metne çeviriyor (JSX boşluk kuralı;
    /// <c>{BRAND}</c>, <c>{LEGAL_NAME}</c>, <c>{CONTACT_EMAIL}</c> web/lib/i18n.ts'teki
    /// değerleriyle; <c>&amp;quot;</c>/<c>&amp;apos;</c> çözülmüş) ve kutunun
    /// çizilmiş hâliyle başlık/paragraf/madde sırasıyla karşılaştırıyor;
    /// bağlantı adresleri de (göreli adres <see cref="WebOrigin"/>'e çözülerek)
    /// aynı olmalı. Vurgu biçimi (strong/em/code) karşılaştırılmıyor.
    /// </summary>
    [Fact]
    public async Task Formdaki_politika_kutusu_web_sayfasiyla_ayni_metni_gosterir()
    {
        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var formDoc = await context.OpenAsync(r => r.Content(html));
        var box = formDoc.QuerySelector("details.policy .policy-box");
        box.Should().NotBeNull();

        var webHtml = "<div id=\"web\">" + await RenderWebPolicyAsHtmlAsync() + "</div>";
        var webDoc = await context.OpenAsync(r => r.Content(webHtml));
        var web = webDoc.GetElementById("web")!;

        var boxBlocks = Blocks(box!);
        var webBlocks = Blocks(web);
        webBlocks.Count.Should().BeGreaterThan(50, "web kaynağı çözümlenemediyse karşılaştırma boşa döner");
        for (var i = 0; i < Math.Max(boxBlocks.Count, webBlocks.Count); i++)
        {
            var inBox = i < boxBlocks.Count ? boxBlocks[i] : null;
            var onWeb = i < webBlocks.Count ? webBlocks[i] : null;
            // Gerekçe metninde süslü parantez olmasın: FluentAssertions onu
            // biçim dizesi olarak okuyor (record'un ToString'i parantezli).
            inBox.Should().Be(onWeb,
                $"{i}. blok iki kopyada farklı — form: «{Show(inBox)}», web: «{Show(onWeb)}». " +
                $"Pages/Public/_GizlilikPolitikasi.cshtml ile {PolicySourcePath} aynı tutulmalı; " +
                "metin değiştiyse KvkkNotice.TextVersion'ı artır");
        }

        Links(box!).Should().Equal(Links(web),
            "kutudaki bağlantılar web sayfasındakilerle aynı adrese gitmeli (göreli adres tam yazılır)");

        static List<PolicyBlock> Blocks(AngleSharp.Dom.IElement root)
            => root.QuerySelectorAll("h2, h3, p, li")
                .Select(e => new PolicyBlock(e.LocalName, Regex.Replace(e.TextContent, "\\s+", " ").Trim()))
                .ToList();

        static string Show(PolicyBlock? b) => b is null ? "(yok)" : $"<{b.Tag}> {b.Text}";

        static List<string> Links(AngleSharp.Dom.IElement root)
            => root.QuerySelectorAll("a[href]")
                .Select(a => a.GetAttribute("href")!)
                .Select(h => (h.StartsWith('/') ? WebOrigin + h : h).TrimEnd('/'))
                .ToList();
    }

    /// <summary>
    /// <see cref="PolicySourcePath"/>'teki <c>LegalLayout</c> gövdesini düz
    /// HTML'e çevirir: React'in çizeceği metin, etiketlerden yalnız adı ve
    /// <c>href</c>. Başa <c>LegalLayout</c>'un bastığı yürürlük tarihi satırı
    /// eklenir (kutudaki kopyada ilk paragraf). Tanımadığı bir JSX ifadesinde
    /// patlıyor: yanlış çözülmüş bir kaynakla yeşil kalmaktansa testin
    /// güncellenmesi istensin.
    /// </summary>
    private static async Task<string> RenderWebPolicyAsHtmlAsync()
    {
        var source = await File.ReadAllTextAsync(FindRepoFile(PolicySourcePath));
        var constants = Regex.Matches(await File.ReadAllTextAsync(FindRepoFile(WebConstantsPath)),
                "export const (BRAND|LEGAL_NAME|CONTACT_EMAIL) = '([^']*)';")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        constants.Keys.Should().BeEquivalentTo(new[] { "BRAND", "LEGAL_NAME", "CONTACT_EMAIL" },
            $"{WebConstantsPath} içindeki sabitler bulunamadı");

        var effectiveDate = Regex.Match(source, "const EFFECTIVE_DATE = '([^']+)'");
        effectiveDate.Success.Should().BeTrue($"{PolicySourcePath} içinde EFFECTIVE_DATE yok");
        var body = Regex.Match(source, "<LegalLayout\\b[^>]*>(.*)</LegalLayout>", RegexOptions.Singleline);
        body.Success.Should().BeTrue($"{PolicySourcePath} içinde LegalLayout gövdesi yok");

        // LegalLayout.tsx: "Yürürlük tarihi: {effectiveDate}".
        var sb = new StringBuilder($"<p>Yürürlük tarihi: {effectiveDate.Groups[1].Value}</p>");
        var text = body.Groups[1].Value;
        var at = 0;
        foreach (Match token in Regex.Matches(text, "<[^>]+>|\\{[^{}]*\\}"))
        {
            sb.Append(CleanJsxText(text[at..token.Index]));
            sb.Append(token.Value[0] == '<' ? JsxTagToHtml(token.Value) : JsxExpression(token.Value));
            at = token.Index + token.Length;
        }
        sb.Append(CleanJsxText(text[at..]));
        return sb.ToString();

        string JsxExpression(string expr)
        {
            var inner = expr[1..^1].Trim();
            if (inner is "' '" or "\" \"") return " ";
            if (Regex.IsMatch(inner, "^/\\*.*\\*/$", RegexOptions.Singleline)) return "";
            if (constants.TryGetValue(inner, out var value)) return WebUtility.HtmlEncode(value);
            throw new InvalidOperationException(
                $"{PolicySourcePath} içinde bu testin tanımadığı JSX ifadesi: {expr}. Çözümünü " +
                $"{nameof(RenderWebPolicyAsHtmlAsync)}'e ekle; testi silme.");
        }

        string JsxTagToHtml(string tag)
        {
            var name = Regex.Match(tag, "^<(/?)([A-Za-z][A-Za-z0-9]*)");
            if (!name.Success)
                throw new InvalidOperationException($"{PolicySourcePath} içinde çözülemeyen etiket: {tag}");
            if (name.Groups[1].Value == "/") return $"</{name.Groups[2].Value}>";

            var href = Regex.Match(tag, "\\shref=(?:\"([^\"]*)\"|\\{`([^`]*)`\\})");
            if (!href.Success)
            {
                if (Regex.IsMatch(tag, "\\shref="))
                    throw new InvalidOperationException($"{PolicySourcePath} içinde çözülemeyen href: {tag}");
                return $"<{name.Groups[2].Value}>";
            }
            var url = href.Groups[1].Success
                ? href.Groups[1].Value
                : Regex.Replace(href.Groups[2].Value, "\\$\\{([A-Z_]+)\\}", m => constants.TryGetValue(m.Groups[1].Value, out var v)
                    ? v
                    : throw new InvalidOperationException($"{PolicySourcePath} içinde çözülemeyen href: {tag}"));
            return $"<{name.Groups[2].Value} href=\"{WebUtility.HtmlEncode(url)}\">";
        }
    }

    /// <summary>
    /// JSX metin düğümünün çizilen hâli — Babel'in
    /// <c>cleanJSXElementLiteralChild</c> kuralı (SWC aynısını uyguluyor):
    /// satır sonuna değen boşluklar atılır, boş satırlar düşer, kalan satırlar
    /// tek boşlukla birleşir. <c>&lt;/a&gt;) tabidir</c> gibi yerlerde araya
    /// boşluk girip girmemesi buna bağlı.
    /// </summary>
    private static string CleanJsxText(string raw)
    {
        var lines = Regex.Split(raw, "\r\n|\n|\r");
        var lastNonEmpty = 0;
        for (var i = 0; i < lines.Length; i++)
            if (Regex.IsMatch(lines[i], "[^ \t]")) lastNonEmpty = i;

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Replace('\t', ' ');
            if (i != 0) line = line.TrimStart(' ');
            if (i != lines.Length - 1) line = line.TrimEnd(' ');
            if (line.Length == 0) continue;
            sb.Append(line);
            if (i != lastNonEmpty) sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz.
    /// Bulunamazsa sessizce geçmek yerine patlıyor: dosyası bulunamadığı için
    /// yeşil kalan bir bekçi, bekçinin hiç olmamasından beter.
    /// </summary>
    private static string FindRepoFile(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"{relativePath} bulunamadı ({AppContext.BaseDirectory} klasöründen yukarı arandı). " +
            "Dosya taşındıysa bu testi güncelle; testi silme.");
    }
}
