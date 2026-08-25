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
/// Numarasız gelen mesajların kalıcı sayacı.
///
/// <para>Bu ölçümün varlık sebebi: sunucuda kalıcı log YOK. Konteynerin
/// <c>json-file</c> sürücüsü her deploy'da sıfırlanıyor, master'a her merge de
/// bir deploy. Ölçüm yalnız loga yazsaydı, "BSUID'i sohbet kimliği yapalım mı"
/// kararı verilene kadar yaşamazdı — nitekim ilk hâli yaşamadı.</para>
/// </summary>
public sealed class WhatsAppDroppedInboundTests
{
    private const string Bsuid = "US.13491208655302741918";

    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId) Build()
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wadropped-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        var licenseId = Guid.NewGuid();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = "PNID_1",
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

    /// <summary>Numarasız mesaj: <c>from</c>/<c>wa_id</c> yok, yalnız BSUID var.</summary>
    private static string Payload(
        long ts, string? userId = Bsuid, string pnid = "PNID_1", params string[] extraWamIds)
    {
        var ids = new[] { "wamid.NOPHONE1" }.Concat(extraWamIds);
        var idField = userId is null ? "" : $"\"from_user_id\": \"{userId}\", ";

        var messages = string.Join(", ", ids.Select(id =>
            $$"""
            { {{idField}}"id": "{{id}}", "timestamp": "{{ts}}",
              "type": "text", "text": { "body": "numarasız" } }
            """));

        return $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "{{{pnid}}}" },
            "messages": [{{{messages}}}]
          }}]}]
        }
        """;
    }

    [Fact]
    public async Task Numarasiz_mesaj_sayaca_yazilir()
    {
        var (db, job, licenseId) = Build();

        await job.ProcessAsync(Payload(1753440000));

        var row = db.WaDroppedInbounds.Single();
        row.LicenseId.Should().Be(licenseId);
        row.BsuId.Should().Be(Bsuid);
        row.PhoneNumberId.Should().Be("PNID_1");
        row.MessageCount.Should().Be(1);
        row.FirstSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
        row.LastSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));

        db.WaMessages.Should().BeEmpty("mesaj hâlâ kaydedilemiyor; sayılan yalnızca kayıp");
    }

    /// <summary>
    /// <b>Bu testin koruduğu şey:</b> kaybın en saf hâli, içinde tek bir
    /// numarasız mesaj olan pakettir. <c>DroppedNoPhone</c> <c>IsEmpty</c>
    /// hesabına katılmasaydı <c>ProcessAsync</c> erken döner, <c>SaveChanges</c>
    /// hiç çalışmaz ve ölçüm tam da ölçmesi gereken durumda sessiz kalırdı.
    /// </summary>
    [Fact]
    public async Task Yalniz_numarasiz_mesaj_iceren_paket_erken_donmez()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440000));

        db.WaDroppedInbounds.Should().ContainSingle();
    }

    [Fact]
    public async Task Ayni_musteri_ikinci_kez_yazinca_sayac_artar_satir_artmaz()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440000));
        await job.ProcessAsync(Payload(1753440500));

        var row = db.WaDroppedInbounds.Single();
        row.MessageCount.Should().Be(2);
        row.FirstSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
        row.LastSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440500));
    }

    /// <summary>
    /// Webhook'lar sırasız gelebilir. Aralık iki uçtan da genişlemezse
    /// "ne zamandan beri kaybediyoruz" sorusunun cevabı bozulur — o soru da
    /// kullanıcı adı özelliğinin bölgemize ne zaman açıldığını gösterecek olan.
    /// </summary>
    [Fact]
    public async Task Gec_gelen_ESKI_paket_baslangici_geri_ceker()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440500));
        await job.ProcessAsync(Payload(1753440000));   // eski, geç geldi

        var row = db.WaDroppedInbounds.Single();
        row.FirstSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
        row.LastSeenAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440500));
    }

    /// <summary>
    /// <b>Bu testin koruduğu şey:</b> aynı müşteri aynı pakette birden çok mesaj
    /// yazabilir. Arama yalnız veritabanına bakarsa ilk <c>Add</c> henüz
    /// kaydedilmediği için ikinci mesaj da satır açar ve <c>SaveChanges</c>
    /// tekil indeksten patlar — ölçüm, ölçmeye çalıştığı olayda çöker.
    /// </summary>
    [Fact]
    public async Task Ayni_paketteki_iki_mesaj_tek_satir_yazar()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440000, extraWamIds: "wamid.NOPHONE2"));

        var row = db.WaDroppedInbounds.Should().ContainSingle().Subject;
        row.MessageCount.Should().Be(2);
    }

    /// <summary>Ne telefon ne BSUID: kimin mesajı olduğunu bilmiyoruz. Hepsini
    /// tek satırda toplamak "kaç müşteri" sorusunun cevabını bozardı.</summary>
    [Fact]
    public async Task Kimliksiz_mesaj_sayaca_yazilmaz()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440000, userId: null));

        db.WaDroppedInbounds.Should().BeEmpty();
    }

    /// <summary>Tanınmayan numaraya gelen kayıp başka bir kiracıya
    /// atfedilmemeli.</summary>
    [Fact]
    public async Task Taninmayan_numara_icin_yazilmaz()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(Payload(1753440000, pnid: "BASKA_PNID"));

        db.WaDroppedInbounds.Should().BeEmpty();
    }

    /// <summary>Numarası gelen normal mesaj sayaca dokunmamalı — aksi hâlde
    /// ölçüm olguyu olduğundan büyük gösterir.</summary>
    [Fact]
    public async Task Normal_mesaj_sayaca_yazilmaz()
    {
        var (db, job, _) = Build();

        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{ "from": "905321234567", "id": "wamid.NORMAL",
                           "timestamp": "1753440000", "type": "text",
                           "text": { "body": "merhaba" } }]
          }}]}]
        }
        """;

        await job.ProcessAsync(payload);

        db.WaDroppedInbounds.Should().BeEmpty();
        db.WaMessages.Should().ContainSingle();
    }
}
