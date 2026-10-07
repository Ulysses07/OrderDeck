using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Gönderimin gördüğü müşteri satırı.</summary>
public sealed record CustomerSyncRow(
    string Id, string Platform, string Username, long LastSeenAt, long SyncSeq, CustomerSyncState Fields);

/// <summary>Değişiklik akışından gelen asıl kayıt (kopya satırları ve geçici olmayan silmeler
/// akış servisinde ayrı yoldan işlenir). <c>Fields</c> sunucu birimleri: damgalar unix ms,
/// BlacklistedAt unix s. Geçiciyse (<c>CreatedByShopper</c>) <c>Fields</c> Shopper'ın beyanıdır.</summary>
public sealed record ServerCustomer(
    string Id, string Platform, string Username, bool CreatedByShopper, CustomerSyncState Fields);

public enum FeedApplyResult
{
    /// <summary>Yapılacak bir şey yok (kopya yerelde yok / hedef henüz yerelde değil).</summary>
    Ignored,
    Inserted,
    Updated,
    Unchanged,
    /// <summary>Yerel kopya(lar) asıl kayda taşındı.</summary>
    Rekeyed,
    /// <summary>KVKK silmesi uygulandı (akış servisi sayar).</summary>
    Purged,
    /// <summary>Geçici sunucu satırı: yerelde açılmadı, hiçbir şey yazılmadı (kural 7).</summary>
    SkippedProvisional,
    /// <summary>Geçici Id'li miras satırı yeni yerel Id'ye dönüştürüldü (U9).</summary>
    Converted,
    /// <summary>Yerel kopya gönderilmemiş ya da ödeme akışında — gönderimi yönlendirmeyi
    /// döndürecek (U5, U13).</summary>
    Deferred,
    /// <summary>Yerel kimlik sahibi gönderilmemiş — akış bu öğede durur, gönderim koşar (U5).</summary>
    Stalled,
    /// <summary>Taşınacak/dönüştürülecek müşteri ödeme akışında — akış bu öğede kalır,
    /// sonraki turda yeniden (U13).</summary>
    Busy,
    /// <summary>Beş turda uygulanamayan öğe atlandı (U10; akış servisi verir).</summary>
    Skipped,
}

public enum RekeyResult
{
    SourceMissing,
    TargetMissing,
    Rekeyed,
    /// <summary>Kaynak ya da hedef ödeme akışında (U13): taşınmadı, kaynak aynı işlemde yeniden
    /// gönderime kondu — sonraki gönderimin yanıtı yönlendirmeyi yeniden getirir (S7).</summary>
    Busy,
    /// <summary>Kaynak, gönderilen partinin okunmasından SONRA değişti (M-7): taşınmadı. Zaten
    /// gönderim imlecinin üstünde — sonraki gönderim yeni hâlini götürür, sunucu yönlendirmeyi
    /// yeniden söyler.</summary>
    Deferred,
}

/// <summary>
/// Çoklu bilgisayar senkronunun müşteri tarafı (Bölüm C). CustomerRepository ekran ve
/// iş akışlarının; bu sınıf yalnız senkronun ihtiyaçları: tam satır okuma, sunucu
/// satırını uygulama, Id taşıma, miras satırı dönüştürme. Her yazım
/// <see cref="SyncApplyScope"/> altındadır: sunucudan inen veri ve taşıma düzenleme
/// DEĞİLDİR — damga basılmaz, SyncSeq yankı üretmez (bkz. göç 045). Taşıma ve
/// dönüştürmede asıl kaydın SyncSeq'i AÇIKÇA ilerletilir (<see cref="CustomerSyncSeq.Bump"/>)
/// ve <c>CustomerRedirect</c> aynı işlemde yazılır (U12). Her işlem <see cref="CustomerBusySet"/>
/// kilidi altında koşar; Id değiştiren yollar ödeme akışındaki müşteriye dokunmaz (U13).
///
/// <para><b>Kilit sırası (U17):</b> önce <see cref="CustomerBusySet"/> kilidi, sonra
/// <see cref="SyncApplyScope"/> (SQLite yazma kilidi). Kapsam açıkken bütün okuma ve yazımlar
/// kapsamın bağlantısı ve işlemiyle; ikinci bağlantı açılmaz. Kendi bağlantısını açan bakım
/// metotları hiçbir kapsamın içinden çağrılmaz.</para>
/// </summary>
public sealed class CustomerSyncRepository
{
    /// <summary>
    /// Customer.Id'ye başvuran HER yerel tablo (göç 001–045 şemasından): Label, GiveawayParticipant
    /// (FK), Shipment, PaymentJob (FK yok). Taşıma bunların HEPSİNİ yeni Id'ye geçirir
    /// (<see cref="MoveReferences"/> bu listeyle koşar). Şema koruma testi göç edilmiş şemadaki
    /// Customer FK'larını ve CustomerId kolonlarını bu listeyle karşılaştırır: yeni bir tablo eklenip
    /// buraya yazılmazsa test düşer — yazılmasaydı satırları silinen Id'de öksüz kalırdı.
    /// </summary>
    public static IReadOnlyList<string> CustomerReferenceTables { get; } =
        ["Label", "GiveawayParticipant", "Shipment", "PaymentJob"];

    private readonly IDbConnectionFactory _factory;
    private readonly CustomerBusySet? _busy;

    /// <param name="busy">Ödeme akışındaki müşteriler (U13). DI tekil örneği verir;
    /// null yalnız testlerde (hiçbir müşteri meşgul sayılmaz).</param>
    public CustomerSyncRepository(IDbConnectionFactory factory, CustomerBusySet? busy = null)
    {
        _factory = factory;
        _busy = busy;
    }

    private const string Columns = @"
        Id, Platform, Username, FirstSeenAt, LastSeenAt, SyncSeq, PurgedAt,
        TotalLabelsPrinted, TotalAmount, AvatarUrl,
        FullName, FullNameChangedAt, DisplayName, DisplayNameChangedAt,
        GroupId, GroupIdChangedAt,
        Address, City, District, AddressChangedAt,
        RecipientPaysActive, RecipientPaysChangedAt,
        Phone, PhoneChangedAt, Email, EmailChangedAt, Tckn, TcknChangedAt,
        WhatsAppConsent, WhatsAppConsentChangedAt, SmsConsent, SmsConsentChangedAt,
        IsBlacklisted, BlacklistReason, BlacklistedAt, BlacklistChangedAt,
        Notes, NotesChangedAt";

    /// <summary>Gönderim partisi: <c>SyncSeq &gt; since</c>, artan (036 sözleşmesi).</summary>
    public IReadOnlyList<CustomerSyncRow> GetForPush(long sinceSeq, int max)
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
                $"SELECT {Columns} FROM Customer WHERE SyncSeq > @since ORDER BY SyncSeq ASC LIMIT @max",
                new { since = sinceSeq, max })
            .Select(r => new CustomerSyncRow(r.Id, r.Platform, r.Username, r.LastSeenAt, r.SyncSeq, r.ToState()))
            .ToList();
    }

    /// <summary>
    /// Akıştaki asıl kaydı uygular.
    /// <list type="bullet">
    /// <item>Geçiciyse: <see cref="ApplyProvisional"/> (kural 7 — yerelde açılmaz).</item>
    /// <item>Yerelde aynı Id varsa: birim kuralları (<see cref="CustomerUnitMerge"/>; eşit damgada
    /// farklı değer sunucunun değeridir — U11).</item>
    /// <item>Yoksa ve yerelde aynı kimlikte (platform + <see cref="CustomerIdentity.KeyOrNull"/>)
    /// başka Id'li satır yoksa: sunucu verisiyle eklenir — boş satıra aynı kural (takma ad
    /// gerçek ad sayılmaz).</item>
    /// <item>Kimlik sahipleri varsa: biri gönderilmemişse (SyncSeq &gt; <paramref name="pushWatermark"/>)
    /// DURUR (U5); biri ödeme akışındaysa <see cref="FeedApplyResult.Busy"/> (U13). Değilse sahipler
    /// silinir, asıl kayıt sunucu verisiyle eklenir, sahiplerin taşınan kümesi (<see cref="Carried"/>)
    /// birleştirilir, referanslar taşınır, yönlendirmeler yazılır (U4, U12).</item>
    /// </list>
    /// </summary>
    public FeedApplyResult ApplyServerCustomer(ServerCustomer c, long pushWatermark, long nowUnix)
    {
        // Geçici satır ApplyProvisional'a gider. SİLİNMİŞ geçici satırı C7 doğrudan
        // ApplyProvisional(id, claims: null) ile uygular; buraya yine de boşaltılmış alanlarla gelirse
        // WithoutShopperClaims bütünüyle boş beyanı "bilinmiyor" sayar (C3 incelemesi M-2).
        if (c.CreatedByShopper) return ApplyProvisional(c.Id, c.Fields, nowUnix);

        return Locked(isBusy =>
        {
            using var scope = SyncApplyScope.Begin(_factory);
            var conn = scope.Connection;
            var tx = scope.Transaction;

            var local = ReadById(conn, tx, c.Id);
            if (local is not null)
            {
                var state = local.ToState();
                // incomingWinsTie (C2 kalite incelemesi): eşit damgada farklı değer → sunucunun değeri;
                // sunucu ilk geleni tuttuğu için bütün bilgisayarlar ona yakınsar.
                if (!CustomerUnitMerge.Apply(state, c.Fields, incomingWinsTie: true))
                    return FeedApplyResult.Unchanged;                 // Dispose: işlem geri alınır
                WriteFields(conn, tx, c.Id, state);
                scope.Commit();
                return FeedApplyResult.Updated;
            }

            var holders = ReadHolders(conn, tx, c.Platform, c.Username, excludeId: c.Id);
            if (holders.Count == 0)
            {
                InsertFromServer(conn, tx, c, firstSeenAt: nowUnix, lastSeenAt: nowUnix);
                scope.Commit();
                return FeedApplyResult.Inserted;
            }

            if (holders.Any(h => h.SyncSeq > pushWatermark)) return FeedApplyResult.Stalled;
            if (holders.Any(h => isBusy(h.Id))) return FeedApplyResult.Busy;

            // Sahiplerin kullanıcı adı asıl kaydınkiyle BİREBİR aynı olabilir (tekil indeks):
            // önce sahipler silinir, sonra asıl kayıt eklenir. Arada etiketler henüz var
            // olmayan Id'yi gösterir → FK denetimi işlem sonuna ertelenir (commit'te biter).
            conn.Execute("PRAGMA defer_foreign_keys = ON", transaction: tx);
            foreach (var h in holders) MoveReferences(conn, tx, h.Id, c.Id, nowUnix);
            foreach (var h in holders) conn.Execute("DELETE FROM Customer WHERE Id = @id", new { id = h.Id }, tx);
            InsertFromServer(conn, tx, c,
                firstSeenAt: holders.Min(h => h.FirstSeenAt), lastSeenAt: holders.Max(h => h.LastSeenAt));

            var target = ReadById(conn, tx, c.Id)!;                    // mezar taşı boşaltması sonrası gerçek hâl
            var merged = target.ToState();
            // Kopya→asıl: eşit damga yankıdır (incomingWinsTie YOK — sunucunun birleştirmesiyle aynı).
            foreach (var h in holders.OrderBy(h => h.SyncSeq))
                CustomerUnitMerge.Apply(merged, Carried(h));
            WriteFields(conn, tx, c.Id, merged);
            AddAggregates(conn, tx, c.Id, holders, includeSeen: false);
            BumpSyncSeq(conn, tx, c.Id);
            foreach (var h in holders) RecordRedirect(conn, tx, h.Id, c.Id, nowUnix);
            scope.Commit();
            return FeedApplyResult.Rekeyed;
        });
    }

    /// <summary>
    /// Geçici (CreatedByShopper) sunucu satırı — silinmiş ya da değil (kural 7, U9). Yerelde
    /// ASLA açılmaz, hiçbir satır ona taşınmaz. Yerelde aynı Id'li satır varsa eski
    /// <c>since</c> ingest'inin miras satırıdır: tek kilitli işlemde yeni yerel Id'ye
    /// dönüştürülür, beyana eşit damgasız birimleri düşer
    /// (<see cref="CustomerSyncState.WithoutShopperClaims"/>), referansları taşınır, SyncSeq
    /// ilerler, <c>CustomerRedirect</c> yazılır. Sonraki gönderim yeni Id'yi götürür → sunucu
    /// devralır (S8). Mezar taşı ve <c>PurgedAt</c> YAZILMAZ: geçici satırın silinmesi kimliğe
    /// yayılmaz.
    /// </summary>
    /// <param name="claims">Hâlâ geçici satırın beyanı; null = bilinmiyor (silinmiş geçici satır —
    /// beyan sunucuda boşaltıldı): beyan olabilen damgasız birimlerin hepsi düşer.</param>
    public FeedApplyResult ApplyProvisional(string id, CustomerSyncState? claims, long nowUnix)
        => Locked(isBusy =>
        {
            using var scope = SyncApplyScope.Begin(_factory);
            var conn = scope.Connection;
            var tx = scope.Transaction;

            var legacy = ReadById(conn, tx, id);
            if (legacy is null) return FeedApplyResult.SkippedProvisional;   // Dispose: yazım yok
            if (isBusy(id)) return FeedApplyResult.Busy;

            var newId = Guid.NewGuid().ToString("N");
            // Id kolonu değişince etiket/çekiliş FK'si işlem içinde geçici kırılır; referanslar
            // hemen taşınır, denetim commit'te. rowid aynı kalır → arama indeksi (035, rowid'e
            // bağlı) etkilenmez; Id hiçbir tetikleyicinin UPDATE OF listesinde değil.
            conn.Execute("PRAGMA defer_foreign_keys = ON", transaction: tx);
            conn.Execute("UPDATE Customer SET Id = @newId WHERE Id = @id", new { newId, id }, tx);
            MoveReferences(conn, tx, id, newId, nowUnix);
            WriteFields(conn, tx, newId, legacy.ToState().WithoutShopperClaims(claims));
            BumpSyncSeq(conn, tx, newId);
            RecordRedirect(conn, tx, id, newId, nowUnix);
            scope.Commit();
            return FeedApplyResult.Converted;
        });

    /// <summary>
    /// Akıştaki kopya satırı <c>{Id → MergedIntoId}</c>. Yerel kopya gönderilmemişse
    /// ertelenir (gönderimi yönlendirmeyi zaten döndürür — S7). Hedef yönlendirme tablosundan
    /// güncel Id'ye çözülür (U12); yerelde yoksa beklenir: asıl kayıt akışta geldiğinde bu satır
    /// kimlik sahibi olarak taşınır (U4). Kopya ya da hedef ödeme akışındaysa kopya kararla AYNI
    /// işlemde yeniden gönderime konur ve ertelenir (U13) — akış durmaz.
    /// </summary>
    public FeedApplyResult ApplyFeedRedirect(string aliasId, string targetId, long pushWatermark, long nowUnix)
        => Locked(isBusy =>
        {
            using var scope = SyncApplyScope.Begin(_factory);
            var conn = scope.Connection;
            var tx = scope.Transaction;
            var from = ReadById(conn, tx, aliasId);
            if (from is null) return FeedApplyResult.Ignored;
            if (from.SyncSeq > pushWatermark) return FeedApplyResult.Deferred;
            var to = ReadResolved(conn, tx, targetId);
            // Hedef yok ya da yerelde kopyanın KENDİSİNE çözülüyor (hedef Id burada dönüştürülüp
            // bu satır olmuştu): asıl kayıt akışta gelince kimlik sahibi yolu taşır (U4).
            if (to is null || to.Id == from.Id) return FeedApplyResult.Ignored;
            if (isBusy(from.Id) || isBusy(to.Id))
            {
                RequeueInScope(scope, from.Id);
                return FeedApplyResult.Deferred;
            }
            RekeyCore(conn, tx, from, to, nowUnix);
            scope.Commit();
            return FeedApplyResult.Rekeyed;
        });

    /// <summary>
    /// Push yanıtındaki yönlendirme (S6/S7): <paramref name="fromId"/> az önce gönderildi
    /// (damgasız verisi sunucuda birleşti), asıl kayıt yerelde ise taşınır. Hedef
    /// yönlendirme tablosundan çözülür (U12). Hedef yerelde değilse
    /// <see cref="RekeyResult.TargetMissing"/> — akış asıl kaydı getirince taşınır.
    /// Ödeme akışındaysa <see cref="RekeyResult.Busy"/>: kaynak kararla AYNI işlemde yeniden
    /// gönderime konur, sonraki turun yanıtı yönlendirmeyi yeniden getirir (S7).
    /// </summary>
    /// <param name="pushedThroughSeq">Yanıtı gelen gönderim partisinin en büyük SyncSeq'i (M-7).
    /// Kaynağın SyncSeq'i bundan büyükse satır parti okunduktan SONRA değişti — yeni hâli sunucuya
    /// gitmedi; şimdi taşınsaydı (ör. damgasız bir doldurma, C10) taşınmayan birimleri kaybolurdu.
    /// <see cref="RekeyResult.Deferred"/> döner: satır zaten imlecin üstünde, sonraki gönderim onu
    /// götürür ve sunucu yönlendirmeyi yeniden söyler.</param>
    public RekeyResult RekeyToLocal(string fromId, string toId, long pushedThroughSeq, long nowUnix)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal)) return RekeyResult.SourceMissing;
        return Locked(isBusy =>
        {
            using var scope = SyncApplyScope.Begin(_factory);
            var conn = scope.Connection;
            var tx = scope.Transaction;
            var from = ReadById(conn, tx, fromId);
            if (from is null) return RekeyResult.SourceMissing;
            if (from.SyncSeq > pushedThroughSeq) return RekeyResult.Deferred;
            var to = ReadResolved(conn, tx, toId);
            if (to is null || to.Id == from.Id) return RekeyResult.TargetMissing;
            if (isBusy(from.Id) || isBusy(to.Id))
            {
                RequeueInScope(scope, from.Id);
                return RekeyResult.Busy;
            }
            RekeyCore(conn, tx, from, to, nowUnix);
            scope.Commit();
            return RekeyResult.Rekeyed;
        });
    }

    /// <summary>
    /// Kimlik anahtarı eksik satırları ve mezar taşlarını onarır (eski sürüme dönüşte
    /// açılmış/yazılmış). Akış servisi süreç başına bir kez çağırır. Kilit gerekmez:
    /// IdentityKey hiçbir tetikleyici listesinde değil.
    /// </summary>
    public int HealIdentityKeys()
    {
        using var conn = _factory.Open();
        // Boş/boşluk kullanıcı adı kimlik değildir: od_identity_key NULL döner, satır NULL kalır
        // (C1 incelemesi M-1/N-1 — boş anahtar bütün boş adlı satırları tek kişi sayardı).
        const string heal = " SET IdentityKey = od_identity_key(Username) WHERE IdentityKey IS NULL AND od_identity_key(Username) IS NOT NULL";
        return conn.Execute("UPDATE Customer" + heal) + conn.Execute("UPDATE CustomerPurgeTombstone" + heal);
    }

    /// <summary>U15: kalmış kilit satırlarını siler (bkz. <see cref="SyncApplyScope.ClearStale"/>);
    /// akış servisi her turda çağırır.</summary>
    public int ClearStaleGuards() => SyncApplyScope.ClearStale(_factory);

    /// <summary>U8: uzlaştırma bekleyen anahtarlı açık miras ödeme işleri (taşımanın ya da 034
    /// göçünün bıraktığı; D1 dikkat sayacıyla aynı ölçüt).</summary>
    public int CountOpenKeyedLegacyJobs()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM PaymentJob
              WHERE ClosedAt IS NULL AND ApplyKey IS NOT NULL
                AND (ScopeKey = 'legacy' OR ScopeKey LIKE 'legacy:%')");
    }

    // ── zehirli akış öğesi (U10) ────────────────────────────────────────

    /// <summary>Başarısız denemeyi kalıcı sayar, güncel deneme sayısını döner. Aynı Id'nin
    /// DAHA YENİ bir değişikliği başarısız olursa sayaç baştan başlar (içerik değişti) — ilk
    /// başarısızlık anı da o değişikliğinki olur (inceleme M-9).</summary>
    public int RecordFeedFailure(string itemId, long changeSeq, string error, long nowUnix)
    {
        using var conn = _factory.Open();
        // SET ifadelerinin hepsi satırın ESKİ değerlerini görür (SQLite): ChangeSeq karşılaştırması
        // atama sırasından bağımsız.
        return conn.ExecuteScalar<int>(@"
            INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, LastError, FirstFailedAt)
            VALUES (@itemId, @changeSeq, 1, @error, @now)
            ON CONFLICT(ItemId) DO UPDATE SET
                Attempts  = CASE WHEN CustomerFeedFailure.ChangeSeq = excluded.ChangeSeq
                                 THEN CustomerFeedFailure.Attempts + 1 ELSE 1 END,
                FirstFailedAt = CASE WHEN CustomerFeedFailure.ChangeSeq = excluded.ChangeSeq
                                     THEN CustomerFeedFailure.FirstFailedAt ELSE excluded.FirstFailedAt END,
                ChangeSeq = excluded.ChangeSeq,
                LastError = excluded.LastError,
                SkippedAt = NULL
            RETURNING Attempts",
            new { itemId, changeSeq, error = error.Length > 500 ? error[..500] : error, now = nowUnix });
    }

    public void MarkFeedItemSkipped(string itemId, long nowUnix)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE CustomerFeedFailure SET SkippedAt = @now WHERE ItemId = @itemId", new { itemId, now = nowUnix });
    }

    public IReadOnlySet<string> GetFeedFailureIds()
    {
        using var conn = _factory.Open();
        return conn.Query<string>("SELECT ItemId FROM CustomerFeedFailure").ToHashSet(StringComparer.Ordinal);
    }

    public void ClearFeedFailure(string itemId)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM CustomerFeedFailure WHERE ItemId = @itemId", new { itemId });
    }

    // ── çekirdek ────────────────────────────────────────────────────────

    /// <summary>Gövdeyi <see cref="CustomerBusySet"/> kilidi altında koşar (U13, U17: küme →
    /// SQLite yazma kilidi). Küme yoksa (testler) hiçbir müşteri meşgul sayılmaz.</summary>
    private T Locked<T>(Func<Func<string, bool>, T> body)
        => _busy is null ? body(static _ => false) : _busy.RunLocked(body);

    private static void RekeyCore(IDbConnection conn, IDbTransaction tx, Row from, Row to, long nowUnix)
    {
        var state = to.ToState();
        // Kopya→asıl: eşit damga yankıdır (incomingWinsTie YOK — sunucunun birleştirmesiyle aynı).
        CustomerUnitMerge.Apply(state, Carried(from));
        MoveReferences(conn, tx, from.Id, to.Id, nowUnix);          // FK: önce çocuklar, sonra silme
        conn.Execute("DELETE FROM Customer WHERE Id = @id", new { id = from.Id }, tx);
        WriteFields(conn, tx, to.Id, state);
        AddAggregates(conn, tx, to.Id, new[] { from }, includeSeen: true);
        BumpSyncSeq(conn, tx, to.Id);
        RecordRedirect(conn, tx, from.Id, to.Id, nowUnix);
        conn.Execute(CustomerRepository.ScrubIfTombstonedSql, new { id = to.Id }, tx);
    }

    /// <summary>
    /// Kopyadan taşınan (kural 6, U3): damgalı birimler + beyan olamayan birimlerin damgasız
    /// değerleri (doldurma). Beyan olabilen damgasız birimler (takma ad, adres bloğu, telefon —
    /// istemcide FullName beyan değildir, C3) taşınmaz: kopyanın geçici kökenli olup olmadığı
    /// istemcide bilinemez (S19) ve sıradan kopyanınki sunucuda zaten birleşti (U3). Silinmiş
    /// kopyanın boşaltılmış kişisel birimleri hiç (S15). GUID olmayan Id'li satır hiç gönderilmedi
    /// ve geçici kökenli olamaz: hepsi (U3b).
    /// </summary>
    private static CustomerSyncState Carried(Row r)
    {
        var s = r.ToState();
        if (r.PurgedAt is not null) return s.WithoutShopperClaims(claims: null).WithoutScrubbedUnits();
        if (!Guid.TryParseExact(r.Id, "N", out _)) return s;
        return s.WithoutShopperClaims(claims: null);
    }

    /// <summary>U12: taşınan/dönüştürülen Id'nin yeni adresi — taşımayla aynı işlemde. Zincir
    /// kısaltılır (from'a yönlenenler to'ya); canlı Id hiçbir zaman yönlendirme kaynağı değildir.</summary>
    private static void RecordRedirect(IDbConnection conn, IDbTransaction tx, string fromId, string toId, long nowUnix)
    {
        var p = new { fromId, toId, now = nowUnix };
        conn.Execute(@"INSERT INTO CustomerRedirect (FromId, ToId, At) VALUES (@fromId, @toId, @now)
                       ON CONFLICT(FromId) DO UPDATE SET ToId = excluded.ToId, At = excluded.At", p, tx);
        conn.Execute("UPDATE CustomerRedirect SET ToId = @toId WHERE ToId = @fromId", p, tx);
        conn.Execute("DELETE FROM CustomerRedirect WHERE FromId = @toId", p, tx);
    }

    /// <summary>U13: ödeme akışı yüzünden ertelenen satırı yeniden gönderime koyar — gönderimin
    /// yanıtı yönlendirmeyi yeniden getirir (S7). "Meşgul" kararını veren AYNI işlemde, küme
    /// kilidi altında (inceleme M-2): ayrı bir işlem kararla arasında kaybolabilir (çökme) ya da
    /// o arada taşınmış bir satıra yazardı. SyncSeq hiçbir tetikleyici listesinde değil; kilit
    /// satırı bu açık ilerletmeyi engellemez.</summary>
    private static void RequeueInScope(SyncApplyScope scope, string id)
    {
        CustomerSyncSeq.Bump(scope.Connection, scope.Transaction, id);
        scope.Commit();
    }

    /// <summary>Kimlik sahipleri: aynı platform (harf duyarsız) + aynı kimlik anahtarı.
    /// Anahtarı eksik kalmış satır (U6) tekil indeksle çakışmasın diye birebir ad da aranır.</summary>
    private static List<Row> ReadHolders(IDbConnection conn, IDbTransaction tx, string platform, string username, string excludeId)
    {
        var byKey = conn.Query<Row>(
            $"SELECT {Columns} FROM Customer WHERE Platform = @platform COLLATE NOCASE AND IdentityKey = @key AND Id <> @excludeId",
            new { platform, key = CustomerIdentity.KeyOrNull(username), excludeId }, tx);
        var exact = conn.Query<Row>(
            $"SELECT {Columns} FROM Customer WHERE Platform = @platform AND Username = @username AND Id <> @excludeId",
            new { platform, username, excludeId }, tx);
        return byKey.Concat(exact).GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    private static Row? ReadById(IDbConnection conn, IDbTransaction tx, string id)
        => conn.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM Customer WHERE Id = @id", new { id }, tx);

    /// <summary>Hedef Id yerelde daha önce taşınmışsa güncel Id'si (U12).</summary>
    private static Row? ReadResolved(IDbConnection conn, IDbTransaction tx, string id)
        => conn.QuerySingleOrDefault<Row>(
            $"SELECT {Columns} FROM Customer WHERE Id = {CustomerIdSql.Resolve("@id")}", new { id }, tx);

    /// <summary>
    /// <see cref="CustomerReferenceTables"/>'ın bütün satırlarını yeni Id'ye geçirir (yeni bir tablo
    /// Customer'a bağlanırsa O LİSTEYE eklenir; şema koruma testi unutulanı yakalar). GroupId bir
    /// başvuru değildir; CustomerRedirect eski Id'leri tutar, RecordRedirect yazar.
    /// Etiket/kargo yeniden gönderilmez: sunucu kopyanın siparişlerini asıl kayda kendisi taşır
    /// ve kopya Id'sini her uçta çözer (S18).
    /// </summary>
    private static void MoveReferences(IDbConnection conn, IDbTransaction tx, string fromId, string toId, long nowUnix)
    {
        var p = new { fromId, toId, now = nowUnix };
        // U8: UX_PaymentJob_Scope(CustomerId, ScopeKey). Hedefte aynı kapsamda iş varsa
        // kopyanınki miras kapsamına alınır: anahtarı (para hareketi) varsa açık kalır ve
        // PaymentRequestService'in miras uzlaştırması kesinleştirir/geri alır; anahtarsız
        // iş (hiç hareket yok) kapatılır. Ödeme akışı süren müşteriye buraya hiç gelinmez (U13).
        //
        // Yeni ad 'legacy:{ApplyKey ya da Id}:{Id}' — iş Id'si adı KENDİLİĞİNDEN benzersiz kılar
        // (inceleme I-1). Miras devralması (PaymentJobRepository.AdoptLegacyResult) kapalı
        // 'legacy:K' işini yerinde bırakıp K'yi kapsam işine taşır; o iş 'legacy:K' adını alsaydı
        // kopyanın kendi kapalı işine çarpar, taşıma her turda düşer ve kimlik kalıcı bölünürdü.
        // Adı hiçbir yol ayrıştırmaz: miras taraması LIKE 'legacy:%', yeniden oynatma ApplyKey
        // kolonunu okur; sunucuya SaleScope olarak giderse 72 karakter (sunucu sınırı 128).
        conn.Execute(@"
            UPDATE PaymentJob
               SET ScopeKey  = 'legacy:' || COALESCE(ApplyKey, Id) || ':' || Id,
                   ClosedAt  = CASE WHEN ApplyKey IS NULL THEN COALESCE(ClosedAt, @now) ELSE ClosedAt END,
                   UpdatedAt = @now
             WHERE CustomerId = @fromId
               AND EXISTS (SELECT 1 FROM PaymentJob t
                           WHERE t.CustomerId = @toId AND t.ScopeKey = PaymentJob.ScopeKey)", p, tx);
        // Tablo adları sabit listeden (kullanıcı girdisi değil).
        foreach (var table in CustomerReferenceTables)
            conn.Execute($"UPDATE {table} SET CustomerId = @toId WHERE CustomerId = @fromId", p, tx);
    }

    /// <summary>Yerel toplamlar (taşınmayan alanlar): etiket sayısı ve ciro toplanır,
    /// ilk/son görülme genişler, avatar boşsa alınır — hedef SİLİNMEMİŞSE (inceleme M-1, KVKK):
    /// avatar kişisel veridir, silinmiş kayda geri gelmez; toplamlar kişisel veri değildir.</summary>
    private static void AddAggregates(IDbConnection conn, IDbTransaction tx, string toId,
        IReadOnlyCollection<Row> sources, bool includeSeen)
        => conn.Execute(@"
            UPDATE Customer SET
                TotalLabelsPrinted = TotalLabelsPrinted + @labels,
                TotalAmount        = TotalAmount + @amount,
                FirstSeenAt        = CASE WHEN @includeSeen = 1 THEN MIN(FirstSeenAt, @first) ELSE FirstSeenAt END,
                LastSeenAt         = CASE WHEN @includeSeen = 1 THEN MAX(LastSeenAt, @last) ELSE LastSeenAt END,
                AvatarUrl          = CASE WHEN PurgedAt IS NULL THEN COALESCE(AvatarUrl, @avatar) ELSE AvatarUrl END
            WHERE Id = @toId",
            new
            {
                toId,
                labels = sources.Sum(s => s.TotalLabelsPrinted),
                amount = sources.Sum(s => s.TotalAmount),
                includeSeen = includeSeen ? 1 : 0,
                first = sources.Min(s => s.FirstSeenAt),
                last = sources.Max(s => s.LastSeenAt),
                avatar = sources.Select(s => s.AvatarUrl).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
            }, tx);

    /// <summary>Kilit SyncSeq tetikleyicisini susturduğu için taşımadan ve dönüştürmeden sonra
    /// açıkça: asıl kayda taşınan birimler (kopyanın son gönderimden sonraki düzenlemesi dahil)
    /// gönderilsin.</summary>
    private static void BumpSyncSeq(IDbConnection conn, IDbTransaction tx, string id)
        => CustomerSyncSeq.Bump(conn, tx, id);   // C1 incelemesi: silinmeye dayanıklı sayaç (MAX+1 değil)

    /// <summary>Sunucu satırını ekler: boş satıra <see cref="CustomerUnitMerge"/> uygulanmış
    /// hâli (damgalı birim damgasıyla, damgasız birim damgasız; takma ad gerçek ad sayılmaz).
    /// Ardından KVKK mezar taşı engeli (R11-D01 — satır açan her yol) ve canlı Id'nin
    /// yönlendirme kaydının silinmesi (U12).</summary>
    private static void InsertFromServer(IDbConnection conn, IDbTransaction tx, ServerCustomer c,
        long firstSeenAt, long lastSeenAt)
    {
        var s = new CustomerSyncState { Username = c.Username };
        CustomerUnitMerge.Apply(s, c.Fields);
        var p = new DynamicParameters(FieldParams(c.Id, s));
        p.Add("Platform", c.Platform);
        p.Add("Username", c.Username);
        // U6: Username'i yazan ifade kimlik anahtarını da yazar (boş ad → NULL).
        p.Add("IdentityKey", CustomerIdentity.KeyOrNull(c.Username));
        p.Add("FirstSeenAt", firstSeenAt);
        p.Add("LastSeenAt", lastSeenAt);
        conn.Execute(@"
            INSERT INTO Customer
              (Id, Platform, Username, IdentityKey, AvatarUrl, FirstSeenAt, LastSeenAt,
               TotalLabelsPrinted, TotalAmount,
               FullName, FullNameChangedAt, DisplayName, DisplayNameChangedAt,
               GroupId, GroupIdChangedAt,
               Address, City, District, AddressChangedAt,
               RecipientPaysActive, RecipientPaysChangedAt,
               Phone, PhoneChangedAt, Email, EmailChangedAt, Tckn, TcknChangedAt,
               WhatsAppConsent, WhatsAppConsentChangedAt, SmsConsent, SmsConsentChangedAt,
               IsBlacklisted, BlacklistReason, BlacklistedAt, BlacklistChangedAt,
               Notes, NotesChangedAt)
            VALUES
              (@Id, @Platform, @Username, @IdentityKey, NULL, @FirstSeenAt, @LastSeenAt,
               0, 0,
               @FullName, @FullNameChangedAt, @DisplayName, @DisplayNameChangedAt,
               @GroupId, @GroupIdChangedAt,
               @Address, @City, @District, @AddressChangedAt,
               @RecipientPaysActive, @RecipientPaysChangedAt,
               @Phone, @PhoneChangedAt, @Email, @EmailChangedAt, @Tckn, @TcknChangedAt,
               @WhatsAppConsent, @WhatsAppConsentChangedAt, @SmsConsent, @SmsConsentChangedAt,
               @IsBlacklisted, @BlacklistReason, @BlacklistedAt, @BlacklistChangedAt,
               @Notes, @NotesChangedAt)", p, tx);
        conn.Execute(CustomerRepository.ScrubIfTombstonedSql, new { id = c.Id }, tx);
        // U12: sunucu daha önce yerelde taşınmış bir Id'yi asıl kayıt olarak indirdi — Id yeniden
        // canlı; canlı Id yönlendirme kaynağı olamaz (yoksa çözüm onu başka satıra götürürdü).
        conn.Execute("DELETE FROM CustomerRedirect WHERE FromId = @id", new { id = c.Id }, tx);
    }

    /// <summary>Birimleri damgalarıyla AÇIKÇA yazar. Kilit altında olduğu için hiçbir
    /// damga tetikleyicisi çalışmaz; silinmiş satıra SQL düzeyinde de yazılmaz.</summary>
    private static void WriteFields(IDbConnection conn, IDbTransaction tx, string id, CustomerSyncState s)
        => conn.Execute(@"
            UPDATE Customer SET
                FullName = @FullName, FullNameChangedAt = @FullNameChangedAt,
                DisplayName = @DisplayName, DisplayNameChangedAt = @DisplayNameChangedAt,
                GroupId = @GroupId, GroupIdChangedAt = @GroupIdChangedAt,
                Address = @Address, City = @City, District = @District, AddressChangedAt = @AddressChangedAt,
                RecipientPaysActive = @RecipientPaysActive, RecipientPaysChangedAt = @RecipientPaysChangedAt,
                Phone = @Phone, PhoneChangedAt = @PhoneChangedAt,
                Email = @Email, EmailChangedAt = @EmailChangedAt,
                Tckn = @Tckn, TcknChangedAt = @TcknChangedAt,
                WhatsAppConsent = @WhatsAppConsent, WhatsAppConsentChangedAt = @WhatsAppConsentChangedAt,
                SmsConsent = @SmsConsent, SmsConsentChangedAt = @SmsConsentChangedAt,
                IsBlacklisted = @IsBlacklisted, BlacklistReason = @BlacklistReason,
                BlacklistedAt = @BlacklistedAt, BlacklistChangedAt = @BlacklistChangedAt,
                Notes = @Notes, NotesChangedAt = @NotesChangedAt
            WHERE Id = @Id AND PurgedAt IS NULL", FieldParams(id, s), tx);

    private static object FieldParams(string id, CustomerSyncState s) => new
    {
        Id = id,
        s.FullName, s.FullNameChangedAt, s.DisplayName, s.DisplayNameChangedAt,
        s.GroupId, s.GroupIdChangedAt,
        s.Address, s.City, s.District, s.AddressChangedAt,
        RecipientPaysActive = s.RecipientPaysActive ? 1 : 0, s.RecipientPaysChangedAt,
        s.Phone, s.PhoneChangedAt, s.Email, s.EmailChangedAt, s.Tckn, s.TcknChangedAt,
        WhatsAppConsent = s.WhatsAppConsent ? 1 : 0, s.WhatsAppConsentChangedAt,
        SmsConsent = s.SmsConsent ? 1 : 0, s.SmsConsentChangedAt,
        IsBlacklisted = s.IsBlacklisted ? 1 : 0, s.BlacklistReason, s.BlacklistedAt, s.BlacklistChangedAt,
        s.Notes, s.NotesChangedAt,
    };

    /// <summary>Dapper ara sınıfı: SQLite INTEGER → long (CustomerRepository.Row deseni).</summary>
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Username { get; set; } = "";
        public long FirstSeenAt { get; set; }
        public long LastSeenAt { get; set; }
        public long SyncSeq { get; set; }
        public long? PurgedAt { get; set; }
        public long TotalLabelsPrinted { get; set; }
        public decimal TotalAmount { get; set; }
        public string? AvatarUrl { get; set; }
        public string? FullName { get; set; }
        public long? FullNameChangedAt { get; set; }
        public string? DisplayName { get; set; }
        public long? DisplayNameChangedAt { get; set; }
        public string? GroupId { get; set; }
        public long? GroupIdChangedAt { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? District { get; set; }
        public long? AddressChangedAt { get; set; }
        public long RecipientPaysActive { get; set; }
        public long? RecipientPaysChangedAt { get; set; }
        public string? Phone { get; set; }
        public long? PhoneChangedAt { get; set; }
        public string? Email { get; set; }
        public long? EmailChangedAt { get; set; }
        public string? Tckn { get; set; }
        public long? TcknChangedAt { get; set; }
        public long WhatsAppConsent { get; set; }
        public long? WhatsAppConsentChangedAt { get; set; }
        public long SmsConsent { get; set; }
        public long? SmsConsentChangedAt { get; set; }
        public long IsBlacklisted { get; set; }
        public string? BlacklistReason { get; set; }
        public long? BlacklistedAt { get; set; }
        public long? BlacklistChangedAt { get; set; }
        public string? Notes { get; set; }
        public long? NotesChangedAt { get; set; }

        public CustomerSyncState ToState() => new()
        {
            Username = Username, PurgedAt = PurgedAt,
            FullName = FullName, FullNameChangedAt = FullNameChangedAt,
            DisplayName = DisplayName, DisplayNameChangedAt = DisplayNameChangedAt,
            GroupId = GroupId, GroupIdChangedAt = GroupIdChangedAt,
            Address = Address, City = City, District = District, AddressChangedAt = AddressChangedAt,
            RecipientPaysActive = RecipientPaysActive == 1, RecipientPaysChangedAt = RecipientPaysChangedAt,
            Phone = Phone, PhoneChangedAt = PhoneChangedAt,
            Email = Email, EmailChangedAt = EmailChangedAt,
            Tckn = Tckn, TcknChangedAt = TcknChangedAt,
            WhatsAppConsent = WhatsAppConsent == 1, WhatsAppConsentChangedAt = WhatsAppConsentChangedAt,
            SmsConsent = SmsConsent == 1, SmsConsentChangedAt = SmsConsentChangedAt,
            IsBlacklisted = IsBlacklisted == 1, BlacklistReason = BlacklistReason,
            BlacklistedAt = BlacklistedAt, BlacklistChangedAt = BlacklistChangedAt,
            Notes = Notes, NotesChangedAt = NotesChangedAt,
        };
    }
}
