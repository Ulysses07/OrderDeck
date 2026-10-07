using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// İki bilgisayar, tek (sahte) sunucu: Bölüm C protokolünün uçtan uca yakınsaması (C11, inceleme M2).
/// Her bilgisayar gerçek yığınını koşturur — kendi SQLite'ı, gerçek
/// <see cref="WpfCustomerProjectionSyncService"/> + <see cref="CustomerChangesPullService"/> +
/// <see cref="CustomerSyncRepository"/>; sunucu <see cref="FakeCustomerServer"/>. Bir tur = akış servisinin
/// bir turu (bakım → gönderim → akış → gerekirse yankı gönderimi). Form yolu
/// <see cref="CustomerRepository.UpsertPersonFromIntake"/>'i doğrudan damgalı kipte çağırır
/// (<c>IntakeFormSyncService</c>'in oynatma işareti/akış beklemesi burada sınanmaz — C10).
/// </summary>
public sealed class TwoPcConvergenceTests
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => key;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Bir bilgisayarın gerçek yığını: kendi SQLite'ı, gerçek gönderim ve akış servisleri.</summary>
    private sealed class Pc : IDisposable
    {
        private readonly string _lisans;

        public InMemorySqlite Db { get; } = new();
        public CustomerRepository Customers { get; }
        public CustomerService CustomerService { get; }
        public LabelService Labels { get; }
        public CustomerChangesPullService Pull { get; }
        public SyncStatusTracker Tracker { get; } = new();

        public Pc(FakeCustomerServer server)
        {
            _lisans = server.Lisans;
            new MigrationRunner(Db).Run();
            var clock = new Clock();
            new SessionRepository(Db).Insert(new StreamSession("s1", null, 1, null, new[] { "tiktok" }, null));
            Customers = new CustomerRepository(Db);
            var labelRepo = new LabelRepository(Db);
            CustomerService = new CustomerService(Customers, new SessionRepository(Db), labelRepo, clock);
            Labels = new LabelService(labelRepo, CustomerService, Db, clock);
            var sync = new CustomerSyncRepository(Db, new CustomerBusySet());
            var cursors = new SyncCursorRepository(Db);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var license = new License(server.Lisans);
            var push = new WpfCustomerProjectionSyncService(api, sync, cursors, license, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            Pull = new CustomerChangesPullService(api, Customers, sync, cursors, push, license, clock,
                Tracker, NullLogger<CustomerChangesPullService>.Instance);
        }

        /// <summary>Akış servisinin bir turu; küçük veride her tur boş sayfaya kadar yetişir.</summary>
        public async Task TourAsync()
        {
            (await Pull.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
            Tracker.IsInitialCatchUpDoneFor(_lisans).Should().BeTrue();
        }

        public string Chat(string username) => CustomerService.GetOrCreate("tiktok", username, username, null).Id;

        public Label Sell(string username) => Labels.Add("s1",
            new ChatMessage(Guid.NewGuid().ToString("N"), "tiktok", null, username, username, null, "A1", 1, Array.Empty<string>()),
            10m, null);

        /// <summary>Kimlik anahtarı aynı olan bütün yerel satırlar (ham — GetById yönlendirmeyi izler).</summary>
        public List<(string Id, string? Notes, string? Phone, string? DisplayName)> Identity(string username)
        {
            using var c = Db.Open();
            return c.Query<(string, string?, string?, string?)>(
                "SELECT Id, Notes, Phone, DisplayName FROM Customer WHERE IdentityKey = @key",
                new { key = CustomerIdentity.KeyOrNull(username) }).ToList();
        }

        /// <summary>Eski <c>since</c> ingest'inin geçici Id'yle açtığı satır (göç öncesi, damgasız):
        /// beyan adı takma ada, telefon ve adres düz yazılmıştı.</summary>
        public void LegacyRow(Guid id, string username, string displayName, string phone, string address, string notes)
        {
            using var scope = SyncApplyScope.Begin(Db);
            scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt,
                                                  Phone, Address, Notes)
                            VALUES (@id, 'tiktok', @username, @key, @displayName, 1, 1, @phone, @address, @notes)",
                new { id = id.ToString("N"), username, key = CustomerIdentity.KeyOrNull(username), displayName, phone, address, notes });
            scope.Commit();
        }

        /// <summary>Eski sürümden kalma, damgasız bir gruba bağlı satır (C2 kalite incelemesi senaryosu).</summary>
        public void LegacyGroupedRow(string platform, string username, string groupId)
        {
            using var scope = SyncApplyScope.Begin(Db);
            scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt, GroupId)
                            VALUES (@id, @platform, @username, @key, @username, 1, 1, @groupId)",
                new { id = Guid.NewGuid().ToString("N"), platform, username, key = CustomerIdentity.KeyOrNull(username), groupId });
            scope.Commit();
        }

        /// <summary>Aynı formu (aynı form Id'si ve SubmittedAt) bu bilgisayarda damgalı kipte uygular —
        /// form senkronunun çağırdığı gerçek imza (form Id'si ve gönderim anı zorunlu).</summary>
        public void Form(Guid formId, long submittedAtMs, params (string Platform, string Username)[] identities)
            => Customers.UpsertPersonFromIntake(
                identities.Select(i => (i.Platform, i.Username, (string?)null)).ToArray(),
                "Örnek Müşteri", "Örnek Sk. No 1", phone: null, email: null, tckn: null,
                whatsAppConsent: false, smsConsent: false, nowUnix: submittedAtMs / 1000,
                formId: formId, submittedAtMs: submittedAtMs);

        public string? GroupOf(string platform, string username)
        {
            using var c = Db.Open();
            return c.ExecuteScalar<string?>(
                "SELECT GroupId FROM Customer WHERE Platform = @platform AND IdentityKey = @key",
                new { platform, key = CustomerIdentity.KeyOrNull(username) });
        }

        public string? LabelOwner(string labelId)
        {
            using var c = Db.Open();
            return c.ExecuteScalar<string?>("SELECT CustomerId FROM Label WHERE Id = @labelId", new { labelId });
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Iki_bilgisayar_ayni_kisiyi_ayri_acar_tek_asil_kayitta_bulusur_birimler_bagimsiz()
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        using var b = new Pc(server);
        var idA = a.Chat("ornek_kisi");
        var idB = b.Chat("Ornek_Kisi");
        var labelB = b.Sell("Ornek_Kisi");

        await a.TourAsync();
        await b.TourAsync();             // B'nin satırı kopya olur; asıl kayıt akışla iner, sahip taşınır
        await a.TourAsync();

        a.Identity("ornek_kisi").Should().ContainSingle().Which.Id.Should().Be(idA);
        b.Identity("ornek_kisi").Should().ContainSingle().Which.Id.Should().Be(idA, "B yerelde asıl kaydın Id'sine geçti");
        b.LabelOwner(labelB.Id).Should().Be(idA);
        server.Get(Guid.Parse(idB)).MergedIntoId.Should().Be(Guid.Parse(idA), "sunucuda B'nin Id'si kopya (S6)");
        b.Customers.ResolveId(idB).Should().Be(idA, "uçuştaki eski Id yazımları asıl kayda iner (U12)");

        var phone = TestPhone.NewE164();
        a.Customers.UpdateNotes(idA, "A notu");
        b.Customers.UpdatePhone(idA, phone);
        await a.TourAsync();
        await b.TourAsync();
        await a.TourAsync();

        foreach (var pc in new[] { a, b })
        {
            var row = pc.Identity("ornek_kisi").Single();
            row.Notes.Should().Be("A notu");
            row.Phone.Should().Be(phone, "farklı birimler iki bilgisayarda da kalır");
        }
        var canonical = server.CanonicalOf("tiktok", "ornek_kisi")!;
        canonical.Fields.Notes.Should().Be("A notu");
        canonical.Fields.Phone.Should().Be(phone);
    }

    [Fact]
    public async Task Gonderim_ile_akis_arasinda_acilan_kopya_akisi_durdurur_ayni_turda_yakinsar()
    {
        // U5: asıl kayıt indiğinde yerelde aynı kimliğin HENÜZ gönderilmemiş satırı var (turun gönderimi
        // ile akışı arasında okunan yorum) → akış durur, gönderim kopyayı sunucuya bağlar (siparişleri
        // asıl kayda çözülür), akış aynı turda sürer ve satırı taşır.
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        using var b = new Pc(server);
        var idA = a.Chat("ornek_kisi");
        await a.TourAsync();

        Label? sold = null;
        server.BeforeNextChanges = () => sold = b.Sell("Ornek_Kisi");
        await b.TourAsync();

        sold.Should().NotBeNull();
        sold!.CustomerId.Should().NotBe(idA, "ön koşul: yorum B'de yeni bir yerel satır açtı");
        server.Get(Guid.Parse(sold.CustomerId)).MergedIntoId.Should().Be(Guid.Parse(idA),
            "durma, satırı taşımadan önce sunucuya götürdü (S6) — o Id'yle giden siparişler sahipsiz kalmaz");
        b.Identity("ornek_kisi").Should().ContainSingle().Which.Id.Should().Be(idA);
        b.LabelOwner(sold.Id).Should().Be(idA);
    }

    /// <summary>
    /// C2 kalite incelemesi: aynı formu iki bilgisayar birbirinin gönderimini görmeden işler; yerel
    /// durumları farklı olduğundan (B'de formun ikinci kimliği eski bir grupta) türettikleri grup FARKLI,
    /// damga AYNI (SubmittedAt). Sunucu eşit damgada ilk geleni tutar; istemci akışta eşit damgalı
    /// farklı değeri alır (C3/C4 incomingWinsTie) → her satırın grubu üç yerde aynı değere yakınsar —
    /// hangi bilgisayar önce gönderirse göndersin. Kalan bilinçli sınır: iki kimlik farklı gruplarda
    /// kalabilir — ama her bilgisayarda AYNI biçimde.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ayni_formu_iki_bilgisayar_haberlesmeden_isler_grup_her_yerde_tek_degere_yakinsar(bool aFirst)
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        using var b = new Pc(server);
        a.Chat("ornek_kisi");
        await a.TourAsync();
        await b.TourAsync();                                   // iki bilgisayarda aynı sohbet satırı (aynı Id)
        b.LegacyGroupedRow("instagram", "ornek.kisi", groupId: "g-eski");

        var form = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        a.Form(form, at, ("tiktok", "ornek_kisi"), ("instagram", "ornek.kisi"));
        b.Form(form, at, ("tiktok", "ornek_kisi"), ("instagram", "ornek.kisi"));   // A'nın gönderimini görmeden
        a.GroupOf("tiktok", "ornek_kisi").Should().NotBe(b.GroupOf("tiktok", "ornek_kisi"),
            "ön koşul: iki bilgisayar aynı formdan farklı grup türetti (B eski gruba katıldı)");

        var (first, second) = aFirst ? (a, b) : (b, a);
        var firstGroup = first.GroupOf("tiktok", "ornek_kisi");
        for (var i = 0; i < 3; i++)
        {
            await first.TourAsync();
            await second.TourAsync();
        }

        foreach (var (platform, user) in new[] { ("tiktok", "ornek_kisi"), ("instagram", "ornek.kisi") })
        {
            var serverGroup = server.CanonicalOf(platform, user)!.Fields.GroupId;
            serverGroup.Should().Be(firstGroup, "sunucu eşit damgada ilk geleni tutar");
            a.GroupOf(platform, user).Should().Be(serverGroup);
            b.GroupOf(platform, user).Should().Be(serverGroup, "eşit damgada sunucunun değeri kazanır (incomingWinsTie)");
        }
    }

    [Fact]
    public async Task Shopper_gecici_kaydi_yerelde_acilmaz_bilgisayarin_satiri_devralir()
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        var claimPhone = TestPhone.NewE164();
        var provisional = server.RegisterShopper("tiktok", "beyanci_bir", "Örnek Beyan", claimPhone, "Beyan Adres");

        await a.TourAsync();
        a.Identity("beyanci_bir").Should().BeEmpty("kural 7: geçici satır yerelde ASLA açılmaz");

        var own = a.Chat("beyanci_bir");
        await a.TourAsync();             // kendi Id'siyle gönderim → sunucu devralır (S8)
        await a.TourAsync();

        server.TakenOver.Should().Be(1);
        server.Get(provisional).MergedIntoId.Should().Be(Guid.Parse(own));
        var canonical = server.CanonicalOf("tiktok", "beyanci_bir")!;
        canonical.Id.Should().Be(Guid.Parse(own));
        canonical.CreatedByShopper.Should().BeFalse("yayıncının satırı asıl kayıt");
        canonical.Fields.Phone.Should().BeNull("beyan telefonu asıl kayda taşınmaz — sahiplenenin bağlantısı yeniden kanıt ister");
        a.Identity("beyanci_bir").Should().ContainSingle().Which.Phone.Should().BeNull();
    }

    [Fact]
    public async Task Eski_ingestin_miras_satiri_donusur_beyan_duser_yayinci_notu_kalir_sunucu_devralir()
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        var claimPhone = TestPhone.NewE164();
        var provisional = server.RegisterShopper("tiktok", "beyanci_iki", "Örnek Beyan", claimPhone, "Beyan Adres");
        a.LegacyRow(provisional, "beyanci_iki", "Örnek Beyan", claimPhone, "Beyan Adres", notes: "kapıda");
        var label = a.Sell("beyanci_iki");    // sohbet miras satırını adla bulur → etiket geçici Id'de

        await a.TourAsync();             // gönderim → akış: geçici → dönüştürme (U9) → gönderim: devralma
        await a.TourAsync();

        var local = a.Identity("beyanci_iki").Should().ContainSingle().Subject;
        local.Id.Should().NotBe(provisional.ToString("N"));
        local.Notes.Should().Be("kapıda", "göç öncesi yayıncı notu beyan değil — kalır");
        local.Phone.Should().BeNull("beyana eşit damgasız telefon düştü");
        local.DisplayName.Should().BeNull("eski ingest beyan adını takma ada yazmıştı — düştü");
        a.LabelOwner(label.Id).Should().Be(local.Id);
        a.Customers.ResolveId(provisional.ToString("N")).Should().Be(local.Id, "uçuştaki eski Id yazımları yeni Id'ye iner (U12)");
        server.TakenOver.Should().Be(1);
        server.Get(provisional).MergedIntoId.Should().Be(Guid.Parse(local.Id));
        var canonical = server.CanonicalOf("tiktok", "beyanci_iki")!;
        canonical.Id.Should().Be(Guid.Parse(local.Id));
        canonical.Fields.Phone.Should().BeNull();
        canonical.Fields.Notes.Should().Be("kapıda");
    }

    [Fact]
    public async Task Miras_satirinda_telefon_duzeltilirse_sunucu_gecici_kaydi_benimser_donusturme_olmaz()
    {
        // S17: geçici satır yalnız DAMGALI telefonla (biçim 2) benimsenir — yayıncının düzelttiği
        // telefon kişiyi kanıtlar, satır asıl kayıt olarak kalır; akış onu artık geçici bildirmez,
        // miras satırı dönüştürülmez (U9 yalnız hâlâ geçici satır için) ve devralma olmaz.
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        var claimPhone = TestPhone.NewE164();
        var provisional = server.RegisterShopper("tiktok", "beyanci_dort", "Örnek Beyan", claimPhone, "Beyan Adres");
        a.LegacyRow(provisional, "beyanci_dort", "Örnek Beyan", claimPhone, "Beyan Adres", notes: "kapıda");
        var fixedPhone = TestPhone.NewE164();
        a.Customers.UpdatePhone(provisional.ToString("N"), fixedPhone).Should().Be(1);

        await a.TourAsync();
        await a.TourAsync();

        server.TakenOver.Should().Be(0);
        var canonical = server.CanonicalOf("tiktok", "beyanci_dort")!;
        canonical.Id.Should().Be(provisional);
        canonical.CreatedByShopper.Should().BeFalse("damgalı telefon geçici kaydı benimsetti");
        canonical.Fields.Phone.Should().Be(fixedPhone);
        var local = a.Identity("beyanci_dort").Should().ContainSingle().Subject;
        local.Id.Should().Be(provisional.ToString("N"), "benimsenen kayıt geçici değil — dönüştürülmez");
        local.Phone.Should().Be(fixedPhone);
        local.Notes.Should().Be("kapıda");
    }

    [Fact]
    public async Task Baska_bilgisayar_devraldiktan_sonra_miras_satiri_notunu_tasir_beyan_birimlerini_tasimaz()
    {
        // U3(a) kalan kaybın sınırı: devralma başka bilgisayarda olduysa beyan bilinmez — beyan
        // olabilen damgasız birimler taşınmaz, beyan olamayanlar (not) taşınır.
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        using var b = new Pc(server);
        var claimPhone = TestPhone.NewE164();
        var provisional = server.RegisterShopper("tiktok", "beyanci_uc", "Örnek Beyan", claimPhone, "Beyan Adres");
        a.LegacyRow(provisional, "beyanci_uc", "Örnek Beyan", claimPhone, "Beyan Adres", notes: "kapıda");
        var own = b.Chat("Beyanci_Uc");
        await b.TourAsync();             // B devraldı: geçici satır kopya, beyan boşaltıldı

        await a.TourAsync();             // A'nın miras gönderimi güvenilmez (S7); asıl kayıt inince sahip taşınır
        await a.TourAsync();

        server.UntrustedAliasPushes.Should().BeGreaterThan(0);
        var local = a.Identity("beyanci_uc").Should().ContainSingle().Subject;
        local.Id.Should().Be(own);
        local.Notes.Should().Be("kapıda");
        local.Phone.Should().BeNull();
        server.CanonicalOf("tiktok", "beyanci_uc")!.Fields.Notes.Should().Be("kapıda", "taşınan not gönderildi");
    }

    [Fact]
    public async Task Silme_iki_bilgisayara_iner_mali_kayit_kalir()
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        using var b = new Pc(server);
        var id = a.Chat("ornek_kisi");
        a.Customers.UpdatePhone(id, TestPhone.NewE164());
        var label = a.Sell("ornek_kisi");
        await a.TourAsync();
        await b.TourAsync();
        b.Identity("ornek_kisi").Should().ContainSingle();

        server.Purge(Guid.Parse(id));
        await a.TourAsync();
        await b.TourAsync();

        foreach (var pc in new[] { a, b })
        {
            var row = pc.Identity("ornek_kisi").Single();
            row.Id.Should().Be(id);
            row.DisplayName.Should().Be("[Silindi]");
            row.Phone.Should().BeNull();
            using var c = pc.Db.Open();
            c.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(1);
        }
        a.LabelOwner(label.Id).Should().Be(id, "satış kaydı silmeden etkilenmez");
    }

    [Fact]
    public async Task Sunucu_yedekten_donunce_imlec_sifirlanir_veri_yeniden_gonderilir()
    {
        using var server = new FakeCustomerServer();
        using var a = new Pc(server);
        var id = a.Chat("ornek_kisi");
        await a.TourAsync();
        var snapshot = server.Snapshot();
        a.Customers.UpdateNotes(id, "yedekten sonra");
        await a.TourAsync();
        server.CanonicalOf("tiktok", "ornek_kisi")!.Fields.Notes.Should().Be("yedekten sonra");

        server.Restore(snapshot);        // sunucu notu ve sayacı kaybetti
        await a.TourAsync();             // CursorReset → akış baştan, gönderim imleci geri sarılır
        await a.TourAsync();             // turun başındaki gönderim her şeyi yeniden götürür

        server.CanonicalOf("tiktok", "ornek_kisi")!.Fields.Notes.Should().Be("yedekten sonra");
        a.Identity("ornek_kisi").Single().Notes.Should().Be("yedekten sonra", "sunucunun eski hâli yerel damgalı notu ezmez");
    }
}
