using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
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

    private long? Stamp(string id, string column)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id });
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
        string? city = null)
        => _repo.UpsertPersonFromIntake(
            new[] { ("instagram", user, (string?)null) },
            fullName ?? "", address ?? "", phone, email, tckn, wa, sms,
            nowUnix: 1000, city: city, submittedAtMs: at);

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
        var legacy = _repo.UpsertFromIntakeForm("Eski.Form", "Ad", "Adres", null, nowUnix: 1000).Id;

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
}
