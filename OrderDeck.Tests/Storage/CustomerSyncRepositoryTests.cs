using System;
using System.Linq;
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
/// Çoklu bilgisayar senkronunun müşteri deposu (Bölüm C, C4): gönderim okuması, sunucu satırını
/// uygulama, yeniden anahtarlama, miras satırı dönüştürme, yönlendirme kaydı, meşgul müşteri.
/// SyncSeq ardışık değildir: beklenen değer işlemden ÖNCE veritabanından okunur, sabit yazılmaz.
/// </summary>
public sealed class CustomerSyncRepositoryTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly CustomerBusySet _busy = new();
    private readonly CustomerSyncRepository _sync;

    private const long Now = 1_791_000_000;          // unix s (FirstSeen/LastSeen)
    private const long T1 = 1_759_312_800_000;       // birim damgaları (unix ms)
    private const long T2 = T1 + 3_600_000;
    private const long T3 = T1 + 7_200_000;
    private const long Pushed = long.MaxValue;       // "her yerel satır gönderildi" filigranı
    private const long NothingPushed = 0;            // göç 045 sonrası ilk açılış

    public CustomerSyncRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _sync = new CustomerSyncRepository(_db, _busy);
    }

    public void Dispose() => _db.Dispose();

    private static string NewId() => Guid.NewGuid().ToString("N");

    // Telefon sabit YAZILMAZ (CLAUDE.md, repo public): üretilir.
    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private string Local(string username, string? displayName = "takma", string? id = null, string? avatar = null)
    {
        id ??= NewId();
        _customers.Insert(new Customer(id, "tiktok", username, displayName, AvatarUrl: avatar,
            FirstSeenAt: 100, LastSeenAt: 200, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 2, TotalAmount: 100m, BlacklistedAt: null, Address: null, Phone: null));
        return id;
    }

    /// <summary>Eski <c>since</c> ingest'inin açtığı satır (ShopperRegistrationIngestService.cs:103-118):
    /// geçici Id, beyan adı DisplayName'de, adres, telefon — hepsi DAMGASIZ (göç öncesi). Kilit
    /// altında eklenir ki INSERT tetikleyicisi damgalamasın.</summary>
    private void Legacy(string id, string username, string? displayName, string? phone, string? address,
        string? notes = null, string? city = null)
    {
        using var scope = SyncApplyScope.Begin(_db);
        scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt,
                                              Phone, Address, City, Notes)
                        VALUES (@id, 'tiktok', @username, @key, @displayName, 100, 200, @phone, @address, @city, @notes)",
            new { id, username, key = CustomerIdentity.KeyOrNull(username), displayName, phone, address, city, notes });
        scope.Commit();
    }

    /// <summary>Ham varlık denetimi (C8'den sonra <c>GetById</c> yönlendirmeyi izler — U12).</summary>
    private bool Exists(string id) => Count("SELECT COUNT(*) FROM Customer WHERE Id = @id", new { id }) == 1;

    private string? RedirectOf(string fromId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT ToId FROM CustomerRedirect WHERE FromId = @fromId", new { fromId });
    }

    private string IdOf(string username)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string>("SELECT Id FROM Customer WHERE Username = @username", new { username })!;
    }

    private long Seq(string id)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id = @id", new { id });
    }

    private long? Stamp(string id, string column)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id });
    }

    private string? Text(string id, string column)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id });
    }

    private int Count(string sql, object? p = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>(sql, p);
    }

    private void Guarded(string sql, object? p = null)
    {
        using var scope = SyncApplyScope.Begin(_db);
        scope.Execute(sql, p);
        scope.Commit();
    }

    /// <summary>Müşteriye bağlı her tablodan bir satır (etiket, kargo, çekiliş katılımı, ödeme işi).</summary>
    private void SeedRefs(string customerId, string? applyKey = null, string scope = "cumulative")
    {
        using (var c = _db.Open())
        {
            c.Execute("INSERT OR IGNORE INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
            c.Execute(@"INSERT OR IGNORE INTO Giveaway (Id, SessionId, Keyword, DurationSeconds, WinnerCount, RandomSeed, StartedAt)
                        VALUES ('g1', 's1', 'k', 60, 1, 'seed', 1)");
            c.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt)
                        VALUES (@id, 's1', @customerId, 'tiktok', 'u', 'A1', 10, 1)", new { id = NewId(), customerId });
            c.Execute("INSERT INTO Shipment (Id, CustomerId, CreatedAt) VALUES (@id, @customerId, 1)",
                new { id = NewId(), customerId });
            c.Execute(@"INSERT INTO GiveawayParticipant (Id, GiveawayId, CustomerId, Platform, Username, EnteredAt)
                        VALUES (@id, 'g1', @customerId, 'tiktok', @customerId, 1)", new { id = NewId(), customerId });
        }
        Job(customerId, scope, applyKey);
    }

    private string Job(string customerId, string scope, string? applyKey)
    {
        var id = NewId();
        using var c = _db.Open();
        c.Execute(@"INSERT INTO PaymentJob (Id, CustomerId, ScopeKey, ProductTotal, State, CreatedAt, UpdatedAt, ApplyKey)
                    VALUES (@id, @customerId, @scope, '100', @state, 1, 1, @applyKey)",
            new { id, customerId, scope, applyKey, state = applyKey is null ? "created" : "applied" });
        return id;
    }

    /// <summary>PaymentRequestService'in miras devralmasından sonraki hâl
    /// (<see cref="PaymentJobRepository.AdoptLegacyResult"/>): kapalı <c>legacy:K</c> işi ve K'yi
    /// devralmış AÇIK kapsam işi — ikisi de aynı müşteride.</summary>
    private (string Legacy, string Adopted, string Key) AdoptedLegacy(string customerId, string scope = "cumulative")
    {
        var key = NewId();
        var legacy = Job(customerId, $"legacy:{key}", key);
        var adopted = Job(customerId, scope, applyKey: null);
        new PaymentJobRepository(_db).AdoptLegacyResult(adopted, legacy);
        return (legacy, adopted, key);
    }

    /// <summary>035'in harici içerikli FTS indeksi Customer ile tutarlı mı (tutarsızsa fırlatır).</summary>
    private void AssertSearchIndexConsistent()
    {
        using var c = _db.Open();
        var check = () => c.Execute("INSERT INTO CustomerFts(CustomerFts, rank) VALUES('integrity-check', 1)");
        check.Should().NotThrow("arama indeksi Customer ile tutarlı kalmalı");
    }

    private long? FirstFailedAt(string itemId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long?>("SELECT FirstFailedAt FROM CustomerFeedFailure WHERE ItemId = @itemId", new { itemId });
    }

    private int Refs(string customerId) =>
        Count("SELECT COUNT(*) FROM Label WHERE CustomerId = @customerId", new { customerId })
      + Count("SELECT COUNT(*) FROM Shipment WHERE CustomerId = @customerId", new { customerId })
      + Count("SELECT COUNT(*) FROM GiveawayParticipant WHERE CustomerId = @customerId", new { customerId })
      + Count("SELECT COUNT(*) FROM PaymentJob WHERE CustomerId = @customerId", new { customerId });

    private static ServerCustomer Server(string id, string username, CustomerSyncState? fields = null, bool provisional = false)
        => new(id, "tiktok", username, provisional, fields ?? new CustomerSyncState());

    private int GuardRows() => Count("SELECT COUNT(*) FROM SyncApplyGuard");

    // ── gönderim okuması ────────────────────────────────────────────────

    [Fact]
    public void GetForPush_tum_birimleri_ve_damgalari_doner()
    {
        var id = Local("ayse");
        _customers.UpdateNotes(id, "kargo kapıya");

        var row = _sync.GetForPush(0, 10).Single(r => r.Id == id);

        row.Platform.Should().Be("tiktok");
        row.Fields.DisplayName.Should().Be("takma");
        row.Fields.DisplayNameChangedAt.Should().NotBeNull();
        row.Fields.Notes.Should().Be("kargo kapıya");
        row.Fields.NotesChangedAt.Should().NotBeNull();
        row.Fields.FullNameChangedAt.Should().BeNull();
        row.LastSeenAt.Should().Be(200);
        row.SyncSeq.Should().Be(Seq(id));
    }

    // ── sunucudan inen asıl kayıt ───────────────────────────────────────

    [Fact]
    public void Yerelde_olmayan_satir_sunucu_damgalariyla_eklenir_kendisi_damgalamaz()
    {
        var id = NewId();
        var r = _sync.ApplyServerCustomer(Server(id, "mehmet", new CustomerSyncState
        {
            FullName = "Mehmet K", FullNameChangedAt = T1,
            City = "İzmir", AddressChangedAt = T2,
            Notes = "damgasız geçmiş not",
        }), NothingPushed, Now);

        r.Should().Be(FeedApplyResult.Inserted);
        var c = _customers.GetById(id)!;
        c.FullName.Should().Be("Mehmet K");
        c.City.Should().Be("İzmir");
        c.Notes.Should().Be("damgasız geçmiş not");
        Stamp(id, "FullNameChangedAt").Should().Be(T1);
        Stamp(id, "AddressChangedAt").Should().Be(T2);
        Stamp(id, "NotesChangedAt").Should().BeNull("damgasız doldurma damgasız kalır");
        Text(id, "IdentityKey").Should().Be("mehmet");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Sunucudan_eklenen_satir_KVKK_mezar_tasina_takilir()
    {
        _customers.RecordPurge("tiktok", "silinen", purgedAtUnix: 5000);
        var id = NewId();

        _sync.ApplyServerCustomer(Server(id, "SILINEN", new CustomerSyncState { Phone = NewPhone(), PhoneChangedAt = T1 }),
            NothingPushed, Now);

        var c = _customers.GetById(id)!;
        c.Phone.Should().BeNull();
        c.DisplayName.Should().Be("[Silindi]");
    }

    [Fact]
    public void Sunucudaki_takma_ad_FullName_yerelde_gercek_ad_sayilmaz()
    {
        var id = NewId();
        _sync.ApplyServerCustomer(Server(id, "ayse_tt", new CustomerSyncState { FullName = "ayse_tt" }), NothingPushed, Now);
        _customers.GetById(id)!.FullName.Should().BeNull("R3-02 takma ad yedeği gerçek ad değil (kural 4)");
    }

    [Fact]
    public void Damgali_yeni_birim_yazilir_esit_ve_eski_yazilmaz()
    {
        var id = Local("ayse");
        _customers.UpdateNotes(id, "yerel");
        var local = Stamp(id, "NotesChangedAt")!.Value;

        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { Notes = "eski", NotesChangedAt = local - 1 }), Pushed, Now)
            .Should().Be(FeedApplyResult.Unchanged);
        // Eşit damga + aynı değer: kendi yankısı. Eşit damgada FARKLI değer sunucununkini alır
        // (U11, C2 kalite incelemesi) — ayrı test.
        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { Notes = "yerel", NotesChangedAt = local }), Pushed, Now)
            .Should().Be(FeedApplyResult.Unchanged);
        _customers.GetById(id)!.Notes.Should().Be("yerel");

        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { Notes = "yeni", NotesChangedAt = local + 1 }), Pushed, Now)
            .Should().Be(FeedApplyResult.Updated);
        _customers.GetById(id)!.Notes.Should().Be("yeni");
        Stamp(id, "NotesChangedAt").Should().Be(local + 1);
    }

    [Fact]
    public void Esit_damgada_farkli_deger_sunucunun_degeri_yazilir_damga_ve_SyncSeq_degismez()
    {
        // U11: sunucu eşit damgada İLK geleni tutar; istemci sunucudan inen satırı uygularken eşit
        // damgalı farklı değeri alır — bütün bilgisayarlar sunucunun değerine yakınsar.
        var id = Local("ayse");
        _customers.UpdateNotes(id, "yerel");
        var local = Stamp(id, "NotesChangedAt")!.Value;
        var seq = Seq(id);

        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { Notes = "sunucu", NotesChangedAt = local }), Pushed, Now)
            .Should().Be(FeedApplyResult.Updated);

        _customers.GetById(id)!.Notes.Should().Be("sunucu");
        Stamp(id, "NotesChangedAt").Should().Be(local, "damga zaten eşit — yeniden basılmaz");
        Seq(id).Should().Be(seq, "sunucudan inen değer yankı olarak geri gönderilmez (U2)");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Damgasiz_birim_yalniz_damgasiz_bosu_doldurur()
    {
        var id = Local("ayse");
        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { DisplayName = "sunucu takma", City = "İzmir" }), Pushed, Now);

        var c = _customers.GetById(id)!;
        c.DisplayName.Should().Be("takma", "yerel takma ad damgalı (sohbet eklemesi)");
        c.City.Should().Be("İzmir");
        Stamp(id, "AddressChangedAt").Should().BeNull();
    }

    [Fact]
    public void Uygulama_SyncSeqi_ilerletmez_yanki_yok()
    {
        var id = Local("ayse");
        var seq = Seq(id);

        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { Phone = NewPhone(), PhoneChangedAt = T3 }), Pushed, Now)
            .Should().Be(FeedApplyResult.Updated);

        Seq(id).Should().Be(seq);
    }

    [Fact]
    public void Silinmis_yerel_satira_yazilmaz()
    {
        var id = Local("ayse");
        _customers.RecordPurge("tiktok", "ayse", purgedAtUnix: 5000);

        _sync.ApplyServerCustomer(Server(id, "ayse", new CustomerSyncState { FullName = "x", FullNameChangedAt = T3 }), Pushed, Now)
            .Should().Be(FeedApplyResult.Unchanged);

        _customers.GetById(id)!.FullName.Should().BeNull();
    }

    // ── kimlik sahipleri (U4, U5) ───────────────────────────────────────

    [Fact]
    public void Kimlik_sahibi_gonderilmemisse_akis_durur_hicbir_sey_yazilmaz()
    {
        var holder = Local("Ayse");
        var canonical = NewId();

        _sync.ApplyServerCustomer(Server(canonical, "ayse"), NothingPushed, Now)
            .Should().Be(FeedApplyResult.Stalled);

        Exists(holder).Should().BeTrue();
        Exists(canonical).Should().BeFalse();
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Gonderilmis_kimlik_sahibi_asil_kayda_tasinir_beyan_olabilen_damgasiz_birimler_tasinmaz()
    {
        var holder = Local("ayse");                       // birebir aynı kullanıcı adı → tekil indeks
        _customers.UpdateNotes(holder, "kapıya bırak");     // damgalı: taşınır
        _customers.UpdatePhone(holder, NewPhone());
        Guarded("UPDATE Customer SET PhoneChangedAt = NULL, Email = 'ayse@example.test' WHERE Id = @holder", new { holder });
        SeedRefs(holder);
        var canonical = NewId();
        var watermark = Seq(holder);                         // sahibin güncel hâli gönderildi
        var holderLastSeen = _customers.GetById(holder)!.LastSeenAt;   // UpdatePhone ilerletti (N03)

        var r = _sync.ApplyServerCustomer(
            Server(canonical, "ayse", new CustomerSyncState { FullName = "Ayşe Yılmaz", FullNameChangedAt = T1 }),
            pushWatermark: watermark, Now);

        r.Should().Be(FeedApplyResult.Rekeyed);
        Exists(holder).Should().BeFalse();
        RedirectOf(holder).Should().Be(canonical, "uçuştaki eski Id yazımları asıl kayda çözülür (U12)");
        var c2 = _customers.GetById(canonical)!;
        c2.FullName.Should().Be("Ayşe Yılmaz");
        c2.Notes.Should().Be("kapıya bırak");
        c2.Phone.Should().BeNull("beyan olabilen damgasız birim taşınmaz (U3a) — sıradan kopyanınki sunucuda zaten birleşti");
        c2.Email.Should().Be("ayse@example.test", "beyan olamayan damgasız birim doldurma olarak taşınır (U3a)");
        c2.TotalLabelsPrinted.Should().Be(2);
        c2.TotalAmount.Should().Be(100m);
        c2.FirstSeenAt.Should().Be(100);
        c2.LastSeenAt.Should().Be(holderLastSeen, "ilk/son görülme sahiplerden gelir, sunucu satırının eklenme anından değil");
        Refs(canonical).Should().Be(4);
        Refs(holder).Should().Be(0);
        Seq(canonical).Should().BeGreaterThan(watermark, "taşınan birimler asıl kaydın Id'siyle gönderilmeli");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Birden_cok_kimlik_sahibi_tek_islemde_asil_kayda_tasinir()
    {
        // Aynı kişinin harf-farklı iki yerel satırı (biri asıl kaydın adıyla BİREBİR aynı — tekil
        // indeks). İkisinin de "cumulative" ödeme işi var: ikincisi miras kapsamına döner (U8).
        var exact = Local("ayse");
        var variant = Local("AYSE");
        SeedRefs(exact, applyKey: NewId());
        SeedRefs(variant, applyKey: NewId());
        var canonical = NewId();
        var watermark = Math.Max(Seq(exact), Seq(variant));

        _sync.ApplyServerCustomer(Server(canonical, "ayse"), watermark, Now).Should().Be(FeedApplyResult.Rekeyed);

        Exists(exact).Should().BeFalse();
        Exists(variant).Should().BeFalse();
        RedirectOf(exact).Should().Be(canonical);
        RedirectOf(variant).Should().Be(canonical);
        Refs(canonical).Should().Be(8);
        Count("SELECT COUNT(*) FROM PaymentJob WHERE CustomerId = @canonical AND ScopeKey LIKE 'legacy:%'", new { canonical })
            .Should().Be(1);
        var c = _customers.GetById(canonical)!;
        c.TotalLabelsPrinted.Should().Be(4);
        c.TotalAmount.Should().Be(200m);
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Kimlik_sahipleri_devralinmis_miras_isleriyle_de_tasinir()
    {
        // I-1, kimlik sahibi yolu: iki sahibin de devralınmış miras işi var — hangisi ikinci
        // taşınırsa onun kapsam işi çakışır; sınama sahiplerin okunma sırasından bağımsız.
        var exact = Local("ayse");
        var variant = Local("AYSE");
        var first = AdoptedLegacy(exact);
        var second = AdoptedLegacy(variant);
        var canonical = NewId();

        _sync.ApplyServerCustomer(Server(canonical, "ayse"), Pushed, Now).Should().Be(FeedApplyResult.Rekeyed);

        using var c = _db.Open();
        var jobs = c.Query<(string Id, string ScopeKey, long? ClosedAt)>(
            "SELECT Id, ScopeKey, ClosedAt FROM PaymentJob WHERE CustomerId = @canonical", new { canonical }).ToList();
        jobs.Should().HaveCount(4);
        jobs.Should().ContainSingle(j => j.ScopeKey == "cumulative");
        jobs.Where(j => j.ClosedAt is not null).Select(j => j.ScopeKey)
            .Should().BeEquivalentTo(new[] { $"legacy:{first.Key}", $"legacy:{second.Key}" }, "kapalı miras işleri adını korur");
        jobs.Single(j => j.ClosedAt is null && j.ScopeKey != "cumulative").ScopeKey
            .Should().BeOneOf($"legacy:{first.Key}:{first.Adopted}", $"legacy:{second.Key}:{second.Adopted}");
        _sync.CountOpenKeyedLegacyJobs().Should().Be(1);
    }

    [Fact]
    public void Kimlik_sahibi_mezar_tasli_asil_kayda_avatar_tasimaz()
    {
        // M-1 (KVKK). Eski sürümün yazdığı, kimlik anahtarı henüz onarılmamış mezar taşı: NOCASE
        // 'ŞEYMA' ile 'şeyma'yı eşlemez — sahip boşaltılmadı; sunucunun asıl kaydı birebir adla
        // eşleşip boşaltılır. Toplamlar silinmiş kayda avatarı geri getirmemeli.
        using (var c = _db.Open())
            c.Execute("INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt) VALUES ('tiktok', 'ŞEYMA', 5000)");
        var holder = Local("şeyma", avatar: "https://cdn.example.test/a.jpg");
        var canonical = NewId();

        _sync.ApplyServerCustomer(Server(canonical, "ŞEYMA"), Seq(holder), Now).Should().Be(FeedApplyResult.Rekeyed);

        var row = _customers.GetById(canonical)!;
        row.DisplayName.Should().Be("[Silindi]");
        row.AvatarUrl.Should().BeNull("silinmiş kayda kişisel veri geri gelmez");
        row.TotalLabelsPrinted.Should().Be(2, "toplamlar kişisel veri değildir, taşınır");
    }

    [Fact]
    public void Kimlik_sahibi_tasinirken_esit_damgada_sunucunun_degeri_kalir()
    {
        // Yerel kopya→asıl birleştirmesi sunucununkiyle aynı: eşit damga yankıdır (U11 —
        // incomingWinsTie YALNIZ sunucudan inen satırın yerel satıra uygulanmasında). Sahibin
        // gönderiminde sunucu da eşit damgalı notu yazmadı; asıl kaydın değeri her yerde aynı kalır.
        var holder = Local("ayse");
        Guarded("UPDATE Customer SET Notes = 'yerel not', NotesChangedAt = @T1 WHERE Id = @holder", new { T1, holder });
        var canonical = NewId();

        _sync.ApplyServerCustomer(Server(canonical, "ayse", new CustomerSyncState { Notes = "sunucu notu", NotesChangedAt = T1 }),
                pushWatermark: Seq(holder), Now)
            .Should().Be(FeedApplyResult.Rekeyed);

        _customers.GetById(canonical)!.Notes.Should().Be("sunucu notu");
        Stamp(canonical, "NotesChangedAt").Should().Be(T1);
    }

    // ── geçici satır ve miras satırı (kural 7, U9) ──────────────────────

    [Fact]
    public void Gecici_kayit_yerelde_hic_acilmaz_kimlik_sahibi_olsa_da_olmasa_da()
    {
        var holder = Local("ayse");
        var squatter = NewId();
        _sync.ApplyServerCustomer(Server(squatter, "AYSE", new CustomerSyncState { Phone = NewPhone() }, provisional: true), Pushed, Now)
            .Should().Be(FeedApplyResult.SkippedProvisional);
        Exists(squatter).Should().BeFalse();
        Exists(holder).Should().BeTrue("gerçek müşteri sahiplenenin kaydına ASLA taşınmaz (kural 7)");

        var lone = NewId();
        _sync.ApplyServerCustomer(Server(lone, "baskasi", new CustomerSyncState { FullName = "Beyan Ad" }, provisional: true), Pushed, Now)
            .Should().Be(FeedApplyResult.SkippedProvisional,
                "yerelde açılsaydı sohbet onu benimser, etiketler geçici Id'ye yazılırdı");
        Exists(lone).Should().BeFalse();
        Count("SELECT COUNT(*) FROM Customer WHERE Username = 'baskasi'").Should().Be(0,
            "yerel BINARY tekil indeks sohbetin aynı adla kendi satırını açmasını da engellerdi");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Miras_satiri_yeni_yerel_Idye_donusur_beyana_esit_damgasiz_birimler_duser_farklilar_kalir()
    {
        var provisional = NewId();
        var claimPhone = NewPhone();
        // Eski ingest beyanı indirdi; yayıncı göç öncesi ili ekleyip not yazmış (damgasız).
        Legacy(provisional, "ayse", displayName: "Ayşe Y", phone: claimPhone, address: "Atatürk Cd. 1",
            notes: "kapıda", city: "İzmir");
        SeedRefs(provisional);
        var claims = new CustomerSyncState { FullName = "Ayşe Y", Phone = claimPhone, Address = "Atatürk Cd. 1" };
        var maxSeqBefore = Count("SELECT MAX(SyncSeq) FROM Customer");

        _sync.ApplyServerCustomer(Server(provisional, "ayse", claims, provisional: true), Pushed, Now)
            .Should().Be(FeedApplyResult.Converted);

        Exists(provisional).Should().BeFalse("geçici Id yerelde yaşamaz — sonraki gönderim bilgisayarın kendi Id'sini götürür");
        var converted = IdOf("ayse");
        converted.Should().NotBe(provisional);
        RedirectOf(provisional).Should().Be(converted);
        var c = _customers.GetById(converted)!;
        c.DisplayName.Should().BeNull("beyan adı — eski ingest DisplayName'e yazmıştı");
        c.Phone.Should().BeNull("beyan telefonu: sahiplenenin bağlantısı kendi telefonuna karşı kanıtlı kalmasın");
        c.Address.Should().Be("Atatürk Cd. 1", "blok yayıncının eliyle değişmiş (il eklendi) — bütün kalır");
        c.City.Should().Be("İzmir");
        c.Notes.Should().Be("kapıda");
        Refs(converted).Should().Be(4);
        Seq(converted).Should().BeGreaterThan(maxSeqBefore, "dönüştürülen satır gönderime girer — sunucu devralsın (S8)");
        Stamp(converted, "AddressChangedAt").Should().BeNull("dönüştürme düzenleme değildir");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void Silinmis_gecici_kaydin_miras_satiri_beyan_bilinmeden_donusur_mezar_tasi_yazilmaz()
    {
        var provisional = NewId();
        Legacy(provisional, "ayse", displayName: "Ayşe Y", phone: NewPhone(), address: "Adres", notes: "kapıda");
        var real = Local("Ayse_Gercek");

        _sync.ApplyProvisional(provisional, claims: null, Now).Should().Be(FeedApplyResult.Converted);

        var c = _customers.GetById(IdOf("ayse"))!;
        c.DisplayName.Should().BeNull();
        c.Phone.Should().BeNull();
        c.Address.Should().BeNull("beyan sunucuda boşaltıldı, bilinmez → beyan olabilen damgasız birimlerin hepsi düşer");
        c.Notes.Should().Be("kapıda", "beyan olamaz");
        Count("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(0, "geçici satırın silinmesi kimliğe yayılmaz");
        Count("SELECT COUNT(*) FROM Customer WHERE PurgedAt IS NOT NULL").Should().Be(0);
        Exists(real).Should().BeTrue();
    }

    [Fact]
    public void Yerelde_satiri_olmayan_gecici_silme_hicbir_sey_yazmaz()
    {
        _sync.ApplyProvisional(NewId(), claims: null, Now).Should().Be(FeedApplyResult.SkippedProvisional);
        Count("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(0);
        GuardRows().Should().Be(0);
    }

    // ── yönlendirmeler (push yanıtı ve akış kopya satırı) ───────────────

    [Fact]
    public void Akis_yonlendirmesi_gonderilmemis_kopyayi_erteler()
    {
        var copy = Local("ayse");
        var canonical = Local("Ayse");

        _sync.ApplyFeedRedirect(copy, canonical, NothingPushed, Now).Should().Be(FeedApplyResult.Deferred);

        Exists(copy).Should().BeTrue("gönderimi yönlendirmeyi zaten döndürecek (S7)");
    }

    [Fact]
    public void Akis_yonlendirmesi_hedef_yereldeyse_tasir_degilse_bekler()
    {
        var copy = Local("ayse");
        var missing = NewId();
        _sync.ApplyFeedRedirect(copy, missing, Pushed, Now).Should().Be(FeedApplyResult.Ignored);
        Exists(copy).Should().BeTrue("asıl kayıt akışta gelince kimlik sahibi olarak taşınır (U4)");

        var canonical = Local("Ayse");
        _sync.ApplyFeedRedirect(copy, canonical, Pushed, Now).Should().Be(FeedApplyResult.Rekeyed);
        Exists(copy).Should().BeFalse();
    }

    [Fact]
    public void RekeyToLocal_referanslari_ve_toplamlari_tasir_kopyayi_siler_asil_kaydi_gonderime_koyar()
    {
        var copy = Local("ayse");
        var canonical = Local("Ayse");
        SeedRefs(copy);
        var before = Seq(canonical);

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        Exists(copy).Should().BeFalse();
        RedirectOf(copy).Should().Be(canonical);
        Refs(canonical).Should().Be(4);
        var c = _customers.GetById(canonical)!;
        c.TotalLabelsPrinted.Should().Be(4);
        c.TotalAmount.Should().Be(200m);
        Seq(canonical).Should().BeGreaterThan(before, "taşınan birimler gönderilsin (U2)");
        GuardRows().Should().Be(0);
    }

    [Fact]
    public void RekeyToLocal_esit_damgada_asil_kaydin_degeri_kalir()
    {
        // Kopya→asıl birleştirmesinde eşit damga yankıdır (sunucuyla aynı, U11): kopyanın
        // gönderiminde sunucu da asıl kaydın eşit damgalı değerini tuttu.
        var canonical = Local("Ayse");
        var copy = Local("ayse");
        Guarded("UPDATE Customer SET Notes = 'asıl not', NotesChangedAt = @T1 WHERE Id = @canonical", new { T1, canonical });
        Guarded("UPDATE Customer SET Notes = 'kopya not', NotesChangedAt = @T1 WHERE Id = @copy", new { T1, copy });

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        _customers.GetById(canonical)!.Notes.Should().Be("asıl not");
        Stamp(canonical, "NotesChangedAt").Should().Be(T1);
    }

    [Fact]
    public void RekeyToLocal_hedef_ya_da_kaynak_yoksa_hicbir_sey_yapmaz()
    {
        var copy = Local("ayse");
        _sync.RekeyToLocal(copy, NewId(), Pushed, Now).Should().Be(RekeyResult.TargetMissing);
        _sync.RekeyToLocal(NewId(), copy, Pushed, Now).Should().Be(RekeyResult.SourceMissing);
        Exists(copy).Should().BeTrue();
    }

    [Fact]
    public void RekeyToLocal_gonderilen_partiden_sonra_degisen_kaynagi_tasimaz()
    {
        // M-7: yönlendirme, partinin okunduğu andaki satır için geldi. Kaynak o andan sonra
        // değiştiyse (ör. taze bilgisayarın form oynatmasının damgasız doldurması — C10) yeni hâli
        // sunucuya hiç gitmedi: şimdi taşınsaydı beyan olabilen damgasız birim düşer, veri kaybolurdu.
        // Satır zaten imlecin üstünde: sonraki gönderim onu götürür, sunucu yönlendirmeyi yeniden söyler.
        var copy = Local("ayse");
        var canonical = Local("Ayse");
        var pushedThrough = Seq(copy);                         // gönderilen partinin en büyük SyncSeq'i
        _customers.UpdateNotes(copy, "parti okunduktan sonra");
        var seq = Seq(copy);

        _sync.RekeyToLocal(copy, canonical, pushedThrough, Now).Should().Be(RekeyResult.Deferred);

        Exists(copy).Should().BeTrue();
        RedirectOf(copy).Should().BeNull();
        Seq(copy).Should().Be(seq, "zaten gönderim bekliyor — ilerletmek gerekmez");
        GuardRows().Should().Be(0);

        _sync.RekeyToLocal(copy, canonical, pushedThroughSeq: seq, Now).Should().Be(RekeyResult.Rekeyed,
            "yeni hâli de gönderildikten sonra taşınır");
        _customers.GetById(canonical)!.Notes.Should().Be("parti okunduktan sonra");
    }

    [Fact]
    public void Customera_baslanan_her_tablo_tasimada_ele_alinir()
    {
        // Şema koruması: Customer'a FK'sı olan ya da CustomerId kolonu taşıyan her tablo taşımanın
        // listesinde olmalı. Yeni bir tablo eklenip unutulursa satırları silinen Id'de öksüz kalır
        // (FK'lıysa commit düşer ve taşıma her turda başarısız olur).
        using var c = _db.Open();
        var referencing = c.Query<string>(@"
            SELECT m.name FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f
             WHERE m.type = 'table' AND f.""table"" = 'Customer' COLLATE NOCASE
            UNION
            SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) p
             WHERE m.type = 'table' AND p.name = 'CustomerId' COLLATE NOCASE").ToList();

        referencing.Should().BeEquivalentTo(CustomerSyncRepository.CustomerReferenceTables);
    }

    [Fact]
    public void Yonlendirme_daha_once_tasinmis_ara_Idye_gelirse_guncel_Idye_tasir()
    {
        // Önce A → K taşındı; sonra push yanıtı B → A diyor (A artık yerelde yok). Çözülmeseydi
        // B ortada kalırdı (U12).
        var a = Local("ayse");
        var b = Local("AYSE");
        var k = Local("Ayse");
        _sync.RekeyToLocal(a, k, Pushed, Now).Should().Be(RekeyResult.Rekeyed);
        SeedRefs(b);

        _sync.RekeyToLocal(b, a, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        Exists(b).Should().BeFalse();
        Refs(k).Should().Be(4);
        RedirectOf(b).Should().Be(k);
    }

    [Fact]
    public void Yonlendirme_zinciri_kisaltilir_canli_Id_yonlendirilmez()
    {
        var a = Local("ayse");
        var k = Local("Ayse");
        _sync.RekeyToLocal(a, k, Pushed, Now).Should().Be(RekeyResult.Rekeyed);
        var z = Local("AYSE");

        _sync.RekeyToLocal(k, z, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        RedirectOf(a).Should().Be(z, "a → k → z zinciri tek adıma iner");
        RedirectOf(k).Should().Be(z);
        RedirectOf(z).Should().BeNull();

        // Sunucu daha önce taşınmış bir Id'yi asıl kayıt olarak indirirse o Id yeniden canlıdır.
        _sync.ApplyServerCustomer(Server(a, "baska_kimlik"), Pushed, Now).Should().Be(FeedApplyResult.Inserted);
        RedirectOf(a).Should().BeNull();
    }

    [Fact]
    public void RekeyToLocal_odeme_isi_kapsam_cakismasi_mirasa_doner()
    {
        var copy = Local("ayse");
        var canonical = Local("Ayse");
        var copyKey = NewId();
        Job(canonical, "cumulative", NewId());
        var applied = Job(copy, "cumulative", copyKey);            // para hareketi var
        Job(canonical, "session:s1", null);
        var fresh = Job(copy, "session:s1", null);                  // hiç hareket yok

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        using var c = _db.Open();
        c.QuerySingle<(string ScopeKey, long? ClosedAt)>("SELECT ScopeKey, ClosedAt FROM PaymentJob WHERE Id = @applied", new { applied })
            .Should().Be(($"legacy:{copyKey}:{applied}", (long?)null), "açık miras iş: PaymentRequestService uzlaştırır (U8)");
        var closed = c.QuerySingle<(string ScopeKey, long? ClosedAt)>("SELECT ScopeKey, ClosedAt FROM PaymentJob WHERE Id = @fresh", new { fresh });
        closed.ScopeKey.Should().Be($"legacy:{fresh}:{fresh}");
        closed.ClosedAt.Should().Be(Now);
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM PaymentJob WHERE CustomerId = @canonical", new { canonical }).Should().Be(4);
    }

    [Fact]
    public void RekeyToLocal_devralinmis_miras_isi_olan_kopya_da_tasinir()
    {
        // I-1: miras devralması (AdoptLegacyResult) kapalı 'legacy:K' işini bırakır, K'yi kapsam
        // işine taşır. Kapsam çakışmasında o iş yeniden 'legacy:K' adını alsaydı kopyanın kendi kapalı
        // işine çarpar (UX_PaymentJob_Scope) ve taşıma her turda düşerdi — kimlik kalıcı bölünürdü.
        var copy = Local("ayse");
        var canonical = Local("Ayse");
        var (legacy, adopted, key) = AdoptedLegacy(copy);
        Job(canonical, "cumulative", NewId());

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        using var c = _db.Open();
        c.QuerySingle<(string ScopeKey, long? ClosedAt)>("SELECT ScopeKey, ClosedAt FROM PaymentJob WHERE Id = @adopted", new { adopted })
            .Should().Be(($"legacy:{key}:{adopted}", (long?)null), "anahtarlı açık miras iş: uzlaştırılır (U8)");
        c.ExecuteScalar<string>("SELECT ScopeKey FROM PaymentJob WHERE Id = @legacy", new { legacy })
            .Should().Be($"legacy:{key}", "kapalı miras işi adını korur");
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM PaymentJob WHERE CustomerId = @canonical", new { canonical }).Should().Be(3);
        _sync.CountOpenKeyedLegacyJobs().Should().Be(1);
    }

    [Fact]
    public void RekeyToLocal_silinmis_kopyanin_damgali_bos_kisisel_birimi_asil_kaydi_silmez()
    {
        var canonical = Local("Ayse");
        var phone = NewPhone();
        _customers.UpdatePhone(canonical, phone);
        Guarded("UPDATE Customer SET PhoneChangedAt = @T1 WHERE Id = @canonical", new { T1, canonical });
        var copy = Local("ayse");
        _customers.UpdatePhone(copy, NewPhone());                  // damga: şimdi (> T1)
        _customers.UpdateNotes(copy, "not kalır");
        // Kopya silinmiş: telefon boşaldı, damgası kaldı (akıştan inen silme kilit altında yazar).
        Guarded("UPDATE Customer SET Phone = NULL, DisplayName = '[Silindi]', PurgedAt = 5000 WHERE Id = @copy", new { copy });

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        var r = _customers.GetById(canonical)!;
        r.Phone.Should().Be(phone, "silinmiş kaynaktaki damgalı boş bilinçli silme değildir (S15)");
        r.Notes.Should().Be("not kalır");
    }

    [Fact]
    public void RekeyToLocal_silinmis_asil_kayda_avatar_tasimaz()
    {
        // M-1 (KVKK): taşımanın toplamları silinmiş asıl kayda kişisel veri (avatar) getirmez.
        var canonical = Local("Ayse");
        _customers.ScrubPersonalData(canonical);
        var copy = Local("ayse", avatar: "https://cdn.example.test/b.jpg");

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        var row = _customers.GetById(canonical)!;
        row.AvatarUrl.Should().BeNull("silinmiş kayda kişisel veri geri gelmez");
        row.TotalLabelsPrinted.Should().Be(4, "toplamlar kişisel veri değildir, taşınır");
    }

    [Fact]
    public void Tasima_ve_donusturme_arama_indeksini_tutarli_birakir()
    {
        // 035'in FTS indeksi rowid'e bağlı: sahip silme + asıl kayıt ekleme, kopya silme ve
        // Id yeniden yazımı (dönüştürme) indeksi Customer ile tutarlı bırakmalı.
        var holder = Local("ayse", displayName: "Ayşe takma");
        _customers.UpdatePhone(holder, NewPhone());
        _sync.ApplyServerCustomer(Server(NewId(), "ayse", new CustomerSyncState { FullName = "Ayşe Yılmaz", FullNameChangedAt = T1 }),
            Seq(holder), Now).Should().Be(FeedApplyResult.Rekeyed);
        AssertSearchIndexConsistent();

        var copy = Local("mehmet");
        var target = Local("Mehmet");
        _sync.RekeyToLocal(copy, target, Pushed, Now).Should().Be(RekeyResult.Rekeyed);
        AssertSearchIndexConsistent();

        var provisional = NewId();
        Legacy(provisional, "zeynep", displayName: "Zeynep Y", phone: NewPhone(), address: "Adres");
        _sync.ApplyProvisional(provisional, claims: null, Now).Should().Be(FeedApplyResult.Converted);
        AssertSearchIndexConsistent();

        _customers.Search("ayse").Should().ContainSingle();
        _customers.Search("mehm").Should().ContainSingle();
        _customers.Search("zeyn").Select(c => c.Username).Should().Equal("zeynep");
    }

    [Fact]
    public async Task Dosya_veritabaninda_tasima_yarisan_yaziciyi_bekler_yazdigini_kaybetmez()
    {
        // Üretim kurgusu (dosya, WAL, havuz): başka bir bağlantı yazma kilidini tutarken taşıma
        // OKUMADAN önce kilidi bekler (BEGIN IMMEDIATE) — okuma-birleştirme-yazma yarışan yazımın
        // ARDINA düşer, operatörün o an kaydettiği not kaybolmaz.
        var path = Path.Combine(Path.GetTempPath(), $"od-rekey-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            var customers = new CustomerRepository(factory);
            var sync = new CustomerSyncRepository(factory, new CustomerBusySet());
            string Insert(string username)
            {
                var id = NewId();
                customers.Insert(new Customer(id, "tiktok", username, "takma", AvatarUrl: null,
                    FirstSeenAt: 100, LastSeenAt: 200, IsBlacklisted: false, BlacklistReason: null, Notes: null,
                    TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null));
                return id;
            }
            var copy = Insert("ayse");
            var canonical = Insert("Ayse");

            using (var writer = new SqliteConnection($"Data Source={path};Foreign Keys=true;Pooling=false"))
            {
                writer.Open();
                SqliteSearchFunctions.Register(writer);
                using var tx = writer.BeginTransaction();                 // BEGIN IMMEDIATE: kilit onda
                writer.Execute("UPDATE Customer SET Notes = 'yarışan not' WHERE Id = @copy", new { copy }, tx);

                var rekey = Task.Run(() => sync.RekeyToLocal(copy, canonical, Pushed, Now));
                await Task.Delay(300);
                rekey.IsCompleted.Should().BeFalse("taşıma yazma kilidini bekler");
                tx.Commit();

                (await rekey.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(RekeyResult.Rekeyed);
            }

            customers.GetById(canonical)!.Notes.Should().Be("yarışan not", "kopyanın taşımadan hemen önceki düzenlemesi taşındı");
            using var conn = factory.Open();
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE Id = @copy", new { copy }).Should().Be(0);
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void GUID_olmayan_Idli_kopyanin_damgasiz_verisi_de_tasinir()
    {
        // U3b: gönderim GUID olmayan Id'yi hiç göndermedi — verisi sunucuda yok; geçici kökenli olamaz.
        var phone = NewPhone();
        Guarded(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt, Phone)
                  VALUES ('eski1', 'tiktok', 'ayse', 'ayse', 'Ayşe', 1, 1, @phone)", new { phone });
        var canonical = Local("Ayse", displayName: null);

        _sync.RekeyToLocal("eski1", canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);

        var c = _customers.GetById(canonical)!;
        c.Phone.Should().Be(phone);
        c.DisplayName.Should().Be("Ayşe");
    }

    // ── meşgul müşteri (U13) ────────────────────────────────────────────

    [Fact]
    public async Task Odeme_akisi_suren_musteri_tasinmaz_donusturulmez_kira_bitince_tasinir()
    {
        var copy = Local("ayse");
        var canonical = Local("Ayse");
        var provisional = NewId();
        Legacy(provisional, "mehmet", displayName: "Mehmet", phone: null, address: null);
        var seqBefore = Seq(copy);

        var lease = await _busy.EnterAsync(copy);
        var provisionalLease = await _busy.EnterAsync(provisional);

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Busy);
        Exists(copy).Should().BeTrue();
        Seq(copy).Should().BeGreaterThan(seqBefore, "yeniden gönderilsin: sunucu yönlendirmeyi yeniden söyler (S7)");
        var seqAfterPushRedirect = Seq(copy);
        _sync.ApplyFeedRedirect(copy, canonical, Pushed, Now).Should().Be(FeedApplyResult.Deferred);
        Seq(copy).Should().BeGreaterThan(seqAfterPushRedirect, "akış yönlendirmesi de yeniden gönderime koyar (U13)");
        GuardRows().Should().Be(0);
        _sync.ApplyServerCustomer(Server(NewId(), "AYSE"), Pushed, Now).Should().Be(FeedApplyResult.Busy,
            "kimlik sahibi ödeme akışında: akış bu öğede kalır, sonraki tur");
        _sync.ApplyProvisional(provisional, claims: null, Now).Should().Be(FeedApplyResult.Busy);
        Exists(provisional).Should().BeTrue();

        lease.Dispose();
        provisionalLease.Dispose();

        _sync.RekeyToLocal(copy, canonical, Pushed, Now).Should().Be(RekeyResult.Rekeyed);
        _sync.ApplyProvisional(provisional, claims: null, Now).Should().Be(FeedApplyResult.Converted);
        GuardRows().Should().Be(0);
    }

    // ── bakım: kimlik anahtarı, zehirli akış öğesi, miras ödeme işi ────

    [Fact]
    public void HealIdentityKeys_eksik_anahtari_onarir_mezar_tasi_dahil()
    {
        using (var c = _db.Open())
        {
            // Eski sürümün açtığı satır ve yazdığı mezar taşı: IdentityKey yok.
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, FirstSeenAt, LastSeenAt)
                        VALUES ('eski', 'tiktok', ' Eski.SURUM ', 1, 1)");
            c.Execute("INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt) VALUES ('tiktok', 'ŞEYMA', 1)");
            // Boş kullanıcı adı kimlik değildir: anahtarı NULL kalır, sayılmaz.
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, FirstSeenAt, LastSeenAt)
                        VALUES ('bos', 'tiktok', '   ', 1, 1)");
        }

        _sync.HealIdentityKeys().Should().Be(2);
        Text("bos", "IdentityKey").Should().BeNull();

        Text("eski", "IdentityKey").Should().Be("eski.surum");
        using var conn = _db.Open();
        conn.ExecuteScalar<string>("SELECT IdentityKey FROM CustomerPurgeTombstone WHERE Username = 'ŞEYMA'").Should().Be("şeyma");
    }

    [Fact]
    public void Akis_hatasi_ayni_degisiklikte_birikir_yeni_degisiklikte_bastan_sayilir()
    {
        // U10: deneme sayısı kalıcı; aynı Id'nin DAHA YENİ değişikliği başarısız olursa içerik
        // değişmiştir — sayaç baştan, atlama işareti kalkar.
        var item = NewId();
        _sync.RecordFeedFailure(item, changeSeq: 7, "ilk hata", Now).Should().Be(1);
        _sync.RecordFeedFailure(item, changeSeq: 7, "yine", Now + 60).Should().Be(2);
        FirstFailedAt(item).Should().Be(Now, "aynı değişiklik: ilk başarısızlık anı korunur");
        _sync.MarkFeedItemSkipped(item, Now + 60);
        _sync.GetFeedFailureIds().Should().BeEquivalentTo(new[] { item });
        Count("SELECT COUNT(*) FROM CustomerFeedFailure WHERE SkippedAt IS NOT NULL").Should().Be(1);

        _sync.RecordFeedFailure(item, changeSeq: 9, new string('x', 600), Now + 120).Should().Be(1);
        FirstFailedAt(item).Should().Be(Now + 120, "sayaç baştan: yeni değişikliğin ilk başarısızlık anı (M-9)");
        Count("SELECT COUNT(*) FROM CustomerFeedFailure WHERE SkippedAt IS NOT NULL").Should().Be(0);
        Count("SELECT LENGTH(LastError) FROM CustomerFeedFailure").Should().Be(500, "hata metni sınırlı tutulur");

        _sync.ClearFeedFailure(item);
        _sync.GetFeedFailureIds().Should().BeEmpty();
    }

    [Fact]
    public void CountOpenKeyedLegacyJobs_yalniz_acik_anahtarli_miras_isleri_sayar()
    {
        var customer = Local("ayse");
        Job(customer, "legacy", NewId());                     // anahtar başına benzersizleştirmeden önceki biçim
        Job(customer, $"legacy:{NewId()}", NewId());          // taşımanın (U8) / 034'ün bıraktığı
        var closed = Job(customer, $"legacy:{NewId()}", NewId());
        Job(customer, $"legacy:{NewId()}", applyKey: null);    // para hareketi yok
        Job(customer, "cumulative", NewId());                 // miras değil
        using (var c = _db.Open())
            c.Execute("UPDATE PaymentJob SET ClosedAt = 1 WHERE Id = @closed", new { closed });

        _sync.CountOpenKeyedLegacyJobs().Should().Be(2);
    }
}
