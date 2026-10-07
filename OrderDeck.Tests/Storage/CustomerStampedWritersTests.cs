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

    // Telefon / TCKN sabit YAZILMAZ (CLAUDE.md, repo public): üretilir.
    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    private static string NewTckn() => Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

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

    private string Form(string user, long at, string? fullName = "Ayşe Yılmaz", string? address = "Atatürk Cd. 1",
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
        var phone = NewPhone();

        Form("ayse_y", T1, phone: phone, email: "ayse@example.test", tckn: NewTckn(), wa: true, city: "İzmir");

        var c = _repo.GetById(id)!;
        c.FullName.Should().Be("Ayşe Yılmaz");
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
        var manual = NewPhone();
        _repo.UpdatePhone(id, manual);              // tetikleyici: şimdi (T1'den yeni)

        Form("ayse_y", T1, phone: NewPhone());       // geç açılan bilgisayarın oynattığı eski form

        _repo.GetById(id)!.Phone.Should().Be(manual);
    }

    [Fact]
    public void Damgasiz_yerel_birimi_form_ezer()
    {
        // Göç öncesi veri damgasızdır; form (damgalı) onun üstüne yazar — kural 3.
        var id = Chat("ayse_y");
        _repo.UpdatePhone(id, NewPhone());
        SetStamp(id, "PhoneChangedAt", null);
        var phone = NewPhone();

        Form("ayse_y", T1, phone: phone);

        _repo.GetById(id)!.Phone.Should().Be(phone);
        Stamp(id, "PhoneChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Bos_form_alani_yazilmaz()
    {
        var id = Chat("ayse_y");
        var phone = NewPhone();
        Form("ayse_y", T1, phone: phone, email: "ayse@example.test");

        Form("ayse_y", T2, fullName: null, address: null, phone: null, email: null);

        var c = _repo.GetById(id)!;
        c.Phone.Should().Be(phone);
        c.Email.Should().Be("ayse@example.test");
        c.FullName.Should().Be("Ayşe Yılmaz");
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

        _repo.GetById(id)!.FullName.Should().Be("Ayşe Yılmaz", "eşit damga yazım üretmez (kural 4)");
    }

    [Fact]
    public void Telefonla_gruplama_baska_satirin_grubunu_form_damgasiyla_yazar()
    {
        var phone = NewPhone();
        var other = Chat("ayse_eski_hesap");
        _repo.UpdatePhone(other, phone);

        var groupId = Form("ayse_y", T1, phone: phone);

        _repo.GetById(other)!.GroupId.Should().Be(groupId);
        Stamp(other, "GroupIdChangedAt").Should().Be(T1);
    }

    [Fact]
    public void Telefonla_gruplama_sonradan_ayrilmis_satiri_geri_baglamaz()
    {
        var phone = NewPhone();
        var other = Chat("ayse_eski_hesap");
        _repo.UpdatePhone(other, phone);
        SetStamp(other, "GroupIdChangedAt", T2);    // operatör T2'de gruptan ayırdı

        Form("ayse_y", T1, phone: phone);

        _repo.GetById(other)!.GroupId.Should().BeNull();
    }

    [Fact]
    public void Backfill_doldurdugu_adi_form_damgasiyla_damgalar_damgali_bos_adi_doldurmaz()
    {
        var a = Chat("musa");
        var b = Chat("musa2");
        SetStamp(b, "FullNameChangedAt", T2);       // T2'de bilerek boş bırakıldı

        _repo.BackfillFullNameForIdentities(
                new[] { ("instagram", "musa"), ("instagram", "musa2") }, "Musa S", submittedAtMs: T1)
            .Should().Be(1);

        _repo.GetById(a)!.FullName.Should().Be("Musa S");
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
        var chat = Chat("Ayse.KAYA");
        Form("Form.Kisi", T1);
        var form = _repo.FindByPlatformAndUsername("instagram", "Form.Kisi")!.Id;
        var legacy = _repo.UpsertFromIntakeForm("Eski.Form", "Ad", "Adres", null, nowUnix: 1000, submittedAtMs: 1_000_000).Id;

        using var c = _db.Open();
        string? Key(string id) => c.ExecuteScalar<string?>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id });
        Key(chat).Should().Be("ayse.kaya");
        Key(form).Should().Be("form.kisi");
        Key(legacy).Should().Be("eski.form");
    }

    [Fact]
    public void FindByPlatformAndUsername_harf_farkli_kaydi_kimlik_anahtariyla_bulur()
    {
        var id = Chat("Ayse.Kaya");

        _repo.FindByPlatformAndUsername("instagram", "ayse.kaya")!.Id.Should().Be(id);
        _repo.FindByPlatformAndUsername("instagram", "AYSE.KAYA ")!.Id.Should().Be(id);
        _repo.FindByPlatformAndUsername("instagram", "baskasi").Should().BeNull();
        _repo.FindByPlatformAndUsername("tiktok", "ayse.kaya").Should().BeNull("platform kimliğin parçası");
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
        _repo.UpdatePhone(id, NewPhone());
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
        var phone = NewPhone();
        string Apply(CustomerRepository repo) => repo.UpsertPersonFromIntake(
            new[] { ("instagram", "ayse_y", (string?)null), ("tiktok", "ayse_tt", (string?)null) },
            "Ayşe Yılmaz", "Atatürk Cd. 1", phone, null, null, false, false,
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
        var phone = NewPhone();
        _repo.UpdatePhone(victim, phone);
        FailWhenUpdated(victim, "GroupId");

        var act = () => _repo.UpsertPersonFromIntake(
            new[] { ("instagram", "ayse_y", (string?)null), ("tiktok", "ayse_tt", (string?)null) },
            "Ayşe Yılmaz", "Atatürk Cd. 1", phone, null, null, false, false,
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
        var grouped = Chat("musa");
        _repo.SetGroupId(grouped, Guid.NewGuid().ToString("N"));
        var solo = Chat("musa2");
        FailWhenUpdated(solo, "FullName");     // grup satırları önce, tekil satır sonra yazılır

        var act = () => _repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "musa"), ("instagram", "musa2") }, "Musa S", submittedAtMs: T1);

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
        var phone = NewPhone();
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
        var phone = NewPhone();
        _repo.Insert(new Customer(Guid.NewGuid().ToString("N"), "instagram", "kotu", DisplayName: "kotu",
            AvatarUrl: null, FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: true, BlacklistReason: "dolandırıcı",
            Notes: null, TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: phone));

        Form("kotu_yeni", T1, phone: phone);

        _repo.FindByPlatformAndUsername("instagram", "kotu_yeni")!.BlacklistedAt.Should().Be(T1 / 1000);
    }

    [Fact]
    public void UpsertFromIntakeForm_harf_farkli_kullanici_adi_mevcut_satiri_gunceller()
    {
        var first = _repo.UpsertFromIntakeForm("Ayse.Form", "Ayşe", "Adres 1", null, nowUnix: 1000, submittedAtMs: T1);

        var second = _repo.UpsertFromIntakeForm("ayse.form", "Ayşe Yılmaz", "Adres 2", null, nowUnix: 1001, submittedAtMs: T2);

        second.Id.Should().Be(first.Id, "kimlik anahtarı aynı kişi diyor — ikinci satır açılmaz");
        second.DisplayName.Should().Be("Ayşe Yılmaz");
        second.Address.Should().Be("Adres 2");
        CustomerCount().Should().Be(1);
    }

    [Fact]
    public void Form_ASCII_disi_harf_farkli_sohbet_satirini_kimlik_anahtariyla_gunceller()
    {
        // NOCASE "ŞEYMA" ile "şeyma"yı eşlemez; FindExistingForIntake'in kimlik anahtarı adımı eşler.
        var id = Chat("ŞEYMA");
        var phone = NewPhone();

        Form("şeyma", T1, phone: phone);

        _repo.GetById(id)!.Phone.Should().Be(phone);
        CustomerCount().Should().Be(1, "aynı kişi için ikinci satır açılmaz");
    }

    [Fact]
    public void Form_silinmis_kimlik_varyantina_yazmaz_yeni_satir_da_acmaz()
    {
        var id = Chat("ŞEYMA");
        _repo.ScrubPersonalData(id);                    // satır silinmiş (PurgedAt); mezar taşı yok

        Form("şeyma", T1, phone: NewPhone());

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
}
