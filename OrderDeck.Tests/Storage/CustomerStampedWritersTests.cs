using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Customer'a yazan yolların birim damgaları (bağlayıcı kural 3, U6, U7, U16, kural 8).
/// Göçün kendisi CustomerUnitStampsMigrationTests'te.
/// </summary>
public sealed class CustomerStampedWritersTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _repo;

    // Form gönderim anları (unix ms). Yalnız sıra önemli; ikisi de bugünden eski.
    private static readonly long T1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long T2 = T1 + 3_600_000;

    public CustomerStampedWritersTests()
    {
        new MigrationRunner(_db).Run();
        _repo = new CustomerRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private long? Stamp(string id, string column) => Stamp(_db, id, column);

    private static long? Stamp(InMemorySqlite db, string id, string column)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id });
    }

    /// <summary>Testin kendi veritabanına geç hata enjekte eder: <paramref name="id"/>'li satırın
    /// <paramref name="column"/>'u değişince ifade düşer (RAISE(ABORT) — yalnız o ifade geri alınır,
    /// işlem açık kalır; geri kalanı çağıranın işlemine kalır).</summary>
    private void FailWhenUpdated(string id, string column)
    {
        using var c = _db.Open();
        c.Execute($@"CREATE TRIGGER test_gec_hata AFTER UPDATE OF {column} ON Customer
                     WHEN new.Id = '{id}'
                     BEGIN SELECT RAISE(ABORT, 'enjekte gec hata'); END;");
    }

    private int CustomerCount()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer");
    }

    /// <summary>Yalnız damga kolonunu yazar (veri aynı → damga tetikleyicisi çalışmaz).
    /// null = "göç öncesi, damgasız" birim.</summary>
    private void SetStamp(string id, string column, long? value)
    {
        using var c = _db.Open();
        c.Execute($"UPDATE Customer SET {column} = @value WHERE Id = @id", new { id, value });
    }

    private string Chat(string username, string platform = "instagram")
    {
        var id = Guid.NewGuid().ToString("N");
        _repo.Insert(new Customer(id, platform, username, DisplayName: username, AvatarUrl: null,
            FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null));
        return id;
    }

    private string Form(string user, long at, string? fullName = "Örnek Müşteri", string? address = "Atatürk Cd. 1",
        string? phone = null, string? email = null, string? tckn = null, bool wa = false, bool sms = false,
        string? city = null, Guid? formId = null)
        => _repo.UpsertPersonFromIntake(
            new[] { ("instagram", user, (string?)null) },
            fullName ?? "", address ?? "", phone, email, tckn, wa, sms,
            nowUnix: 1000, formId: formId ?? Guid.NewGuid(), submittedAtMs: at, city: city);

    [Fact]
    public void Form_birimleri_SubmittedAt_damgasiyla_yazilir_dolu_takma_ada_dokunulmaz()
    {
        var id = Chat("ayse_y");
        var phone = TestPhone.NewE164();

        Form("ayse_y", T1, phone: phone, email: "ayse@example.test", tckn: TestTckn.NewValid(), wa: true, city: "İzmir");

        var c = _repo.GetById(id)!;
        c.FullName.Should().Be("Örnek Müşteri");
        c.Phone.Should().Be(phone);
        c.City.Should().Be("İzmir");
        foreach (var col in new[] { "FullNameChangedAt", "PhoneChangedAt", "EmailChangedAt", "TcknChangedAt",
                                    "AddressChangedAt", "GroupIdChangedAt", "WhatsAppConsentChangedAt",
                                    "SmsConsentChangedAt" })
            Stamp(id, col).Should().Be(T1, $"{col}: damga formun gönderim anı, işleme anı değil");
        c.DisplayName.Should().Be("ayse_y", "takma ad doluyken form ona dokunmaz");
    }

    [Fact]
    public void Eski_form_sonradan_yapilan_elle_duzenlemeyi_ezmez()
    {
        var id = Chat("ayse_y");
        var manual = TestPhone.NewE164();
        _repo.UpdatePhone(id, manual);              // tetikleyici: şimdi (T1'den yeni)

        Form("ayse_y", T1, phone: TestPhone.NewE164());       // geç açılan bilgisayarın oynattığı eski form

        _repo.GetById(id)!.Phone.Should().Be(manual);
    }

    [Fact]
    public void Damgasiz_yerel_birimi_form_ezer()
    {
        // Göç öncesi veri damgasızdır; form (damgalı) onun üstüne yazar — kural 3.
        var id = Chat("ayse_y");
        _repo.UpdatePhone(id, TestPhone.NewE164());
        SetStamp(id, "PhoneChangedAt", null);
        var phone = TestPhone.NewE164();

        Form("ayse_y", T1, phone: phone);

        _repo.GetById(id)!.Phone.Should().Be(phone);
        Stamp(id, "PhoneChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Bos_form_alani_yazilmaz()
    {
        var id = Chat("ayse_y");
        var phone = TestPhone.NewE164();
        Form("ayse_y", T1, phone: phone, email: "ayse@example.test");

        Form("ayse_y", T2, fullName: null, address: null, phone: null, email: null);

        var c = _repo.GetById(id)!;
        c.Phone.Should().Be(phone);
        c.Email.Should().Be("ayse@example.test");
        c.FullName.Should().Be("Örnek Müşteri");
        c.Address.Should().Be("Atatürk Cd. 1");
        Stamp(id, "PhoneChangedAt").Should().Be(T1);
        Stamp(id, "AddressChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Yeni_satir_yalniz_dolu_form_birimlerini_damgalar_izin_her_zaman_cevaptir()
    {
        Form("yeni_kisi", T1, phone: null, email: null);

        var c = _repo.FindByPlatformAndUsername("instagram", "yeni_kisi")!;
        Stamp(c.Id, "FullNameChangedAt").Should().Be(T1);
        Stamp(c.Id, "DisplayNameChangedAt").Should().Be(T1, "takma ad formun Ad Soyad'ıyla doldu");
        Stamp(c.Id, "GroupIdChangedAt").Should().Be(T1, "grup formdan türer — her zaman damgalı");
        Stamp(c.Id, "AddressChangedAt").Should().Be(T1);
        Stamp(c.Id, "PhoneChangedAt").Should().BeNull();
        Stamp(c.Id, "EmailChangedAt").Should().BeNull();
        Stamp(c.Id, "SmsConsentChangedAt").Should().Be(T1, "izin formun cevabıdır — 'hayır' da");
        Stamp(c.Id, "NotesChangedAt").Should().BeNull();
        Stamp(c.Id, "RecipientPaysChangedAt").Should().BeNull();
    }

    [Fact]
    public void Ayni_damgali_ikinci_form_yanki_sayilir()
    {
        Form("ayse_y", T1);
        var id = _repo.FindByPlatformAndUsername("instagram", "ayse_y")!.Id;

        Form("ayse_y", T1, fullName: "Başka Ad");

        _repo.GetById(id)!.FullName.Should().Be("Örnek Müşteri", "eşit damga yazım üretmez (kural 4)");
    }

    [Fact]
    public void Telefonla_gruplama_baska_satirin_grubunu_form_damgasiyla_yazar()
    {
        var phone = TestPhone.NewE164();
        var other = Chat("ayse_eski_hesap");
        _repo.UpdatePhone(other, phone);

        var groupId = Form("ayse_y", T1, phone: phone);

        _repo.GetById(other)!.GroupId.Should().Be(groupId);
        Stamp(other, "GroupIdChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Telefonla_gruplama_sonradan_ayrilmis_satiri_geri_baglamaz()
    {
        var phone = TestPhone.NewE164();
        var other = Chat("ayse_eski_hesap");
        _repo.UpdatePhone(other, phone);
        SetStamp(other, "GroupIdChangedAt", T2);    // operatör T2'de gruptan ayırdı

        Form("ayse_y", T1, phone: phone);

        _repo.GetById(other)!.GroupId.Should().BeNull();
    }

    [Fact]
    public void Backfill_doldurdugu_adi_form_damgasiyla_damgalar_damgali_bos_adi_doldurmaz()
    {
        var a = Chat("deneme.alici");
        var b = Chat("deneme.alici2");
        SetStamp(b, "FullNameChangedAt", T2);       // T2'de bilerek boş bırakıldı

        _repo.BackfillFullNameForIdentities(
                new[] { ("instagram", "deneme.alici"), ("instagram", "deneme.alici2") }, "Deneme Alıcı", submittedAtMs: T1)
            .Should().Be(1);

        _repo.GetById(a)!.FullName.Should().Be("Deneme Alıcı");
        Stamp(a, "FullNameChangedAt").Should().Be(T1);
        _repo.GetById(b)!.FullName.Should().BeNull();
    }

    [Fact]
    public void UpsertFromIntakeForm_veritabanindaki_hali_doner()
    {
        _repo.UpsertFromIntakeForm("alice", "Alice Yeni", "Yeni Adres", null, nowUnix: 1000, submittedAtMs: T2);

        var returned = _repo.UpsertFromIntakeForm("alice", "Alice Eski", "Eski Adres", null, nowUnix: 1001, submittedAtMs: T1);

        returned.DisplayName.Should().Be("Alice Yeni", "eski damgalı form yazılmadı; dönen nesne bunu yansıtmalı");
        returned.Address.Should().Be("Yeni Adres");
        returned.LastSeenAt.Should().Be(1001);
    }

    [Fact]
    public void Satir_acan_yollar_kimlik_anahtarini_yazar()
    {
        var chat = Chat("Ornek.MUSTERI");
        Form("Form.Kisi", T1);
        var form = _repo.FindByPlatformAndUsername("instagram", "Form.Kisi")!.Id;
        var legacy = _repo.UpsertFromIntakeForm("Eski.Form", "Ad", "Adres", null, nowUnix: 1000, submittedAtMs: 1_000_000).Id;

        using var c = _db.Open();
        string? Key(string id) => c.ExecuteScalar<string?>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id });
        Key(chat).Should().Be("ornek.musteri");
        Key(form).Should().Be("form.kisi");
        Key(legacy).Should().Be("eski.form");
    }

    [Fact]
    public void FindByPlatformAndUsername_harf_farkli_kaydi_kimlik_anahtariyla_bulur()
    {
        var id = Chat("Ornek.Musteri");

        _repo.FindByPlatformAndUsername("instagram", "ornek.musteri")!.Id.Should().Be(id);
        _repo.FindByPlatformAndUsername("instagram", "ORNEK.MUSTERI ")!.Id.Should().Be(id);
        _repo.FindByPlatformAndUsername("instagram", "baskasi").Should().BeNull();
        _repo.FindByPlatformAndUsername("tiktok", "ornek.musteri").Should().BeNull("platform kimliğin parçası");
    }

    [Fact]
    public void FindByPlatformAndUsername_birebir_eslesmeyi_tercih_eder()
    {
        var upper = Chat("Ayse");
        var lower = Chat("ayse");

        _repo.FindByPlatformAndUsername("instagram", "ayse")!.Id.Should().Be(lower);
        _repo.FindByPlatformAndUsername("instagram", "Ayse")!.Id.Should().Be(upper);
    }

    [Fact]
    public void RecordPurge_kilit_altinda_damgalamaz_SyncSeq_ilerletmez_kimlik_anahtariyla_esler()
    {
        // NOCASE yalnız ASCII katlar: "ŞEYMA" ile "şeyma"yı eşleyen kimlik anahtarı.
        var id = Chat("ŞEYMA");
        _repo.UpdatePhone(id, TestPhone.NewE164());
        var seq = _repo.GetById(id)!.SyncSeq;
        var phoneStamp = Stamp(id, "PhoneChangedAt");

        _repo.RecordPurge("instagram", "şeyma", purgedAtUnix: 5000).Should().Be(1);

        var c = _repo.GetById(id)!;
        c.Phone.Should().BeNull();
        c.DisplayName.Should().Be("[Silindi]");
        c.SyncSeq.Should().Be(seq, "silinmiş satırı sunucuya geri göndermek boşuna tur");
        Stamp(id, "PhoneChangedAt").Should().Be(phoneStamp, "akıştan inen silme düzenleme değildir");
        using var conn = _db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(1);
    }

    [Fact]
    public void Mezar_tasi_ASCII_disi_harf_farkini_da_yakalar()
    {
        // U16: NOCASE "ŞEYMA" ile "şeyma"yı eşlemez; kimlik anahtarı eşler. Yerelde satır yokken
        // inen karar, sonradan sohbetten açılan satırı boş doğurur.
        _repo.RecordPurge("instagram", "ŞEYMA", purgedAtUnix: 5000).Should().Be(0, "yerelde satır yok — karar yine yazılır");

        var id = Chat("şeyma");

        var c = _repo.GetById(id)!;
        c.DisplayName.Should().Be("[Silindi]");
        c.Phone.Should().BeNull();
        using var conn = _db.Open();
        conn.ExecuteScalar<long?>("SELECT PurgedAt FROM Customer WHERE Id = @id", new { id }).Should().Be(5000);
        conn.ExecuteScalar<string>("SELECT IdentityKey FROM CustomerPurgeTombstone").Should().Be("şeyma");
    }

    // ── Form yolu: grup kimliği formdan türer, tek işlemde uygulanır, kara liste yayılımı form
    //    damgasıyla (C2 kalite incelemesi) ──

    [Fact]
    public void Ayni_formu_isleyen_iki_bilgisayar_ayni_grubu_ve_damgayi_yazar()
    {
        // İki bilgisayar aynı formu (aynı Id, aynı SubmittedAt) birbirinin gönderimini görmeden
        // işler. Yeni grup rastgele olsaydı eşit damgalı iki farklı GroupId doğardı; sunucu da
        // istemci de eşit damgayı yok saydığı için iki taraf hiç yakınsamazdı (kart bölünür).
        using var otherDb = new InMemorySqlite();
        new MigrationRunner(otherDb).Run();
        var other = new CustomerRepository(otherDb);
        var chatId = Guid.NewGuid().ToString("N");
        foreach (var repo in new[] { _repo, other })
            repo.Insert(new Customer(chatId, "instagram", "ayse_y", DisplayName: "ayse_y", AvatarUrl: null,
                FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: false, BlacklistReason: null, Notes: null,
                TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null));
        var formId = Guid.NewGuid();
        var phone = TestPhone.NewE164();
        string Apply(CustomerRepository repo) => repo.UpsertPersonFromIntake(
            new[] { ("instagram", "ayse_y", (string?)null), ("tiktok", "ayse_tt", (string?)null) },
            "Örnek Müşteri", "Atatürk Cd. 1", phone, null, null, false, false,
            nowUnix: 1000, formId: formId, submittedAtMs: T1);

        var here = Apply(_repo);
        var there = Apply(other);

        here.Should().Be(formId.ToString("N")).And.Be(there);
        _repo.GetById(chatId)!.GroupId.Should().Be(here);
        other.GetById(chatId)!.GroupId.Should().Be(there);
        Stamp(_db, chatId, "GroupIdChangedAt").Should().Be(T1);
        Stamp(otherDb, chatId, "GroupIdChangedAt").Should().Be(T1);
        _repo.FindByPlatformAndUsername("tiktok", "ayse_tt")!.GroupId.Should().Be(here);
        other.FindByPlatformAndUsername("tiktok", "ayse_tt")!.GroupId.Should().Be(there);

        // Aynı kimliğin sonraki formu (başka Id) ilk grubu korur.
        Form("ayse_y", T2).Should().Be(here);
    }

    [Fact]
    public void Bos_form_kimligi_reddedilir()
    {
        // Yeni grup formun kimliğinden türer: boş kimlik, ilgisiz kişileri tek grupta toplardı.
        var act = () => Form("ayse_y", T1, formId: Guid.Empty);

        act.Should().Throw<ArgumentException>();
        CustomerCount().Should().Be(0);
    }

    [Fact]
    public void Form_tek_islemde_uygulanir_gec_hata_onceki_yazimlari_geri_alir()
    {
        // Form ya bütünüyle uygulanır ya hiç. Burada telefonla gruplamanın BAŞKA satıra yazımı
        // (formun son adımlarından) patlıyor: aynı formun kimlik satırı yazımları da geri alınmalı.
        // Ayrı ifadelerde kalsaydı yazılan yarım form kalırdı; araya giren bir yeniden anahtarlama
        // (kendi BEGIN IMMEDIATE'i) da Id ile yazımı boşa düşürüp formu sessizce kaybettirebilirdi.
        var existing = Chat("ayse_y");
        var victim = Chat("ayse_eski_hesap");
        var phone = TestPhone.NewE164();
        _repo.UpdatePhone(victim, phone);
        FailWhenUpdated(victim, "GroupId");

        var act = () => _repo.UpsertPersonFromIntake(
            new[] { ("instagram", "ayse_y", (string?)null), ("tiktok", "ayse_tt", (string?)null) },
            "Örnek Müşteri", "Atatürk Cd. 1", phone, null, null, false, false,
            nowUnix: 1000, formId: Guid.NewGuid(), submittedAtMs: T1);

        act.Should().Throw<SqliteException>().WithMessage("*enjekte gec hata*");
        var row = _repo.GetById(existing)!;
        row.Phone.Should().BeNull("aynı formun önceki yazımı geri alındı");
        row.FullName.Should().BeNull();
        row.GroupId.Should().BeNull();
        Stamp(existing, "PhoneChangedAt").Should().BeNull();
        _repo.FindByPlatformAndUsername("tiktok", "ayse_tt").Should().BeNull("açılan satır da geri alındı");
        CustomerCount().Should().Be(2);
    }

    [Fact]
    public void Backfill_tek_islemde_uygulanir_gec_hata_grup_yazimini_geri_alir()
    {
        var grouped = Chat("deneme.alici");
        _repo.SetGroupId(grouped, Guid.NewGuid().ToString("N"));
        var solo = Chat("deneme.alici2");
        FailWhenUpdated(solo, "FullName");     // grup satırları önce, tekil satır sonra yazılır

        var act = () => _repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "deneme.alici"), ("instagram", "deneme.alici2") }, "Deneme Alıcı", submittedAtMs: T1);

        act.Should().Throw<SqliteException>().WithMessage("*enjekte gec hata*");
        _repo.GetById(grouped)!.FullName.Should().BeNull("grup yazımı aynı işlemdeydi");
        Stamp(grouped, "FullNameChangedAt").Should().BeNull();
    }

    [Fact]
    public void Kara_liste_yayilimi_form_damgasini_tasir_daha_yeni_kaldirmayi_ezmez()
    {
        // Eski formun geç oynatılması, başka bilgisayarda SONRADAN kara listeden çıkarılmış satırı
        // yeniden kara listeye almamalı: yayılım formdan türeyen yazımdır → formun damgası ve
        // "yalnız daha yeniyse" kuralı.
        var phone = TestPhone.NewE164();
        var source = Guid.NewGuid().ToString("N");
        _repo.Insert(new Customer(source, "instagram", "kotu", DisplayName: "kotu", AvatarUrl: null,
            FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: true, BlacklistReason: "dolandırıcı", Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: 999, Address: null, Phone: phone));
        var cleared = Chat("kotu_eski_hesap");
        _repo.UpdatePhone(cleared, phone);
        SetStamp(cleared, "BlacklistChangedAt", T2);    // başka bilgisayarda T2'de kara listeden çıkarıldı

        Form("kotu_yeni", T1, phone: phone);

        _repo.GetById(cleared)!.IsBlacklisted.Should().BeFalse();
        Stamp(cleared, "BlacklistChangedAt").Should().Be(T2);
        var fresh = _repo.FindByPlatformAndUsername("instagram", "kotu_yeni")!;
        fresh.IsBlacklisted.Should().BeTrue("grubun kara listesi yayılır");
        fresh.BlacklistReason.Should().Be("dolandırıcı");
        fresh.BlacklistedAt.Should().Be(999);
        Stamp(fresh.Id, "BlacklistChangedAt").Should().Be(T1, "yayılım formun damgasını taşır, işleme anını değil");
    }

    [Fact]
    public void Kara_liste_yayilimi_tarihsiz_kaynakta_formun_anini_yazar()
    {
        // Kaynak üyede kara liste tarihi yoksa yayılan tarih de formdan türer (gönderim anı, sn).
        // İşleme anı olsaydı aynı formu işleyen iki bilgisayar eşit damgayla farklı değer yazardı.
        var phone = TestPhone.NewE164();
        _repo.Insert(new Customer(Guid.NewGuid().ToString("N"), "instagram", "kotu", DisplayName: "kotu",
            AvatarUrl: null, FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: true, BlacklistReason: "dolandırıcı",
            Notes: null, TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: phone));

        Form("kotu_yeni", T1, phone: phone);

        _repo.FindByPlatformAndUsername("instagram", "kotu_yeni")!.BlacklistedAt.Should().Be(T1 / 1000);
    }

    [Fact]
    public void Kara_liste_yayilimi_esit_tarihte_kaynagi_Id_ile_secer()
    {
        // Aynı tarihli iki kara liste üyesi: kaynak tarama sırasına (ekleme sırası) bırakılsaydı
        // aynı veriye sahip iki bilgisayar farklı sebep yayardı — eşit damgayla, yakınsamadan.
        var phone = TestPhone.NewE164();
        var formId = Guid.NewGuid();
        var members = new[] { ("kaynak-1", "kotu_a", "sebep-a"), ("kaynak-2", "kotu_b", "sebep-b") };
        string PropagatedReason(bool reverseInsertOrder)
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var repo = new CustomerRepository(db);
            var order = reverseInsertOrder ? new[] { members[1], members[0] } : members;
            foreach (var (id, user, reason) in order)
                repo.Insert(new Customer(id, "instagram", user, DisplayName: user, AvatarUrl: null,
                    FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: true, BlacklistReason: reason, Notes: null,
                    TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: 999, Address: null, Phone: phone));
            repo.UpsertPersonFromIntake(
                new[] { ("instagram", "kotu_yeni", (string?)null) },
                "Örnek Müşteri", "Atatürk Cd. 1", phone, null, null, false, false,
                nowUnix: 1000, formId: formId, submittedAtMs: T1);
            return repo.FindByPlatformAndUsername("instagram", "kotu_yeni")!.BlacklistReason!;
        }

        PropagatedReason(reverseInsertOrder: false).Should().Be("sebep-a");
        PropagatedReason(reverseInsertOrder: true).Should().Be("sebep-a", "eşit tarihte en küçük Id — ekleme sırası değil");
    }

    [Fact]
    public void UpsertFromIntakeForm_harf_farkli_kullanici_adi_mevcut_satiri_gunceller()
    {
        var first = _repo.UpsertFromIntakeForm("Ayse.Form", "Ayşe", "Adres 1", null, nowUnix: 1000, submittedAtMs: T1);

        var second = _repo.UpsertFromIntakeForm("ayse.form", "Örnek Müşteri", "Adres 2", null, nowUnix: 1001, submittedAtMs: T2);

        second.Id.Should().Be(first.Id, "kimlik anahtarı aynı kişi diyor — ikinci satır açılmaz");
        second.DisplayName.Should().Be("Örnek Müşteri");
        second.Address.Should().Be("Adres 2");
        CustomerCount().Should().Be(1);
    }

    [Fact]
    public void Form_ASCII_disi_harf_farkli_sohbet_satirini_kimlik_anahtariyla_gunceller()
    {
        // NOCASE "ŞEYMA" ile "şeyma"yı eşlemez; FindExistingForIntake'in kimlik anahtarı adımı eşler.
        var id = Chat("ŞEYMA");
        var phone = TestPhone.NewE164();

        Form("şeyma", T1, phone: phone);

        _repo.GetById(id)!.Phone.Should().Be(phone);
        CustomerCount().Should().Be(1, "aynı kişi için ikinci satır açılmaz");
    }

    [Fact]
    public void Form_silinmis_kimlik_varyantina_yazmaz_yeni_satir_da_acmaz()
    {
        var id = Chat("ŞEYMA");
        _repo.ScrubPersonalData(id);                    // satır silinmiş (PurgedAt); mezar taşı yok

        Form("şeyma", T1, phone: TestPhone.NewE164());

        var c = _repo.GetById(id)!;
        c.Phone.Should().BeNull();
        c.FullName.Should().BeNull();
        c.DisplayName.Should().Be("[Silindi]");
        CustomerCount().Should().Be(1, "silinmiş satır kimliğin bariyeri — form yeni satır açmaz");
    }

    [Fact]
    public void Adres_blogu_yalniz_il_dolu_formda_da_yazilir()
    {
        // Adres bloğu tek birim: il tek başına doluysa blok formun hâliyle yazılır (boş parça NULL).
        var id = Chat("ayse_y");

        Form("ayse_y", T1, address: null, city: "Ankara");

        var c = _repo.GetById(id)!;
        c.Address.Should().BeNull();
        c.City.Should().Be("Ankara");
        c.District.Should().BeNull();
        Stamp(id, "AddressChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Adres_blogu_daha_yeni_form_ili_bosaltir()
    {
        var id = Chat("ayse_y");
        Form("ayse_y", T1, address: "Eski Cd. 1", city: "İzmir");

        Form("ayse_y", T2, address: "Yeni Cd. 2", city: null);

        var c = _repo.GetById(id)!;
        c.Address.Should().Be("Yeni Cd. 2");
        c.City.Should().BeNull("blok formun hâliyle yazılır — formda il yok");
        Stamp(id, "AddressChangedAt").Should().Be(T2);
    }

    // ── taze bilgisayarın ilk oynatması: doldurma kipi (U14) ────────────

    private string FillForm(string user, long at, string? phone = null, string? email = null, string? address = "Atatürk Cd. 1")
        => _repo.UpsertPersonFromIntake(
            new[] { ("instagram", user, (string?)null) },
            "Örnek Müşteri", address ?? "", phone, email, tckn: null, whatsAppConsent: true, smsConsent: false,
            nowUnix: 1000, formId: Guid.NewGuid(), submittedAtMs: at, mode: IntakeApplyMode.FillOnly);

    private long Seq(string id)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id = @id", new { id });
    }

    private int GuardRows()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard");
    }

    [Fact]
    public void Doldurma_kipi_damgasiz_dolu_birimi_ezmez_bos_birimi_damgasiz_doldurur()
    {
        var id = Chat("ayse_y");
        var legacyPhone = TestPhone.NewE164();
        _repo.UpdatePhone(id, legacyPhone);
        SetStamp(id, "PhoneChangedAt", null);                // göç öncesi elle girilmiş, damgasız
        var seq = Seq(id);
        var lastSeen = _repo.GetById(id)!.LastSeenAt;

        FillForm("ayse_y", T1, phone: TestPhone.NewE164(), email: "ayse@example.test");

        var c = _repo.GetById(id)!;
        c.Phone.Should().Be(legacyPhone, "kural 3'ün damgalı yazımı eski formla bunu ezerdi");
        c.Email.Should().Be("ayse@example.test");
        c.FullName.Should().Be("Örnek Müşteri");
        c.WhatsAppConsent.Should().BeTrue();
        Stamp(id, "EmailChangedAt").Should().BeNull("doldurma damga yazmaz");
        Stamp(id, "FullNameChangedAt").Should().BeNull();
        Stamp(id, "WhatsAppConsentChangedAt").Should().BeNull();
        Stamp(id, "PhoneChangedAt").Should().BeNull();
        Seq(id).Should().BeGreaterThan(seq, "doldurulan değerler gönderilsin (sunucuda da yalnız boşu doldurur)");
        c.LastSeenAt.Should().Be(lastSeen, "eski formun oynatılması 'son görülme' değildir (2. inceleme)");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Doldurma_kipi_doldurulacak_birim_yoksa_satiri_yazmaz_gonderime_koymaz()
    {
        var id = Chat("ayse_y");
        FillForm("ayse_y", T1);                               // ad, adres, grup, izin dolar
        var seq = Seq(id);
        var lastSeen = _repo.GetById(id)!.LastSeenAt;

        FillForm("ayse_y", T1);                               // aynı form yeniden: doldurulacak bir şey yok

        Seq(id).Should().Be(seq, "değişmeyen satır yeniden gönderilmez");
        _repo.GetById(id)!.LastSeenAt.Should().Be(lastSeen);
    }

    [Fact]
    public void Doldurma_kipi_damgali_bos_birimi_doldurmaz()
    {
        var id = Chat("ayse_y");
        SetStamp(id, "EmailChangedAt", T2);                  // e-posta T2'de bilerek boşaltıldı

        FillForm("ayse_y", T1, email: "ayse@example.test");

        _repo.GetById(id)!.Email.Should().BeNull();
        Stamp(id, "EmailChangedAt").Should().Be(T2);
    }

    [Fact]
    public void Doldurma_kipi_yeni_satiri_damgasiz_acar()
    {
        FillForm("yeni_kisi", T1, phone: TestPhone.NewE164());

        var c = _repo.FindByPlatformAndUsername("instagram", "yeni_kisi")!;
        c.Phone.Should().NotBeNull();
        c.FirstSeenAt.Should().Be(T1 / 1000, "oynatmanın açtığı satırın görülme anı formun gönderim anı");
        c.LastSeenAt.Should().Be(T1 / 1000);
        foreach (var col in new[] { "FullNameChangedAt", "DisplayNameChangedAt", "AddressChangedAt", "PhoneChangedAt",
                                    "GroupIdChangedAt", "WhatsAppConsentChangedAt", "SmsConsentChangedAt" })
            Stamp(c.Id, col).Should().BeNull($"{col}: kilit altında açıldı — INSERT tetikleyicisi damgalamadı");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Doldurma_kipi_telefonla_yalniz_bos_grubu_doldurur_baska_grubu_birlestirmez()
    {
        var phone = TestPhone.NewE164();
        var first = Chat("ilk_grup");
        _repo.UpdatePhone(first, phone);
        _repo.SetGroupId(first, "g0");
        SetStamp(first, "GroupIdChangedAt", null);            // göç öncesi gruplanmış: damgasız ama dolu
        var second = Chat("ikinci_grup");
        _repo.UpdatePhone(second, phone);
        _repo.SetGroupId(second, "g9");
        SetStamp(second, "GroupIdChangedAt", null);
        var loose = Chat("grupsuz");
        _repo.UpdatePhone(loose, phone);

        var groupId = FillForm("ayse_y", T1, phone: phone);

        _repo.GetById(first)!.GroupId.Should().Be("g0", "doldurma kipi dolu grubu değiştirmez");
        _repo.GetById(second)!.GroupId.Should().Be("g9", "doldurma kipi grupları birleştirmez");
        _repo.GetById(loose)!.GroupId.Should().Be(groupId, "boş ve damgasız grup doldurulur");
        Stamp(loose, "GroupIdChangedAt").Should().BeNull();
    }

    [Fact]
    public void Doldurma_kipi_kara_liste_yayilimi_telefonsuz_formda_da_kosar_damgasiz_ve_damgali_satiri_ezmez()
    {
        // #495: yayılım her formda (telefonsuz form da kimliği gruba koyar). Doldurma kipinde kilit
        // altında — damga yazılmaz; damgalı (başka bilgisayarda bilerek kara listeden çıkarılmış)
        // satır yeniden kara listeye alınmaz (yalnız boş VE damgasız birim).
        var group = Guid.NewGuid().ToString("N");
        _repo.Insert(new Customer(Guid.NewGuid().ToString("N"), "instagram", "kotu", DisplayName: "kotu",
            AvatarUrl: null, FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: true, BlacklistReason: "dolandırıcı",
            Notes: null, TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: 999, Address: null, Phone: null,
            GroupId: group));
        var cleared = Chat("kotu_eski_hesap");
        _repo.SetGroupId(cleared, group);
        SetStamp(cleared, "BlacklistChangedAt", T2);          // başka bilgisayarda T2'de kara listeden çıkarıldı

        _repo.UpsertPersonFromIntake(
            new[] { ("instagram", "kotu", (string?)null), ("tiktok", "kotu_tt", (string?)null) },
            "Örnek Müşteri", "", phone: null, email: null, tckn: null, whatsAppConsent: false, smsConsent: false,
            nowUnix: 1000, formId: Guid.NewGuid(), submittedAtMs: T1, mode: IntakeApplyMode.FillOnly)
            .Should().Be(group);

        var fresh = _repo.FindByPlatformAndUsername("tiktok", "kotu_tt")!;
        fresh.GroupId.Should().Be(group);
        fresh.IsBlacklisted.Should().BeTrue("grubun kara listesi telefonsuz formda da yayılır");
        fresh.BlacklistReason.Should().Be("dolandırıcı");
        Stamp(fresh.Id, "BlacklistChangedAt").Should().BeNull("doldurma kipi damga yazmaz");
        _repo.GetById(cleared)!.IsBlacklisted.Should().BeFalse();
        Stamp(cleared, "BlacklistChangedAt").Should().Be(T2);
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Doldurma_kipi_backfill_damgasiz_bos_adi_doldurur_damga_yazmaz()
    {
        var a = Chat("deneme.alici");
        var b = Chat("deneme.alici2");
        SetStamp(b, "FullNameChangedAt", T2);                // T2'de bilerek boş bırakıldı
        var seq = Seq(a);

        _repo.BackfillFullNameForIdentities(
                new[] { ("instagram", "deneme.alici"), ("instagram", "deneme.alici2") }, "Deneme Alıcı",
                submittedAtMs: T1, mode: IntakeApplyMode.FillOnly)
            .Should().Be(1);

        _repo.GetById(a)!.FullName.Should().Be("Deneme Alıcı");
        Stamp(a, "FullNameChangedAt").Should().BeNull();
        Seq(a).Should().BeGreaterThan(seq, "doldurulan ad gönderilsin");
        _repo.GetById(b)!.FullName.Should().BeNull("damgalı boş ad bilinçli silmedir");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Doldurma_kipi_eski_form_yolu_damgasiz_acar_dolu_birimi_ezmez_LastSeenAt_ilerletmez()
    {
        var opened = _repo.UpsertFromIntakeForm("eski.form", "Örnek Müşteri", "Eski Cd. 1", null,
            nowUnix: 1000, submittedAtMs: T1, mode: IntakeApplyMode.FillOnly);

        opened.FirstSeenAt.Should().Be(T1 / 1000);
        opened.LastSeenAt.Should().Be(T1 / 1000);
        foreach (var col in new[] { "DisplayNameChangedAt", "AddressChangedAt" })
            Stamp(opened.Id, col).Should().BeNull($"{col}: kilit altında açıldı");
        var seq = Seq(opened.Id);
        var phone = TestPhone.NewE164();

        var again = _repo.UpsertFromIntakeForm("eski.form", "Başka Ad", "Yeni Cd. 2", phone,
            nowUnix: 2000, submittedAtMs: T2, mode: IntakeApplyMode.FillOnly);

        again.Id.Should().Be(opened.Id);
        again.DisplayName.Should().Be("Örnek Müşteri", "dolu takma ad ezilmez");
        again.Address.Should().Be("Eski Cd. 1", "dolu adres ezilmez");
        again.Phone.Should().Be(phone, "boş ve damgasız telefon doldurulur");
        Stamp(opened.Id, "PhoneChangedAt").Should().BeNull();
        again.LastSeenAt.Should().Be(T1 / 1000, "doldurma LastSeenAt'i ilerletmez");
        Seq(opened.Id).Should().BeGreaterThan(seq);
        GuardRows().Should().Be(0);
    }
}
