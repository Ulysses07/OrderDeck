using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sunucunun müşteri senkron kurallarının test ikizi (Bölüm C, C11) — master'daki
/// <c>LicensesWpfCustomersSyncController</c>, <c>LicensesWpfCustomersPullController</c>,
/// <c>CustomerFieldMerge</c> ve <c>CustomerIdentityMergeJob</c>'un aynası. Gönderim (<c>ApplyBatchAsync</c>
/// ile aynı sıra): Id'ye göre tekilleştirme (son giriş kazanır) ve bozuk platform/kullanıcı adı elemesi;
/// önce DEVRALMA (S8 — yeni Id'nin kimliğinin asıl kaydı geçiciyse yayıncının satırı asıl kayıt olur,
/// geçici satırın yalnız DAMGALI birimleri taşınır, beyanı boşaltılır, kopyaya döner, yönlendirme
/// dönmez); sonra payload sırasıyla: bilinen kopya (S7 — en çok üç adımda asıl kayda çözülür; geçici
/// kökenliye hiçbir şey yazılmaz; yönlendirme her durumda), silinmiş satıra yazılmaz, kendi Id'si
/// (birim birleştirme + yalnız damgalı telefonla benimseme, S17), yeni kopya (S6), yeni asıl kayıt.
///
/// <para><b>Bağımsız kâhin (C11 incelemesi M-6):</b> birim birleştirme sunucunun kendi kuralının
/// kopyası (<see cref="ServerCustomerFieldMerge"/>: kırpma, sunucunun FullName doldurması, eşit damgada
/// ilk gelen kalır), istemcinin <c>CustomerUnitMerge</c>'ü DEĞİL. Tel biçimi de üretimdeki kayıtların
/// kopyası (<see cref="SyncItem"/>, <see cref="ChangeItem"/>): istemcinin DTO'larıyla değil, JSON'un
/// kendisiyle konuşur — alan adı kayması da yakalanır.</para>
///
/// <para><b>Biçim (M-3):</b> üretimdeki <c>SyncItem.Format</c>'ın varsayılanı 1'dir (eski istemci alanı
/// göndermez); istemcinin <c>WpfCustomerSyncItem</c>'ında 2. Sahte sunucu üretimin kaydıyla okur: istemci
/// alanı yazmayı bırakırsa öğe biçim 1 sayılır. Biçim 1 (<c>ApplyLegacy</c>) taklit edilmez — bu istemci
/// yalnız biçim 2 gönderir; gelirse <see cref="Faults"/>'a yazılır ve 500 döner.</para>
///
/// <para><b>Hatalar (I-2):</b> istek işlenirken fırlayan her istisna, beklenmeyen uç ve biçim 1 öğe
/// <see cref="Faults"/>'a yazılır ve 500/404 döner — istemci gönderim hatasını yalnız günlüğe yazdığı
/// için tur yine "yetişti" görünürdü; testler her turdan sonra listenin boş olduğunu doğrular.</para>
///
/// <para>Akış: <c>ChangeSeq</c> = rowversion; bir gönderimin bütün yazımları kayıt anında numaralanır,
/// sırası <see cref="Order"/> ile seçilir (EF Core önce değişenleri, sonra eklenenleri yazar; istemci
/// sıraya bağlı olmamalı — U4). Ufuk = sıradaki rowversion (MIN_ACTIVE_ROWVERSION); imleç eksi ya da
/// ufkun üstündeyse sayfa baştan + <c>CursorReset</c> (S11); boş sayfada <c>nextAfterSeq = afterSeq</c>;
/// kopya satırı yalnız yönlendirme (S12); TCKN çözülemezse null (S13, <see cref="TcknUnreadable"/>).</para>
/// </summary>
internal sealed class FakeCustomerServer : HttpMessageHandler
{
    /// <summary>Bir yazım grubunun rowversion sırası.</summary>
    internal enum CommitOrder
    {
        /// <summary>Önce değişen satırlar, sonra eklenenler — her grup yazım sırasıyla (EF Core gibi).</summary>
        ModifiedFirst,
        /// <summary>Önce eklenenler, sonra değişenler.</summary>
        InsertedFirst,
        /// <summary>Yazım sırasının tersi.</summary>
        Reversed,
    }

    /// <summary>Sunucudaki <c>WpfCustomerProjection</c>'ın senkronla ilgili kısmı. TCKN düz tutulur
    /// (üretimde şifreli <c>TcknProtected</c>).</summary>
    internal sealed class Row
    {
        public Guid Id;
        public string Platform = "";
        public string Username = "";
        public Guid? MergedIntoId;
        public bool CreatedByShopper;
        public DateTimeOffset? PurgedAt;
        public DateTimeOffset UpdatedAt;
        public long ChangeSeq;

        public string? FullName; public DateTimeOffset? FullNameChangedAt;
        public string? DisplayName; public DateTimeOffset? DisplayNameChangedAt;
        public string? GroupId; public DateTimeOffset? GroupIdChangedAt;
        public string? Address; public string? City; public string? District; public DateTimeOffset? AddressChangedAt;
        public bool RecipientPaysActive; public DateTimeOffset? RecipientPaysChangedAt;
        public string? Phone; public DateTimeOffset? PhoneChangedAt;
        public string? Email; public DateTimeOffset? EmailChangedAt;
        public string? Tckn; public DateTimeOffset? TcknChangedAt;
        public bool WhatsAppConsent; public DateTimeOffset? WhatsAppConsentChangedAt;
        public bool SmsConsent; public DateTimeOffset? SmsConsentChangedAt;
        public bool IsBlacklisted; public string? BlacklistReason; public DateTimeOffset? BlacklistedAt;
        public DateTimeOffset? BlacklistChangedAt;
        public string? Notes; public DateTimeOffset? NotesChangedAt;

        /// <summary>Sunucudaki <c>WpfCustomerProjection.IdentityKeyOf</c>: kırp, küçült, İ→i (ı'ya dokunmaz).</summary>
        public string IdentityKey => Username.Trim().ToLowerInvariant().Replace('İ', 'i');

        /// <summary>Sunucu <c>ScrubPersonal</c>: kişisel birimlerin DEĞERLERİ boşalır, damgalar kalır.</summary>
        public void ScrubPersonal()
        {
            FullName = null; DisplayName = null;
            Phone = null; Email = null; Tckn = null;
            Address = null; City = null; District = null;
            WhatsAppConsent = false; SmsConsent = false;
        }

        public Row Clone() => (Row)MemberwiseClone();
    }

    /// <summary>Kopya zincirinin çözüm sınırı (sunucu <c>CustomerIdResolver.MaxHops</c>).</summary>
    private const int MaxHops = 3;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Row> _rows = new();
    private readonly List<IntakeFormSubmissionDto> _forms = new();
    private readonly ConcurrentQueue<string> _faults = new();
    private long _version;
    private Action? _beforeNextChanges;

    public Guid LicenseId { get; } = Guid.NewGuid();
    public string Lisans { get; } = $"lisans-{Guid.NewGuid():N}";

    /// <summary>Yazım gruplarının rowversion sırası (U4 testleri üçünü de koşar).</summary>
    public CommitOrder Order { get; set; } = CommitOrder.ModifiedFirst;

    /// <summary>false: yeni Id kimlik araması yapılmadan asıl kayıt açar — YALNIZ B1/PR-1 öncesi dönemin
    /// çift asıl kayıtlarını kurmak için (sonra <see cref="MergeInto"/> birleştirme işini taklit eder).</summary>
    public bool IdentityLookup { get; set; } = true;

    /// <summary>S13: TCKN anahtarı kayıp — akış TCKN'yi null verir, damga değişmez.</summary>
    public bool TcknUnreadable { get; set; }

    /// <summary>Asıl kayda hiçbir şey yazılmayan geçici kökenli kopya gönderimi sayısı (S7).</summary>
    public int UntrustedAliasPushes { get; private set; }

    /// <summary>Yayıncının satırına devredilen geçici kayıt sayısı (S8).</summary>
    public int TakenOver { get; private set; }

    /// <summary>Gönderim yanıtlarında dönen yönlendirme sayısı.</summary>
    public int RedirectsReturned { get; private set; }

    /// <summary>İşlenemeyen istekler (I-2) — testler her turdan sonra boş olduğunu doğrular.</summary>
    public IReadOnlyCollection<string> Faults => _faults.ToArray();

    /// <summary>Bir sonraki akış isteğinde, yanıt hazırlanmadan ÖNCE bir kez koşar — bir bilgisayarın
    /// gönderimi ile akışı arasına giren yerel yazımı (aynı anda okunan yorum) taklit eder.</summary>
    public Action? BeforeNextChanges
    {
        set => Interlocked.Exchange(ref _beforeNextChanges, value);
    }

    public Row Get(Guid id) { lock (_gate) return _rows[id].Clone(); }

    /// <summary>Kimliğin asıl kaydı (B1: kimlik başına tek).</summary>
    public Row? CanonicalOf(string platform, string username)
    {
        lock (_gate) return CanonicalFor(platform, username)?.Clone();
    }

    /// <summary>Shopper kaydı (<c>ShopperAuthController</c> 8a): aday yoksa GEÇİCİ asıl kayıt açar;
    /// beyan = FullName/Phone/Address, damgasız.</summary>
    public Guid RegisterShopper(string platform, string username, string fullName, string phone, string address)
    {
        lock (_gate)
        {
            if (CanonicalFor(platform, username) is not null)
                throw new InvalidOperationException("aday var — sunucu geçici satır açmaz");
            var batch = new Batch();
            Add(batch, new Row
            {
                Id = Guid.NewGuid(), Platform = platform.ToLowerInvariant(), Username = username,
                CreatedByShopper = true, UpdatedAt = DateTimeOffset.UtcNow,
                FullName = fullName, Phone = phone, Address = address,
            });
            Commit(batch);
            return batch.Touched[0].Row.Id;
        }
    }

    /// <summary>KVKK silmesi (<c>WpfCustomerProjection.MarkPurged</c>): kişisel birimlerin değerleri
    /// boşalır, damgalar/not/kara liste kalır, PurgedAt ilk silme anını korur.</summary>
    public void Purge(Guid id)
    {
        lock (_gate)
        {
            var r = _rows[id];
            var now = DateTimeOffset.UtcNow;
            r.ScrubPersonal();
            r.PurgedAt ??= now;
            r.UpdatedAt = now;
            var batch = new Batch();
            batch.Touch(r);
            Commit(batch);
        }
    }

    /// <summary>
    /// Sunucunun kimlik birleştirme işi (<c>CustomerIdentityMergeJob</c>, bir grup için): kopyanın
    /// alanları (geçiciyse yalnız damgalı birimler, silinmişse boşaltılmış birimler hariç) asıl kayda
    /// birim kurallarıyla yazılır; kopya boşaltılır ve asıl kayda yönlenir, ona yönlenmiş eski kopyalar
    /// da; kopya silinmişse (geçici değilse) asıl kayıt da silinir. Asıl kayıt her durumda yazılır
    /// (rowversion ilerler). HİÇBİR gönderim olmadan kopya doğar — bilgisayarlar onu akıştan öğrenir.
    /// </summary>
    public void MergeInto(Guid loser, Guid winner)
    {
        lock (_gate)
        {
            var copy = _rows[loser];
            var canonical = _rows[winner];
            if (copy.MergedIntoId is not null || canonical.MergedIntoId is not null)
                throw new InvalidOperationException("birleştirme iki asıl kayıt ister");
            var batch = new Batch();
            var now = DateTimeOffset.UtcNow;
            var becomesPurged = copy.PurgedAt is not null && !copy.CreatedByShopper && canonical.PurgedAt is null;

            var fields = ServerSyncFields.From(copy);
            if (copy.CreatedByShopper) fields = fields.StampedOnly();
            if (copy.PurgedAt is not null) fields = fields.WithoutScrubbedUnits();
            ServerCustomerFieldMerge.Apply(canonical, fields);
            batch.Touch(canonical);                                           // her durumda yazılır
            copy.ScrubPersonal();
            copy.MergedIntoId = canonical.Id;                                 // UpdatedAt korunur
            batch.Touch(copy);
            foreach (var chained in _rows.Values.Where(r => r.MergedIntoId == copy.Id).ToList())
            {
                chained.MergedIntoId = canonical.Id;
                batch.Touch(chained);
            }
            if (copy.PurgedAt is { } purgedAt && !copy.CreatedByShopper)
            {
                canonical.ScrubPersonal();
                canonical.PurgedAt = canonical.PurgedAt is { } p && p < purgedAt ? p : purgedAt;
            }
            if (becomesPurged) canonical.UpdatedAt = now;
            Commit(batch);
        }
    }

    /// <summary>Bir form gönderimi (<c>/api/v1/me/form-submissions</c>; istemcinin DTO'suyla).</summary>
    public void AddForm(IntakeFormSubmissionDto form)
    {
        lock (_gate) _forms.Add(form);
    }

    /// <summary>Yedekten geri dönüş provası: anlık görüntüden sonraki yazımlar ve rowversion sayacı
    /// kaybolur (.bak geri yüklemesi sayacı geri sarar).</summary>
    public (long Version, List<Row> Rows) Snapshot()
    {
        lock (_gate) return (_version, _rows.Values.Select(r => r.Clone()).ToList());
    }

    public void Restore((long Version, List<Row> Rows) snapshot)
    {
        lock (_gate)
        {
            _rows.Clear();
            foreach (var r in snapshot.Rows) _rows[r.Id] = r.Clone();
            _version = snapshot.Version;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        try
        {
            if (path == "/api/v1/me/licenses" && req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json(200, $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}}]");

            if (path == "/api/v1/me/form-submissions" && req.Method == HttpMethod.Get)
                lock (_gate) return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(Forms(req.RequestUri.Query), Web));

            // Lisans sahipliği (uçlar başka lisansa 404 döner).
            var prefix = $"/api/v1/licenses/{LicenseId}/wpf-customers/";
            if (path.Equals(prefix + "sync", StringComparison.OrdinalIgnoreCase) && req.Method == HttpMethod.Post)
            {
                var body = JsonSerializer.Deserialize<SyncRequest>(await req.Content!.ReadAsStringAsync(ct), Web);
                if (body?.Customers is { Count: > 500 })                       // parti boyutu istemci hatası (S2)
                    return FakeHttpMessageHandler.Problem(400, "batch-too-large", "Max 500 customers per batch");
                lock (_gate) return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(Sync(body?.Customers), Web));
            }
            if (path.Equals(prefix + "changes", StringComparison.OrdinalIgnoreCase) && req.Method == HttpMethod.Get)
            {
                Interlocked.Exchange(ref _beforeNextChanges, null)?.Invoke();
                var q = req.RequestUri.Query;
                var after = long.Parse(Regex.Match(q, @"afterSeq=(-?\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
                var take = int.Parse(Regex.Match(q, @"take=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
                lock (_gate) return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(Changes(after, take), Web));
            }

            _faults.Enqueue($"beklenmeyen istek: {req.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        catch (Exception ex)
        {
            _faults.Enqueue($"{req.Method} {path}: {ex.GetType().Name}: {ex.Message}");
            return FakeHttpMessageHandler.Problem(500, "fake-server-fault", ex.Message);
        }
    }

    // ── tel biçimi: üretimdeki kayıtların kopyası (istemcinin DTO'ları değil) ─────────────

    /// <summary><c>LicensesWpfCustomersSyncController.SyncItem</c> — <see cref="Format"/> varsayılanı 1 (M-3).</summary>
    internal sealed record SyncItem(
        Guid Id, string? Platform, string? Username, string? FullName, string? Phone, string? Address,
        DateTimeOffset UpdatedAt,
        int Format = 1,
        DateTimeOffset? FullNameChangedAt = null,
        string? DisplayName = null, DateTimeOffset? DisplayNameChangedAt = null,
        string? GroupId = null, DateTimeOffset? GroupIdChangedAt = null,
        string? City = null, string? District = null, DateTimeOffset? AddressChangedAt = null,
        bool RecipientPaysActive = false, DateTimeOffset? RecipientPaysChangedAt = null,
        DateTimeOffset? PhoneChangedAt = null,
        string? Email = null, DateTimeOffset? EmailChangedAt = null,
        string? Tckn = null, DateTimeOffset? TcknChangedAt = null,
        bool WhatsAppConsent = false, DateTimeOffset? WhatsAppConsentChangedAt = null,
        bool SmsConsent = false, DateTimeOffset? SmsConsentChangedAt = null,
        bool IsBlacklisted = false, string? BlacklistReason = null, DateTimeOffset? BlacklistedAt = null,
        DateTimeOffset? BlacklistChangedAt = null,
        string? Notes = null, DateTimeOffset? NotesChangedAt = null);

    internal sealed record SyncRequest(List<SyncItem>? Customers);
    internal sealed record SyncRedirect(Guid Id, Guid CanonicalId);
    internal sealed record SyncResponse(int Synced, int RetroactiveMatches, List<SyncRedirect> Redirects);

    /// <summary><c>LicensesWpfCustomersPullController.WpfCustomerChangeItem</c>.</summary>
    internal sealed record ChangeItem(
        Guid Id, string Platform, string Username, Guid? MergedIntoId, DateTimeOffset? PurgedAt,
        string? FullName, DateTimeOffset? FullNameChangedAt,
        string? DisplayName, DateTimeOffset? DisplayNameChangedAt,
        string? GroupId, DateTimeOffset? GroupIdChangedAt,
        string? Address, string? City, string? District, DateTimeOffset? AddressChangedAt,
        bool RecipientPaysActive, DateTimeOffset? RecipientPaysChangedAt,
        string? Phone, DateTimeOffset? PhoneChangedAt,
        string? Email, DateTimeOffset? EmailChangedAt,
        string? Tckn, DateTimeOffset? TcknChangedAt,
        bool WhatsAppConsent, DateTimeOffset? WhatsAppConsentChangedAt,
        bool SmsConsent, DateTimeOffset? SmsConsentChangedAt,
        bool IsBlacklisted, string? BlacklistReason, DateTimeOffset? BlacklistedAt, DateTimeOffset? BlacklistChangedAt,
        string? Notes, DateTimeOffset? NotesChangedAt,
        long ChangeSeq,
        bool CreatedByShopper = false)
    {
        /// <summary>Kopya satırı: YALNIZ yönlendirme (S12).</summary>
        public static ChangeItem Redirect(Guid id, string platform, string username, Guid mergedIntoId, long changeSeq) => new(
            id, platform, username, mergedIntoId, null,
            null, null, null, null, null, null,
            null, null, null, null, false, null,
            null, null, null, null, null, null,
            false, null, false, null,
            false, null, null, null, null, null,
            changeSeq, CreatedByShopper: false);
    }

    internal sealed record ChangesPage(List<ChangeItem> Items, long NextAfterSeq, bool CursorReset = false);

    // ── gönderim (LicensesWpfCustomersSyncController.Sync + ApplyBatchAsync) ────────────

    /// <summary>Bir yazım grubu: kayıt anında (<see cref="Commit"/>) numaralanır.</summary>
    private sealed class Batch
    {
        public readonly List<(Row Row, bool Added)> Touched = new();

        public void Touch(Row r)
        {
            if (!Touched.Any(t => ReferenceEquals(t.Row, r))) Touched.Add((r, false));
        }
    }

    private SyncResponse Sync(IReadOnlyList<SyncItem?>? payload)
    {
        if (payload is null || payload.Count == 0) return new SyncResponse(0, 0, new List<SyncRedirect>());

        // Null öğe ve bozuk platform/kullanıcı adı yazılmadan SAYILIR; Id'ye göre tekilleştirme (son kazanır).
        var nonNull = payload.Where(i => i is not null).Select(i => i!).ToList();
        var deduped = nonNull.GroupBy(i => i.Id).Select(g => g.Last()).ToList();
        var items = deduped.Where(IsAcceptable).ToList();
        var skipped = payload.Count - nonNull.Count + (deduped.Count - items.Count);
        // Biçim 1 taklit edilmez — hiçbir şey yazılmadan reddedilir (yarım uygulanmış parti kalmasın).
        var legacy = items.Count(i => i.Format < 2);
        if (legacy > 0)
            throw new InvalidOperationException($"{legacy} öğe biçim 1 geldi (Format alanı yok ya da < 2) — istemci yalnız biçim 2 göndermeli");

        var batch = new Batch();
        var existing = items.Where(i => _rows.ContainsKey(i.Id)).ToDictionary(i => i.Id, i => _rows[i.Id]);
        var newItems = items.Where(i => !existing.ContainsKey(i.Id)).ToList();

        // Yeni Id'lerin kimlik araması: asıl kayıt (kopya değil, boş olmayan anahtar). Seçim kuralı
        // B1 öncesinden (yayıncı satırı geçiciden önce, sonra en eski UpdatedAt, sonra Id). Silinmiş asıl
        // kayıt da bulunur (sunucu süzmez).
        var newKeys = newItems.Select(KeyOf).ToHashSet();
        var canonicalByKey = !IdentityLookup
            ? new Dictionary<(string, string), Row>()
            : _rows.Values
                .Where(r => r.MergedIntoId is null && r.IdentityKey != "" && newKeys.Contains((r.Platform, r.IdentityKey)))
                .GroupBy(r => (r.Platform, r.IdentityKey))
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(r => r.CreatedByShopper).ThenBy(r => r.UpdatedAt).ThenBy(r => r.Id)
                    .First());

        // Bilinen kopyaların asıl kayıtları — partinin BAŞINDAKİ duruma göre, en çok üç adımda
        // (CustomerIdResolver). Sınırın ötesindeki uç hâlâ kopyaysa hedef yok.
        var canonicalOfAlias = existing.Values.Where(r => r.MergedIntoId is not null)
            .ToDictionary(r => r.Id, r => ResolveBounded(r.Id));
        var targets = canonicalOfAlias.Values.Distinct()
            .Where(id => _rows.TryGetValue(id, out var t) && t.MergedIntoId is null)
            .ToDictionary(id => id, id => _rows[id]);

        var redirects = new List<SyncRedirect>();
        var synced = 0;

        // DEVRALMA (S8) — partinin geri kalanından ÖNCE (sonuç payload sırasına bağlı kalmasın).
        var successorOf = new Dictionary<Guid, Guid>();
        var takeoverItems = new HashSet<Guid>();
        foreach (var item in newItems)
        {
            var key = KeyOf(item);
            if (!canonicalByKey.TryGetValue(key, out var provisional) || !provisional.CreatedByShopper) continue;

            var created = AddCanonical(batch, item);
            var carried = ServerSyncFields.From(provisional).StampedOnly();
            if (provisional.PurgedAt is not null) carried = carried.WithoutScrubbedUnits();
            ServerCustomerFieldMerge.Apply(created, carried);
            provisional.ScrubPersonal();                                       // köken bayrağı KALIR
            provisional.MergedIntoId = created.Id;
            batch.Touch(provisional);
            foreach (var older in _rows.Values.Where(r => r.MergedIntoId == provisional.Id && r.Id != provisional.Id).ToList())
            {
                older.MergedIntoId = created.Id;                               // kopyanın kopyası kalmaz
                batch.Touch(older);
            }

            canonicalByKey[key] = created;
            targets[created.Id] = created;
            successorOf[provisional.Id] = created.Id;
            takeoverItems.Add(item.Id);
            TakenOver++;
        }

        foreach (var item in items)
        {
            if (takeoverItems.Contains(item.Id))
            {
                synced++;
                continue;
            }
            var key = KeyOf(item);

            if (existing.TryGetValue(item.Id, out var current))
            {
                if (current.MergedIntoId is { } immediateTarget)
                {
                    // Bu partide kopyaya dönen satır (devralınan geçici) çözüm tablosunda yok: anlık hedef.
                    var canonicalId = canonicalOfAlias.TryGetValue(current.Id, out var resolved) ? resolved : immediateTarget;
                    if (successorOf.TryGetValue(canonicalId, out var successor)) canonicalId = successor;
                    if (targets.TryGetValue(canonicalId, out var target))
                    {
                        if (current.CreatedByShopper)
                            UntrustedAliasPushes++;                             // S7: geçici kökenli — yazılmaz
                        else
                        {
                            if (Merge(target, item)) batch.Touch(target);       // UpdatedAt ilerlemez
                            NoteProvisionalWrite(batch, target, item);
                        }
                        redirects.Add(new SyncRedirect(item.Id, canonicalId));
                    }
                    synced++;
                    continue;
                }

                if (current.PurgedAt is not null)                               // silinmiş: sayılır, yazılmaz
                {
                    synced++;
                    continue;
                }

                if (Merge(current, item))
                {
                    current.UpdatedAt = item.UpdatedAt;
                    batch.Touch(current);
                }
                NoteProvisionalWrite(batch, current, item);
                synced++;
                continue;
            }

            if (IdentityLookup && canonicalByKey.TryGetValue(key, out var canonical))
            {
                // S6: aynı kişi başka bilgisayarda zaten var — bu Id kopya, veri asıl kayda.
                Add(batch, new Row
                {
                    Id = item.Id, Platform = key.Platform, Username = item.Username!,
                    MergedIntoId = canonical.Id, UpdatedAt = item.UpdatedAt,
                });
                if (Merge(canonical, item)) batch.Touch(canonical);             // UpdatedAt ilerlemez
                redirects.Add(new SyncRedirect(item.Id, canonical.Id));
                synced++;
                continue;
            }

            canonicalByKey[key] = AddCanonical(batch, item);                    // aynı partide ikincisi buna bağlansın
            synced++;
        }

        Commit(batch);
        RedirectsReturned += redirects.Count;
        return new SyncResponse(synced + skipped, 0, redirects);
    }

    /// <summary>Kayıt: grubun yazımları <see cref="Order"/> sırasıyla yeni rowversion alır.</summary>
    private void Commit(Batch batch)
    {
        var ordered = Order switch
        {
            CommitOrder.ModifiedFirst => batch.Touched.Where(t => !t.Added).Concat(batch.Touched.Where(t => t.Added)),
            CommitOrder.InsertedFirst => batch.Touched.Where(t => t.Added).Concat(batch.Touched.Where(t => !t.Added)),
            _ => Enumerable.Reverse(batch.Touched),
        };
        foreach (var (row, _) in ordered.ToList()) row.ChangeSeq = ++_version;
    }

    private void Add(Batch batch, Row r)
    {
        _rows[r.Id] = r;
        batch.Touched.Add((r, true));
    }

    /// <summary>Kendi Id'siyle ilk gönderimin kuralı: yeni asıl kayıt.</summary>
    private Row AddCanonical(Batch batch, SyncItem item)
    {
        var r = new Row
        {
            Id = item.Id, Platform = KeyOf(item).Platform, Username = item.Username!, UpdatedAt = item.UpdatedAt,
        };
        Merge(r, item);
        Add(batch, r);
        return r;
    }

    /// <summary>Hâlâ asıl kayıt olan geçici satıra yayıncı yazımı: yalnız DAMGALI telefonla (biçim 2)
    /// benimseme (S17). Silinmiş satıra dokunulmaz. Bağlantıların yeniden kanıtı burada modellenmez.</summary>
    private static void NoteProvisionalWrite(Batch batch, Row row, SyncItem item)
    {
        if (!row.CreatedByShopper || row.PurgedAt is not null) return;
        if (item.Format >= 2 && item.PhoneChangedAt is not null)
        {
            row.CreatedByShopper = false;
            batch.Touch(row);
        }
    }

    /// <summary>Biçim 2 birim kuralları (sunucunun kendi kuralı — eşit damgada ilk gelen kalır).</summary>
    private static bool Merge(Row target, SyncItem item) => ServerCustomerFieldMerge.Apply(target, FieldsOf(item));

    private Row? CanonicalFor(string platform, string username)
    {
        var key = username.Trim().ToLowerInvariant().Replace('İ', 'i');
        return _rows.Values.FirstOrDefault(r => r.MergedIntoId is null
            && r.Platform == platform.ToLowerInvariant() && r.IdentityKey == key);
    }

    private Guid ResolveBounded(Guid id)
    {
        var current = id;
        for (var hop = 0; hop < MaxHops && _rows.TryGetValue(current, out var r) && r.MergedIntoId is { } next; hop++)
            current = next;
        return current;
    }

    private static (string Platform, string IdentityKey) KeyOf(SyncItem item)
        => (item.Platform!.ToLowerInvariant(), item.Username!.Trim().ToLowerInvariant().Replace('İ', 'i'));

    /// <summary>Sunucu kabul sınırları = kolon sınırları (Platform 32, Username 128).</summary>
    private static bool IsAcceptable(SyncItem c)
        => !string.IsNullOrWhiteSpace(c.Platform) && c.Platform.Length <= 32
        && !string.IsNullOrWhiteSpace(c.Username) && c.Username.Length <= 128;

    /// <summary>Gelen öğenin birimleri (sunucu <c>FieldsOf</c>): TCKN kırpılır; 11 karakteri aşan TCKN
    /// birimi HİÇ gelmemiş sayılır (değer de damga da düşer — S9).</summary>
    private static ServerSyncFields FieldsOf(SyncItem i)
    {
        var plain = string.IsNullOrWhiteSpace(i.Tckn) ? null : i.Tckn.Trim();
        var fields = new ServerSyncFields
        {
            FullName = i.FullName, FullNameChangedAt = i.FullNameChangedAt,
            DisplayName = i.DisplayName, DisplayNameChangedAt = i.DisplayNameChangedAt,
            GroupId = i.GroupId, GroupIdChangedAt = i.GroupIdChangedAt,
            Address = i.Address, City = i.City, District = i.District, AddressChangedAt = i.AddressChangedAt,
            RecipientPaysActive = i.RecipientPaysActive, RecipientPaysChangedAt = i.RecipientPaysChangedAt,
            Phone = i.Phone, PhoneChangedAt = i.PhoneChangedAt,
            Email = i.Email, EmailChangedAt = i.EmailChangedAt,
            Tckn = plain, TcknChangedAt = i.TcknChangedAt,
            WhatsAppConsent = i.WhatsAppConsent, WhatsAppConsentChangedAt = i.WhatsAppConsentChangedAt,
            SmsConsent = i.SmsConsent, SmsConsentChangedAt = i.SmsConsentChangedAt,
            IsBlacklisted = i.IsBlacklisted, BlacklistReason = i.BlacklistReason,
            BlacklistedAt = i.BlacklistedAt, BlacklistChangedAt = i.BlacklistChangedAt,
            Notes = i.Notes, NotesChangedAt = i.NotesChangedAt,
        };
        return plain is { Length: > 11 } ? fields with { Tckn = null, TcknChangedAt = null } : fields;
    }

    // ── akış (LicensesWpfCustomersPullController.Changes) ───────────────────────────────

    private ChangesPage Changes(long afterSeq, int take)
    {
        take = Math.Clamp(take, 1, 500);
        var horizon = _version + 1;                                            // MIN_ACTIVE_ROWVERSION, işlem yokken
        var cursorReset = afterSeq < 0 || afterSeq >= horizon;
        if (cursorReset) afterSeq = 0;
        var items = _rows.Values
            .Where(r => r.ChangeSeq > afterSeq && r.ChangeSeq < horizon)
            .OrderBy(r => r.ChangeSeq)
            .Take(take)
            .Select(ToChange)
            .ToList();
        return new ChangesPage(items, items.Count == 0 ? afterSeq : items[^1].ChangeSeq, cursorReset);
    }

    /// <summary>S12: kopya satırı yalnız yönlendirme; asıl kayıt tam alanlarıyla (TCKN çözülemezse null — S13).</summary>
    private ChangeItem ToChange(Row p) => p.MergedIntoId is { } target
        ? ChangeItem.Redirect(p.Id, p.Platform, p.Username, target, p.ChangeSeq)
        : new ChangeItem(
            p.Id, p.Platform, p.Username, p.MergedIntoId, p.PurgedAt,
            p.FullName, p.FullNameChangedAt,
            p.DisplayName, p.DisplayNameChangedAt,
            p.GroupId, p.GroupIdChangedAt,
            p.Address, p.City, p.District, p.AddressChangedAt,
            p.RecipientPaysActive, p.RecipientPaysChangedAt,
            p.Phone, p.PhoneChangedAt,
            p.Email, p.EmailChangedAt,
            TcknUnreadable ? null : p.Tckn, p.TcknChangedAt,
            p.WhatsAppConsent, p.WhatsAppConsentChangedAt,
            p.SmsConsent, p.SmsConsentChangedAt,
            p.IsBlacklisted, p.BlacklistReason, p.BlacklistedAt, p.BlacklistChangedAt,
            p.Notes, p.NotesChangedAt,
            p.ChangeSeq,
            p.CreatedByShopper);

    // ── form gönderimleri (/api/v1/me/form-submissions) ─────────────────────────────────

    /// <summary>Bileşik imleç (SubmittedAt, Id): <c>since</c>'ten sonrakiler, aynı damgada Id'si büyük
    /// olanlar; sıra aynı karşılaştırmayla (istemci imleci son satırdan okur).</summary>
    private List<IntakeFormSubmissionDto> Forms(string query)
    {
        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        var limit = int.Parse(parts["limit"], CultureInfo.InvariantCulture);
        IEnumerable<IntakeFormSubmissionDto> rows = _forms.OrderBy(f => f.SubmittedAt).ThenBy(f => f.Id);
        if (parts.TryGetValue("since", out var sinceText))
        {
            var since = DateTimeOffset.Parse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var sinceId = parts.TryGetValue("sinceId", out var idText) ? Guid.Parse(idText) : Guid.Empty;
            rows = rows.Where(f => f.SubmittedAt > since || (f.SubmittedAt == since && f.Id.CompareTo(sinceId) > 0));
        }
        return rows.Take(limit).ToList();
    }
}
