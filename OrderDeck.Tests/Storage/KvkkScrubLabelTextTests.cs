using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// KVKK metin boşaltması, yerel taraf (spec 2026-10-13 §9 "Ayar açıkken yerelde", §11 KVKK kararı; W2-02). Müşteri
/// akışından inen silme (<see cref="CustomerRepository.RecordPurge(string, string, long, out bool, out int)"/>),
/// <c>SalesSyncState.ScrubOrderText = 1</c> iken o kimliğin BÜTÜN yerel etiketlerinde takma adı ve yorumu boşaltır:
/// kendi oturumunda da, başka bilgisayardan inmiş <c>Authority = 'server'</c> oturumda da. Satır, CustomerId, kullanıcı
/// adı, kod ve fiyat kalır; <c>SyncedAt</c>, <c>StockSyncedAt</c>, <c>Revision</c> ve <c>ServerVersion</c> değişmez
/// (boşaltma gönderilmez: sunucu kendisi boşaltır). Ayar kapalıyken (üretim, sahip kararı) etiketlere dokunulmaz.
/// </summary>
public sealed class KvkkScrubLabelTextTests : IDisposable
{
    private const long PurgedAt = 1_790_000_000L;
    private const long StartedAt = 1_789_990_000L;

    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;

    public KvkkScrubLabelTextTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Etiketin sınanan kolonlarının tamamı (ham).</summary>
    private sealed record LabelRow
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
        public double Price { get; init; }
        public long Revision { get; init; }
        public long? SyncedAt { get; init; }
        public long? StockSyncedAt { get; init; }
        public long? ServerVersion { get; init; }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private string Customer(string username, string platform = "tiktok")
    {
        var id = NewId();
        _customers.Insert(new Customer(id, platform, username, "Takma Ad", null, FirstSeenAt: 100, LastSeenAt: 200,
            IsBlacklisted: false, BlacklistReason: null, Notes: null, TotalLabelsPrinted: 1, TotalAmount: 150m,
            BlacklistedAt: null, Address: null, Phone: TestPhone.NewE164()));
        return id;
    }

    /// <summary>Bu bilgisayarın canlı (açık) yayını.</summary>
    private string OwnLiveSession()
    {
        var id = NewId();
        new SessionRepository(_db).Insert(new StreamSession(id, "Kendi yayını", StartedAt, null, ["tiktok"], null));
        return id;
    }

    /// <summary>Başka bilgisayarın devredilmiş, akıştan inmiş yayını (<c>Origin = 'remote'</c>, <c>Authority = 'server'</c>).</summary>
    private string RemoteServerSession()
    {
        var id = NewId();
        Exec(@"INSERT INTO StreamSession (Id, Title, StartedAt, EndedAt, Platforms, SyncedAt, Origin, Authority,
                                          OwnerMachineName, ReleasedAt)
               VALUES (@id, 'Uzak yayın', @startedAt, @endedAt, '[""tiktok""]', @endedAt, 'remote', 'server', 'PC-B', @endedAt)",
            new { id, startedAt = StartedAt, endedAt = StartedAt + 3600 });
        return id;
    }

    /// <summary>Etiket, kilit altında (sunucu otoritesindeki oturuma da ekleyebilsin — akışın eklemesi gibi).</summary>
    private string InsertLabel(string sessionId, string customerId, string username, bool synced,
        string platform = "tiktok", string code = "A1", decimal price = 150m, int revision = 2, int? serverVersion = 4)
    {
        var id = NewId();
        using var scope = SyncApplyScope.Begin(_db);
        scope.Execute(
            @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price,
                                 AddedAt, PrintedAt, Revision, SyncedAt, StockSyncedAt, ServerVersion)
              VALUES (@id, @sessionId, @customerId, @platform, @username, 'Takma Ad', 'MAVI XL aldım', @code, @price,
                      @addedAt, @addedAt, @revision, @syncedAt, @syncedAt, @serverVersion)",
            new
            {
                id, sessionId, customerId, platform, username, code, price = (double)price,
                addedAt = StartedAt + 60, revision,
                syncedAt = synced ? StartedAt + 200 : (long?)null,
                serverVersion = synced ? serverVersion : null,
            });
        scope.Commit();
        return id;
    }

    private void SetScrub(bool on) => Exec("UPDATE SalesSyncState SET ScrubOrderText = @v WHERE Id = 1", new { v = on ? 1 : 0 });

    private Dictionary<string, LabelRow> Labels()
    {
        using var c = _db.Open();
        return c.Query<LabelRow>(
                @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, Revision,
                         SyncedAt, StockSyncedAt, ServerVersion
                  FROM Label")
            .ToDictionary(r => r.Id);
    }

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private int Scalar(string sql)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>(sql);
    }

    [Fact]
    public void Ayar_acikken_silinen_kisinin_kendi_ve_uzak_oturumdaki_etiket_metni_bosalir_birimler_ve_onay_kalir()
    {
        var victim = Customer("silinen_kisi");
        var other = Customer("baska_kisi");
        var own = OwnLiveSession();
        var remote = RemoteServerSession();
        var victimOwn = InsertLabel(own, victim, "silinen_kisi", synced: true, code: "A1", price: 150m, revision: 2, serverVersion: 4);
        var victimPending = InsertLabel(own, victim, "silinen_kisi", synced: false, code: "B2", price: 90m, revision: 1);
        var victimRemote = InsertLabel(remote, victim, "silinen_kisi", synced: true, code: "C3", price: 75m, revision: 0, serverVersion: 7);
        var otherOwn = InsertLabel(own, other, "baska_kisi", synced: true);
        var otherRemote = InsertLabel(remote, other, "baska_kisi", synced: true);
        SetScrub(true);
        var before = Labels();

        // Harf farkı: akıştaki yazım yereldekinden farklı olabilir (NOCASE + kimlik anahtarı).
        _customers.RecordPurge("tiktok", "SILINEN_KISI", PurgedAt, out var changed, out var scrubbedLabels);

        changed.Should().BeTrue();
        scrubbedLabels.Should().Be(3);
        var after = Labels();
        foreach (var id in new[] { victimOwn, victimPending, victimRemote })
            after[id].Should().Be(before[id] with { DisplayName = null, MessageText = "" },
                "yalnız takma ad ve yorum boşalır; satır, CustomerId, kod, fiyat, Revision, SyncedAt, StockSyncedAt ve " +
                "ServerVersion kalır — boşaltma gönderilmez, sunucu kendisi boşaltır");
        after[otherOwn].Should().Be(before[otherOwn], "başka kişinin etiketine dokunulmaz");
        after[otherRemote].Should().Be(before[otherRemote], "başka kişinin etiketine dokunulmaz");
        Scalar("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0, "kilit satırı commit'te kalkar");
        _customers.GetById(victim)!.DisplayName.Should().Be("[Silindi]", "müşteri satırı bugünkü gibi boşalır");
    }

    [Fact]
    public void Ayar_acikken_silmenin_yeniden_uygulanmasi_hicbir_sey_degistirmez()
    {
        var victim = Customer("silinen_kisi");
        var own = OwnLiveSession();
        var remote = RemoteServerSession();
        InsertLabel(own, victim, "silinen_kisi", synced: true);
        InsertLabel(remote, victim, "silinen_kisi", synced: true);
        SetScrub(true);
        _customers.RecordPurge("tiktok", "silinen_kisi", PurgedAt, out _, out _);
        var once = Labels();

        // CursorReset tekrarı ya da takılan sayfanın ilerisinde erken uygulanmış silmenin sırası gelince.
        _customers.RecordPurge("tiktok", "silinen_kisi", PurgedAt, out var changed, out var scrubbedLabels);

        changed.Should().BeFalse("zaten uygulanmış silme 'KVKK silme' günlüğünü yinelemez");
        scrubbedLabels.Should().Be(0);
        Labels().Should().BeEquivalentTo(once);
    }

    [Fact]
    public void Ayar_kapaliyken_etiketlere_dokunulmaz_musteri_bugunku_gibi_bosalir()
    {
        var victim = Customer("silinen_kisi");
        var own = OwnLiveSession();
        var remote = RemoteServerSession();
        InsertLabel(own, victim, "silinen_kisi", synced: true);
        InsertLabel(remote, victim, "silinen_kisi", synced: false);
        var before = Labels();

        _customers.RecordPurge("tiktok", "silinen_kisi", PurgedAt, out var changed, out var scrubbedLabels);

        changed.Should().BeTrue();
        scrubbedLabels.Should().Be(0);
        Labels().Should().BeEquivalentTo(before, "ayar kapalı (üretim, §11): bugünkü politika — Label satırlarına dokunulmaz");
        _customers.GetById(victim)!.DisplayName.Should().Be("[Silindi]");
    }

    [Fact]
    public void Ayar_acikken_kimligin_at_isaretli_ikizinin_etiketi_de_bosalir()
    {
        // IG/TikTok/FB: "ad" ile "@ad" aynı hesap — müşteri satırının boşaltması iki yazımı da kapsıyor, etiketler de.
        var bare = Customer("ornek_kisi", platform: "instagram");
        var twin = Customer("@ornek_kisi", platform: "instagram");
        var lookalike = Customer("ornek_kisi_2", platform: "instagram");
        var own = OwnLiveSession();
        var onBare = InsertLabel(own, bare, "ornek_kisi", synced: true, platform: "instagram");
        var onTwin = InsertLabel(own, twin, "@ornek_kisi", synced: true, platform: "instagram");
        var onLookalike = InsertLabel(own, lookalike, "ornek_kisi_2", synced: true, platform: "instagram");
        SetScrub(true);
        var before = Labels();

        _customers.RecordPurge("instagram", "ornek_kisi", PurgedAt, out _, out var scrubbedLabels);

        scrubbedLabels.Should().Be(2);
        var after = Labels();
        after[onBare].Should().Be(before[onBare] with { DisplayName = null, MessageText = "" });
        after[onTwin].Should().Be(before[onTwin] with { DisplayName = null, MessageText = "" });
        after[onLookalike].Should().Be(before[onLookalike]);
    }
}
