using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    private const string RequiredMessage = "Devam etmek için aydınlatma metnini okuduğunu onaylamalısın.";
    private const string PolicyUrl = "https://orderdeckapp.com/gizlilik-politikasi/";

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
    /// </summary>
    private static FormUrlEncodedContent Form(
        string token, string slug, Entered e, params string[] kvkk)
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
    public async Task Sayfa_zorunlu_kutuyu_ve_politika_baglantisini_paragrafin_hemen_altinda_cizer()
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

        var link = Tag(label, "<a [^>]*>Gizlilik Politikası</a>");
        link.Should().Contain($"href=\"{PolicyUrl}\"");
        link.Should().Contain("target=\"_blank\"");
        link.Should().Contain("rel=\"noopener\"");

        // Yer: aydınlatma paragrafından hemen sonra, bal küpü ve gönder
        // butonundan önce.
        var paragraphEnd = html.IndexOf("</p>", html.IndexOf("<p class=\"kvkk\">", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var labelAt = html.IndexOf(label, StringComparison.Ordinal);
        paragraphEnd.Should().BePositive();
        labelAt.Should().BeGreaterThan(paragraphEnd);
        html[(paragraphEnd + 4)..labelAt].Trim().Should().BeEmpty("araya başka öğe girmemeli");
        labelAt.Should().BeLessThan(html.IndexOf("name=\"website\"", StringComparison.Ordinal));
        labelAt.Should().BeLessThan(html.IndexOf("class=\"btn\"", StringComparison.Ordinal));

        // İşaretsiz kutunun "false" göndermesini sağlayan gizli alan formun içinde.
        var formEnd = html.IndexOf("</form>", StringComparison.Ordinal);
        Regex.Match(html[..formEnd], "<input name=\"Input\\.KvkkAcknowledged\" type=\"hidden\" value=\"false\"")
            .Success.Should().BeTrue();
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
    /// metnin özetini sürüme bağlıyor. Kırmızıysa: metni bilerek değiştirdiysen
    /// <see cref="KvkkNotice.TextVersion"/>'ı artır (kural orada) ve aşağıdaki
    /// iki sabiti birlikte güncelle.
    /// </summary>
    [Fact]
    public async Task Metin_degisirse_surum_de_degismeli()
    {
        const string ExpectedVersion = "kvkk-2026-10-13";
        const string ExpectedSha256 = "3CFE27445DE1E17C2F6BEF050AF49BF016F62EC945D531785F95DBFF4452A40C";

        var (slug, _) = await SeedConfigAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();

        static string Text(string fragment)
            => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(fragment, "<[^>]+>", " ")), "\\s+", " ").Trim();

        var paragraph = Tag(html, "<p class=\"kvkk\">.*?</p>");
        var label = Tag(html, "<label class=\"check\">\\s*<input[^>]*name=\"Input\\.KvkkAcknowledged\".*?</label>");
        var href = Regex.Match(label, "href=\"([^\"]+)\"").Groups[1].Value;
        var canonical = Text(paragraph) + "\n" + Text(label) + "\n" + href;
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        KvkkNotice.TextVersion.Should().Be(ExpectedVersion,
            "sürüm değiştiyse bu testteki özeti de yeni metne göre güncelle");
        sha.Should().Be(ExpectedSha256,
            $"aydınlatma metni ya da onay etiketi değişti; KvkkNotice.TextVersion'ı artır. Yeni özet {sha}, metin:\n{canonical}");
    }
}
