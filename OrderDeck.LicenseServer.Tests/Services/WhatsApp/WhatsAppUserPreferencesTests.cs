using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// <c>user_preferences</c> webhook'u: müşterinin pazarlama mesajı tercihi.
///
/// <para>Bu defterin kritik özelliği <b>kaybın telafi edilememesi</b>: Meta
/// tercihi okumak için uç nokta sunmuyor, olayı da yeniden göndermiyor.
/// Kaçırılan bir <c>stop</c> geri gelmez. Testler bu yüzden "yazıldı mı"nın
/// ötesine geçip <i>yanlış yazılabilecek</i> yolları da kapatıyor.</para>
/// </summary>
public sealed class WhatsAppUserPreferencesTests
{
    private const string Bsuid = "US.13491208655302741918";

    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId) Build(
        string pnid = "PNID_1")
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"waprefs-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        var licenseId = Guid.NewGuid();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = pnid,
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("t"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        return (db, new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(new PdfDekontParser(), NullLogger<WaDekontExtractor>.Instance)),
            licenseId);
    }

    /// <summary>Meta'nın belgelediği biçim: <c>timestamp</c> SAYI, mesaj
    /// webhook'undaki gibi string değil.</summary>
    private static string Payload(
        string value, long ts, string? waId = "905321234567", string? userId = null,
        string category = "marketing_messages", string pnid = "PNID_1")
    {
        var idFields = string.Join(", ", new[]
        {
            waId is null ? null : $"\"wa_id\": \"{waId}\"",
            userId is null ? null : $"\"user_id\": \"{userId}\"",
        }.Where(x => x is not null));

        return $$"""
        {
          "entry": [{ "changes": [{ "field": "user_preferences", "value": {
            "messaging_product": "whatsapp",
            "metadata": { "phone_number_id": "{{pnid}}" },
            "user_preferences": [{ {{idFields}},
              "detail": "User requested to stop marketing messages",
              "category": "{{category}}", "value": "{{value}}", "timestamp": {{ts}} }]
          }, "field": "user_preferences" }]}]
        }
        """;
    }

    // ---------- ayrıştırma ----------

    [Fact]
    public void Tercih_olayi_ayristirilir()
    {
        var events = WhatsAppWebhookParser.Parse(Payload("stop", 1731705721, userId: Bsuid));

        var p = events.UserPreferences.Should().ContainSingle().Subject;
        p.PhoneNumberId.Should().Be("PNID_1");
        p.WaId.Should().Be("905321234567");
        p.UserId.Should().Be(Bsuid);
        p.Category.Should().Be("marketing_messages");
        p.Value.Should().Be("stop");
        p.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1731705721));
    }

    /// <summary>
    /// Yalnız tercih içeren paket <c>IsEmpty</c> sayılmamalı — sayılsaydı
    /// <c>ProcessAsync</c> erken döner ve tercih sessizce düşerdi. Tercih
    /// olayları kendi paketlerinde geldiği için bu, istisna değil NORMAL hâl.
    /// </summary>
    [Fact]
    public void Yalniz_tercih_iceren_paket_bos_sayilmaz()
    {
        WhatsAppWebhookParser.Parse(Payload("stop", 1731705721)).IsEmpty.Should().BeFalse();
    }

    /// <summary>Kararı olmayan satır defteri kirletir; atlanmalı.</summary>
    [Theory]
    [InlineData("\"category\": \"marketing_messages\"")]
    [InlineData("\"value\": \"stop\"")]
    public void Kararsiz_veya_kategorisiz_satir_atlanir(string onlyField)
    {
        var payload = $$$"""
        {
          "entry": [{ "changes": [{ "field": "user_preferences", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "user_preferences": [{ "wa_id": "905321234567", {{{onlyField}}}, "timestamp": 1731705721 }]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).UserPreferences.Should().BeEmpty();
    }

    // ---------- kalıcılaştırma ----------

    [Fact]
    public async Task Stop_kaydedilir()
    {
        var (db, job, licenseId) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721, userId: Bsuid));

        var row = db.WaMarketingPreferences.Single();
        row.LicenseId.Should().Be(licenseId);
        row.CustomerPhone.Should().Be("905321234567");
        row.BsuId.Should().Be(Bsuid);
        row.Category.Should().Be("marketing_messages");
        row.Preference.Should().Be(WaMarketingPreferences.Stop);
        row.PreferenceAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1731705721));
        row.Source.Should().Be(WaMarketingPreferenceSources.UserPreferences);
    }

    [Fact]
    public async Task Resume_ayni_satiri_gunceller_yeni_satir_acmaz()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721));
        await job.ProcessAsync(Payload("resume", 1731705999));

        db.WaMarketingPreferences.Single().Preference.Should().Be(WaMarketingPreferences.Resume);
    }

    /// <summary>
    /// <b>Bu testin koruduğu şey:</b> webhook'lar sırasız gelebilir ve Hangfire
    /// eski bir paketi yeniden işleyebilir. Sıralama işleme anına göre yapılsaydı
    /// geç işlenen eski bir <c>resume</c>, yeni bir <c>stop</c>'u ezerdi — ve
    /// sonuç, müşterinin çıkmak istediği mesajı ona göndermek olurdu.
    /// </summary>
    [Fact]
    public async Task Gec_gelen_ESKI_olay_yeni_karari_ezmez()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705999));    // yeni karar
        await job.ProcessAsync(Payload("resume", 1731705721));  // eski, geç geldi

        var row = db.WaMarketingPreferences.Single();
        row.Preference.Should().Be(WaMarketingPreferences.Stop);
        row.PreferenceAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1731705999));
    }

    /// <summary>
    /// Kullanıcı adı özelliğini açmış müşteride <c>wa_id</c> düşüyor. Eşleştirme
    /// BSUID üzerinden kurulmazsa aynı kişi için ikinci bir defter satırı açılır
    /// ve "bu müşteri çıktı mı" sorusunun iki farklı cevabı olur.
    /// </summary>
    [Fact]
    public async Task Telefonsuz_ikinci_olay_BSUID_ile_ayni_satiri_bulur()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721, userId: Bsuid));
        await job.ProcessAsync(Payload("resume", 1731705999, waId: null, userId: Bsuid));

        var row = db.WaMarketingPreferences.Single();
        row.Preference.Should().Be(WaMarketingPreferences.Resume);
        row.CustomerPhone.Should().Be("905321234567", "önceden bilinen telefon silinmemeli");
    }

    /// <summary>Önce yalnız BSUID'li olay geldiyse, telefon sonradan
    /// öğrenildiğinde satıra işlenmeli — kimlik zenginleştikçe eşleştirme
    /// kolaylaşır.</summary>
    [Fact]
    public async Task Sonradan_ogrenilen_telefon_satira_islenir()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721, waId: null, userId: Bsuid));
        db.WaMarketingPreferences.Single().CustomerPhone.Should().BeNull();

        await job.ProcessAsync(Payload("stop", 1731705999, userId: Bsuid));

        db.WaMarketingPreferences.Single().CustomerPhone.Should().Be("905321234567");
    }

    /// <summary>Kimliksiz olay kaydedilemez: kimin kararı olduğunu bilmiyoruz.
    /// Uydurulmuş bir eşleştirme, kaydın hiç olmamasından tehlikelidir.</summary>
    [Fact]
    public async Task Kimliksiz_olay_kaydedilmez()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721, waId: null));

        db.WaMarketingPreferences.Should().BeEmpty();
    }

    /// <summary>Tanınmayan numaraya gelen olay başka bir kiracının defterine
    /// yazılmamalı.</summary>
    [Fact]
    public async Task Taninmayan_numara_icin_kaydedilmez()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721, pnid: "BASKA_PNID"));

        db.WaMarketingPreferences.Should().BeEmpty();
    }

    /// <summary>Tercih kategori başına tutuluyor: Meta yeni bir kategori
    /// eklerse mevcut kararı ezmemeli, ayrı satır olmalı.</summary>
    [Fact]
    public async Task Farkli_kategori_ayri_satir_olur()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload("stop", 1731705721));
        await job.ProcessAsync(Payload("resume", 1731705999, category: "gelecek_kategori"));

        db.WaMarketingPreferences.Should().HaveCount(2);
        db.WaMarketingPreferences.Single(x => x.Category == "marketing_messages")
            .Preference.Should().Be(WaMarketingPreferences.Stop);
    }

    /// <summary>Aynı paketin Hangfire tarafından yeniden işlenmesi ikinci satır
    /// açmamalı.</summary>
    [Fact]
    public async Task Ayni_paket_iki_kez_islenirse_tek_satir_kalir()
    {
        var (db, job, _) = Build();
        var payload = Payload("stop", 1731705721, userId: Bsuid);

        await job.ProcessAsync(payload);
        await job.ProcessAsync(payload);

        db.WaMarketingPreferences.Should().ContainSingle();
    }

    // ---------- 131050: düşen pazarlama mesajından çıkarım ----------
    //
    // Tercih webhook'u yalnız abone olduğumuz andan SONRA çıkanları haber
    // veriyor; Meta geçmişi göndermiyor. Bugüne kadar çıkmış müşteriler
    // deftere ancak bu yoldan giriyor — yani fiilen çalışan yol bu.

    private static void SeedOutbound(
        LicenseDbContext db, Guid licenseId, string phone, params string[] wamIds)
    {
        var convoId = Guid.NewGuid();
        db.WaConversations.Add(new WaConversation
        {
            Id = convoId,
            LicenseId = licenseId,
            CustomerPhone = phone,
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var wamId in wamIds)
        {
            db.WaMessages.Add(new WaMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = convoId,
                LicenseId = licenseId,
                WamId = wamId,
                Direction = "out",
                Type = "template",
                Status = "sent",
                Timestamp = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        db.SaveChanges();
    }

    /// <summary>Durum webhook'u: <c>timestamp</c> burada yine STRING.</summary>
    private static string FailedPayload(long ts, string code, params string[] wamIds)
    {
        var statuses = string.Join(", ", wamIds.Select(id =>
            $$"""
            { "id": "{{id}}", "status": "failed", "timestamp": "{{ts}}",
              "errors": [{ "code": {{code}}, "title": "Marketing message blocked" }] }
            """));

        return $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "statuses": [{{{statuses}}}]
          }}]}]
        }
        """;
    }

    [Fact]
    public async Task Dusen_pazarlama_mesaji_stop_olarak_deftere_yazilir()
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1");

        await job.ProcessAsync(FailedPayload(1731705721, "131050", "wamid.OUT1"));

        var row = db.WaMarketingPreferences.Single();
        row.LicenseId.Should().Be(licenseId);
        row.CustomerPhone.Should().Be("905321234567");
        row.Category.Should().Be(WaMarketingPreferences.MarketingCategory);
        row.Preference.Should().Be(WaMarketingPreferences.Stop);
        row.PreferenceAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1731705721));
        row.Source.Should().Be(
            WaMarketingPreferenceSources.Error131050,
            "kaynak ayırt edilmezse defter, müşterinin söylemediği bir şeyi " +
            "söylemiş gibi gösterir");

        db.WaMessages.Single().ErrorCode.Should().Be("131050", "mesaj kaydı da bozulmamalı");
    }

    /// <summary>Yalnız 131050 tercih anlamına gelir. 131047 (pencere kapalı) ya
    /// da 131026 (teslim edilemez) müşterinin kararı hakkında hiçbir şey
    /// söylemez; deftere yazmak uydurma olurdu.</summary>
    [Theory]
    [InlineData("131047")]
    [InlineData("131026")]
    [InlineData("131049")]
    public async Task Baska_hata_kodu_deftere_yazilmaz(string code)
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1");

        await job.ProcessAsync(FailedPayload(1731705721, code, "wamid.OUT1"));

        db.WaMarketingPreferences.Should().BeEmpty();
    }

    /// <summary>Müşterinin kendi beyanı, sonradan gelirse çıkarımı ezmeli —
    /// hem karar hem kaynak güncellenmeli.</summary>
    [Fact]
    public async Task Sonraki_resume_131050_karari_ezer()
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1");

        await job.ProcessAsync(FailedPayload(1731705721, "131050", "wamid.OUT1"));
        await job.ProcessAsync(Payload("resume", 1731705999));

        var row = db.WaMarketingPreferences.Single();
        row.Preference.Should().Be(WaMarketingPreferences.Resume);
        row.Source.Should().Be(WaMarketingPreferenceSources.UserPreferences);
    }

    /// <summary>
    /// <b>Bu testin koruduğu şey:</b> müşteri geri döndükten sonra, ondan önce
    /// gönderilmiş bir mesajın düşme bildirimi geç gelebilir. Sıralama işleme
    /// anına göre yapılsaydı bu bildirim <c>resume</c>'u ezer ve geri dönmüş
    /// müşteriyi tekrar çıkmış sayardık.
    /// </summary>
    [Fact]
    public async Task Gec_gelen_131050_yeni_resume_u_ezmez()
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1");

        await job.ProcessAsync(Payload("resume", 1731705999));
        await job.ProcessAsync(FailedPayload(1731705721, "131050", "wamid.OUT1"));

        var row = db.WaMarketingPreferences.Single();
        row.Preference.Should().Be(WaMarketingPreferences.Resume);
        row.Source.Should().Be(WaMarketingPreferenceSources.UserPreferences);
    }

    /// <summary>Zaten <c>stop</c> yazılmış müşteride 131050 ikinci satır
    /// açmamalı.</summary>
    [Fact]
    public async Task Zaten_stop_olan_musteride_ikinci_satir_acilmaz()
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1");

        await job.ProcessAsync(Payload("stop", 1731705721));
        await job.ProcessAsync(FailedPayload(1731705999, "131050", "wamid.OUT1"));

        db.WaMarketingPreferences.Should().ContainSingle()
            .Which.Preference.Should().Be(WaMarketingPreferences.Stop);
    }

    /// <summary>
    /// <b>Bu testin koruduğu şey:</b> aynı pakette aynı müşteriye giden iki
    /// mesaj birden düşebilir (toplu gönderim). Arama yalnız veritabanına
    /// bakarsa ilk <c>Add</c> henüz kaydedilmediği için ikinci olay da yeni
    /// satır açar ve <c>SaveChanges</c> tekil indeksten patlar — o da tüm
    /// paketi, içindeki gerçek mesajlarla birlikte düşürür.
    /// </summary>
    [Fact]
    public async Task Ayni_paketteki_iki_dusen_mesaj_tek_satir_yazar()
    {
        var (db, job, licenseId) = Build();
        SeedOutbound(db, licenseId, "905321234567", "wamid.OUT1", "wamid.OUT2");

        await job.ProcessAsync(
            FailedPayload(1731705721, "131050", "wamid.OUT1", "wamid.OUT2"));

        db.WaMarketingPreferences.Should().ContainSingle();
    }

    /// <summary>Bizim kaydımız olmayan mesajın durumu zaten atlanıyor; deftere
    /// de bir şey yazılmamalı.</summary>
    [Fact]
    public async Task Taninmayan_mesaj_icin_defter_yazilmaz()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(FailedPayload(1731705721, "131050", "wamid.BILINMEYEN"));

        db.WaMarketingPreferences.Should().BeEmpty();
    }
}
