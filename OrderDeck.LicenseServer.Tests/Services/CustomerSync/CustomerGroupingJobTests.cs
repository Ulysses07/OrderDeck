using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="CustomerGroupingJob"/> (<c>group-customers</c>): yayıncının elle
/// seçtiği (alıcı, kayıtlı) çiftleri sunucuda damgalı tek gruba koyar. Gerçek
/// SQL Server: iş bileşen başına işlem açar, "dokunulmadı" kanıtı rowversion
/// (<see cref="WpfCustomerProjection.ChangeSeq"/>) ister — InMemory ikisini de
/// sağlamaz. Şema BUGÜNKÜ (B1 indeksi yerinde): iş kimlik değiştirmez. Her test
/// METODU kendi veritabanını alır.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerGroupingJobTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;
    public CustomerGroupingJobTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddDays(-30);

    private static async Task<Guid> NewLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "Gruplama İşi Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "grp-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary>Kullanıcı adı üretilmiş; grup numarası verilirse damgasız (göç
    /// 046'nın eski grupları gibi).</summary>
    private static WpfCustomerProjection Row(Guid license, string? groupId = null, string platform = "instagram") => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, Platform = platform,
        Username = "u" + Guid.NewGuid().ToString("N")[..10], GroupId = groupId, UpdatedAt = T0,
    };

    /// <summary>Ordinal sırası bilinen grup numarası: ilk hane <paramref name="lead"/>.</summary>
    private static string Group(char lead) => lead + Guid.NewGuid().ToString("N")[1..];

    private static CustomerGroupingJob.Pair P(WpfCustomerProjection buyer, WpfCustomerProjection registered)
        => new(buyer.Id, registered.Id);

    private static CustomerGroupingJob Job(LicenseDbContext db, ILogger<CustomerGroupingJob>? log = null) => new(db, log);

    private static async Task<Dictionary<Guid, WpfCustomerProjection>> RowsAsync(LicenseDbContext db, Guid license)
    {
        db.ChangeTracker.Clear();
        return await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == license).ToDictionaryAsync(p => p.Id);
    }

    [Fact]
    public async Task Kuru_calistirma_hicbir_sey_yazmaz_uygulamayla_ayni_sayilari_verir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l = Group('a');
        var buyer = Row(lic, platform: "tiktok");
        var registered = Row(lic, l);
        var member = Row(lic, l);
        member.IsBlacklisted = true;
        member.BlacklistReason = "ödeme yapmadı";
        member.BlacklistedAt = T0;
        db.WpfCustomerProjections.AddRange(buyer, registered, member);
        await db.SaveChangesAsync();
        var before = await RowsAsync(db, lic);

        var dry = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: false, default);

        dry.Should().Be(new CustomerGroupingJob.Report(
            PairsRead: 1, PairsResolved: 1, PairsUnresolved: 0, AlreadyTogether: 0, Components: 1,
            RowsToChange: 1, ComponentsJoiningRegistered: 0, BlacklistPropagations: 2, FailedComponents: 0));
        var after = await RowsAsync(db, lic);
        foreach (var (id, row) in before)
            after[id].ChangeSeq.Should().Be(row.ChangeSeq, "kuru çalıştırma hiçbir satırı yazmaz");

        (await Job(db).RunAsync(lic, [P(buyer, registered)], apply: true, default)).Should().Be(dry);
    }

    [Fact]
    public async Task Grupsuz_alici_kayitlinin_grubuna_damgali_girer_kayitli_satira_dokunulmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l = Group('b');
        var buyer = Row(lic);
        var registered = Row(lic, l);
        db.WpfCustomerProjections.AddRange(buyer, registered);
        await db.SaveChangesAsync();
        var before = await RowsAsync(db, lic);
        var start = DateTimeOffset.UtcNow;

        var report = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: true, default);

        report.RowsToChange.Should().Be(1);
        report.FailedComponents.Should().Be(0);
        var rows = await RowsAsync(db, lic);
        rows[buyer.Id].GroupId.Should().Be(l);
        rows[buyer.Id].GroupIdChangedAt.Should().NotBeNull().And.BeOnOrAfter(start.AddSeconds(-1));
        rows[buyer.Id].UpdatedAt.Should().Be(T0, "UpdatedAt'e dokunulmaz — akış rowversion'la taşır");
        rows[buyer.Id].ChangeSeq.Should().BeGreaterThan(before[buyer.Id].ChangeSeq);
        rows[registered.Id].GroupIdChangedAt.Should().BeNull("zaten hedefte: damga değişmez");
        rows[registered.Id].ChangeSeq.Should().Be(before[registered.Id].ChangeSeq, "zaten hedefte olan satır yazılmaz");
    }

    [Fact]
    public async Task Kendi_grubu_olan_alicinin_butun_uyeleri_kayitlinin_grubuna_tasinir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        // Alıcının numarası ordinal olarak KÜÇÜK: hedef yine kayıtlı tarafınki.
        var g = Group('0');
        var l = Group('f');
        var buyer = Row(lic, g);
        var buyerMember = Row(lic, g, "tiktok");
        var registered = Row(lic, l);
        db.WpfCustomerProjections.AddRange(buyer, buyerMember, registered);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: true, default);

        report.Components.Should().Be(1);
        report.RowsToChange.Should().Be(2);
        var rows = await RowsAsync(db, lic);
        rows[buyer.Id].GroupId.Should().Be(l);
        rows[buyerMember.Id].GroupId.Should().Be(l, "alıcının grubunun öbür üyesi de taşınır");
        rows[buyerMember.Id].GroupIdChangedAt.Should().NotBeNull();
        rows[registered.Id].GroupIdChangedAt.Should().BeNull();
    }

    [Fact]
    public async Task Iki_kayitliya_eslenen_alici_hepsini_kucuk_numarada_toplar_obur_grubun_uyeleri_damgalanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l1 = Group('1');
        var l2 = Group('2');
        var buyer = Row(lic);
        var r1 = Row(lic, l1);
        var m1 = Row(lic, l1, "tiktok");
        var r2 = Row(lic, l2);
        var m2 = Row(lic, l2, "facebook");
        db.WpfCustomerProjections.AddRange(buyer, r1, m1, r2, m2);
        await db.SaveChangesAsync();

        // Sıra önemsiz: büyük numaralı kayıtlı önce gelse de hedef küçük olan.
        var report = await Job(db).RunAsync(lic, [P(buyer, r2), P(buyer, r1)], apply: true, default);

        report.Components.Should().Be(1);
        report.RowsToChange.Should().Be(3);
        report.ComponentsJoiningRegistered.Should().Be(1, "iki ayrı kayıtlı grup tek kişi oluyor");
        var rows = await RowsAsync(db, lic);
        new[] { buyer, r1, m1, r2, m2 }.Select(r => rows[r.Id].GroupId).Should().AllBe(l1);
        rows[r2.Id].GroupIdChangedAt.Should().NotBeNull();
        rows[m2.Id].GroupIdChangedAt.Should().NotBeNull();
        rows[r1.Id].GroupIdChangedAt.Should().BeNull();
        rows[m1.Id].GroupIdChangedAt.Should().BeNull();
    }

    [Fact]
    public async Task Kara_liste_en_yeni_kaynagin_sebebiyle_gruba_yayilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l = Group('c');
        var buyer = Row(lic);
        var registered = Row(lic, l);
        registered.IsBlacklisted = true;
        registered.BlacklistReason = "eski sebep";
        registered.BlacklistedAt = T0.AddDays(-5);
        registered.BlacklistChangedAt = T0.AddDays(-5);
        var newest = Row(lic, l, "tiktok");
        newest.IsBlacklisted = true;
        newest.BlacklistReason = "yeni sebep";
        newest.BlacklistedAt = T0.AddDays(-1);
        // Tarihsiz kara liste sona: kaynak olamaz.
        var undated = Row(lic, l, "facebook");
        undated.IsBlacklisted = true;
        undated.BlacklistReason = "tarihsiz";
        var clean = Row(lic, l, "youtube");
        db.WpfCustomerProjections.AddRange(buyer, registered, newest, undated, clean);
        await db.SaveChangesAsync();
        var start = DateTimeOffset.UtcNow;

        var report = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: true, default);

        report.BlacklistPropagations.Should().Be(2, "alıcı ve grubun kara listede olmayan üyesi");
        var rows = await RowsAsync(db, lic);
        foreach (var id in new[] { buyer.Id, clean.Id })
        {
            rows[id].IsBlacklisted.Should().BeTrue();
            rows[id].BlacklistReason.Should().Be("yeni sebep");
            rows[id].BlacklistedAt.Should().BeCloseTo(T0.AddDays(-1), TimeSpan.FromMilliseconds(1));
            rows[id].BlacklistChangedAt.Should().NotBeNull().And.BeOnOrAfter(start.AddSeconds(-1));
        }
        rows[registered.Id].BlacklistReason.Should().Be("eski sebep", "kara listedeki üyeye dokunulmaz");
        rows[undated.Id].BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public async Task Silinmis_uye_kara_liste_kaynagi_olur_hedef_olmaz()
    {
        // Masaüstünün PropagateGroupBlacklist'i gibi: KVKK boşaltması kara listeyi
        // korur, silinmiş üye kaynak seçilir. Silinmiş satır ise hiç yazılmaz.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l = Group('7');
        var buyer = Row(lic);
        var registered = Row(lic, l);
        var purgedSource = Row(lic, l, "tiktok");
        purgedSource.IsBlacklisted = true;
        purgedSource.BlacklistReason = "sahte sipariş";
        purgedSource.BlacklistedAt = T0.AddDays(-2);
        purgedSource.MarkPurged(T0);
        var purgedClean = Row(lic, l, "facebook");
        purgedClean.MarkPurged(T0);
        db.WpfCustomerProjections.AddRange(buyer, registered, purgedSource, purgedClean);
        await db.SaveChangesAsync();
        var before = await RowsAsync(db, lic);

        var dry = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: false, default);
        var applied = await Job(db).RunAsync(lic, [P(buyer, registered)], apply: true, default);

        dry.BlacklistPropagations.Should().Be(2, "alıcı ve kayıtlı satır; silinmişler hedef değil");
        applied.Should().Be(dry);
        var rows = await RowsAsync(db, lic);
        foreach (var id in new[] { buyer.Id, registered.Id })
        {
            rows[id].IsBlacklisted.Should().BeTrue();
            rows[id].BlacklistReason.Should().Be("sahte sipariş");
            rows[id].BlacklistedAt.Should().BeCloseTo(T0.AddDays(-2), TimeSpan.FromMilliseconds(1));
        }
        rows[purgedClean.Id].IsBlacklisted.Should().BeFalse("silinmiş satır hedef olmaz");
        rows[purgedClean.Id].ChangeSeq.Should().Be(before[purgedClean.Id].ChangeSeq);
        rows[purgedSource.Id].ChangeSeq.Should().Be(before[purgedSource.Id].ChangeSeq, "kaynak yalnız okunur");
    }

    [Fact]
    public async Task Kopya_Idsi_asil_kayda_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l = Group('d');
        var canonical = Row(lic);
        var copy = Row(lic);
        copy.MergedIntoId = canonical.Id;
        var copyOfCopy = Row(lic);
        copyOfCopy.MergedIntoId = copy.Id; // iki adım: sınırın içinde
        var registered = Row(lic, l);
        var otherCanonical = Row(lic);
        var otherCopy = Row(lic);
        otherCopy.MergedIntoId = otherCanonical.Id;
        db.WpfCustomerProjections.AddRange(canonical, copy, copyOfCopy, registered, otherCanonical, otherCopy);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(
            lic, [P(copyOfCopy, registered), P(otherCopy, registered)], apply: true, default);

        report.PairsResolved.Should().Be(2);
        var rows = await RowsAsync(db, lic);
        rows[canonical.Id].GroupId.Should().Be(l);
        rows[otherCanonical.Id].GroupId.Should().Be(l);
        rows[copy.Id].GroupId.Should().BeNull("kopyaya yazılmaz");
        rows[copyOfCopy.Id].GroupId.Should().BeNull();
        rows[otherCopy.Id].GroupId.Should().BeNull();
    }

    [Fact]
    public async Task Bulunamayan_baska_lisansin_silinmis_ve_uzun_zincirli_Idler_cozulmez_atlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var otherLic = await NewLicenseAsync(db);
        var l = Group('e');
        var registered = Row(lic, l);
        var buyer = Row(lic);
        var purged = Row(lic);
        purged.MarkPurged(T0);
        var foreign = Row(otherLic);
        // Dört adımlı zincir: asıl kayda ancak MaxHops'tan (3) uzun yoldan varılır.
        var head = Row(lic);
        var chain = new List<WpfCustomerProjection> { head };
        for (var i = 0; i < 4; i++)
        {
            var link = Row(lic);
            link.MergedIntoId = chain[^1].Id;
            chain.Add(link);
        }
        db.WpfCustomerProjections.AddRange(registered, buyer, purged, foreign);
        db.WpfCustomerProjections.AddRange(chain);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic,
        [
            new CustomerGroupingJob.Pair(Guid.NewGuid(), registered.Id),
            P(purged, registered),
            P(foreign, registered),
            P(chain[^1], registered),
            P(buyer, registered),
        ], apply: true, default);

        report.PairsRead.Should().Be(5);
        report.PairsResolved.Should().Be(1);
        report.PairsUnresolved.Should().Be(4);
        report.RowsToChange.Should().Be(1);
        var rows = await RowsAsync(db, lic);
        rows[buyer.Id].GroupId.Should().Be(l);
        rows[purged.Id].GroupId.Should().BeNull("silinmiş kişi gruplanmaz");
        rows[head.Id].GroupId.Should().BeNull("uzun zincir çözülmez");
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == foreign.Id))
            .GroupId.Should().BeNull("başka lisansın satırına dokunulmaz");
    }

    [Fact]
    public async Task Ikinci_uygulama_hicbir_satiri_degistirmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var l1 = Group('1');
        var buyer = Row(lic);
        var r1 = Row(lic, l1);
        r1.IsBlacklisted = true;
        r1.BlacklistReason = "iade suistimali";
        r1.BlacklistedAt = T0;
        var r2 = Row(lic, Group('2'));
        var loneBuyer = Row(lic);
        var loneRegistered = Row(lic); // iki ucu da grupsuz: yeni numara
        db.WpfCustomerProjections.AddRange(buyer, r1, r2, loneBuyer, loneRegistered);
        await db.SaveChangesAsync();
        IReadOnlyList<CustomerGroupingJob.Pair> pairs = [P(buyer, r1), P(buyer, r2), P(loneBuyer, loneRegistered)];

        var first = await Job(db).RunAsync(lic, pairs, apply: true, default);
        first.Components.Should().Be(2);
        first.RowsToChange.Should().Be(4);
        var afterFirst = await RowsAsync(db, lic);
        afterFirst[loneBuyer.Id].GroupId.Should().NotBeNullOrWhiteSpace()
            .And.Be(afterFirst[loneRegistered.Id].GroupId, "iki grupsuz uç yeni bir numarada buluşur");

        var second = await Job(db).RunAsync(lic, pairs, apply: true, default);

        second.Should().Be(new CustomerGroupingJob.Report(
            PairsRead: 3, PairsResolved: 3, PairsUnresolved: 0, AlreadyTogether: 3, Components: 0,
            RowsToChange: 0, ComponentsJoiningRegistered: 0, BlacklistPropagations: 0, FailedComponents: 0));
        var afterSecond = await RowsAsync(db, lic);
        foreach (var (id, row) in afterFirst)
            afterSecond[id].ChangeSeq.Should().Be(row.ChangeSeq, "ikinci uygulama hiçbir satırı yazmaz");
    }

    [Fact]
    public void Damga_kosunun_ani_satirinki_ondan_eski_degilse_bir_ms_sonrasi()
    {
        var now = DateTimeOffset.UtcNow;
        CustomerGroupingJob.StampAt(now, null).Should().Be(now);
        CustomerGroupingJob.StampAt(now, now.AddMinutes(-5)).Should().Be(now);
        var ahead = now.AddMinutes(3); // saati ileri bir bilgisayarın damgası
        CustomerGroupingJob.StampAt(now, ahead).Should().Be(ahead.AddMilliseconds(1));
        // Aynı milisaniye: bilgisayar eşit sayardı — yine 1 ms sonrası.
        var sameMs = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        CustomerGroupingJob.StampAt(now, sameMs).Should().Be(sameMs.AddMilliseconds(1));
    }

    [Fact]
    public async Task Cakisan_bilesen_geri_alinir_sayilir_is_surer_gunluge_kisisel_veri_yazmaz()
    {
        Guid lic;
        WpfCustomerProjection a, aMember, ra, b, rb;
        var la = Group('a');
        var lb = Group('b');
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            var ga = Group('9');
            a = Row(lic, ga);
            aMember = Row(lic, ga, "tiktok");
            ra = Row(lic, la);
            b = Row(lic);
            rb = Row(lic, lb);
            db.WpfCustomerProjections.AddRange(a, aMember, ra, b, rb);
            await db.SaveChangesAsync();
        }

        // Eşzamanlı KVKK silmesi: ilk bileşen kaydedilmeden HEMEN ÖNCE ayrı bir
        // bağlantıdan; UPDATE'i PurgedAt jetonuna takılır.
        var hook = new SaveHookInterceptor();
        await using var hooked = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_cs).AddInterceptors(hook).Options);
        var fired = false;
        hook.BeforeSave = async () =>
        {
            if (fired || !hooked.ChangeTracker.Entries<WpfCustomerProjection>().Any(e => e.Entity.Id == aMember.Id)) return;
            fired = true;
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE WpfCustomerProjections SET PurgedAt = SYSDATETIMEOFFSET() WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", aMember.Id);
            await cmd.ExecuteNonQueryAsync();
        };
        var log = new LogRecorder<CustomerGroupingJob>();
        IReadOnlyList<CustomerGroupingJob.Pair> pairs = [P(a, ra), P(b, rb)];

        var first = await Job(hooked, log).RunAsync(lic, pairs, apply: true, default);

        fired.Should().BeTrue();
        first.Components.Should().Be(2);
        first.FailedComponents.Should().Be(1);
        var entry = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain(nameof(DbUpdateConcurrencyException)).And.Contain(lic.ToString());
        entry.Message.Should().NotContain(a.Username).And.NotContain(la);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await RowsAsync(db, lic);
            rows[a.Id].GroupId.Should().NotBe(la, "çakışan bileşen BÜTÜNÜYLE geri alındı");
            rows[b.Id].GroupId.Should().Be(lb, "öteki bileşen uygulandı");

            // Yeniden koşu kalanı tamamlar; silinen üye artık grubun dışında.
            var second = await Job(db).RunAsync(lic, pairs, apply: true, default);
            second.FailedComponents.Should().Be(0);
            second.Components.Should().Be(1);
            second.RowsToChange.Should().Be(1);
            rows = await RowsAsync(db, lic);
            rows[a.Id].GroupId.Should().Be(la);
            rows[aMember.Id].GroupId.Should().NotBe(la, "silinmiş satıra yazılmaz");
        }
    }
}
