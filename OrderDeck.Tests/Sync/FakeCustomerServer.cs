using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderDeck.Core.Customers;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sunucunun müşteri senkron kurallarının test ikizi (Bölüm C, C11) — master'daki
/// <c>LicensesWpfCustomersSyncController</c>, <c>LicensesWpfCustomersPullController</c> ve
/// <c>CustomerFieldMerge</c>'ün aynası. Gönderim (<c>ApplyBatchAsync</c> ile aynı sıra): Id'ye göre
/// tekilleştirme (son giriş kazanır) ve bozuk platform/kullanıcı adı elemesi; önce DEVRALMA (S8 —
/// yeni Id'nin kimliğinin asıl kaydı geçiciyse yayıncının satırı asıl kayıt olur, geçici satırın
/// yalnız DAMGALI birimleri taşınır, beyanı boşaltılır, kopyaya döner, yönlendirme dönmez); sonra
/// payload sırasıyla: bilinen kopya (S7 — en çok üç adımda asıl kayda çözülür; geçici kökenliye
/// hiçbir şey yazılmaz; yönlendirme her durumda), silinmiş satıra yazılmaz, kendi Id'si (birim
/// birleştirme + yalnız damgalı telefonla benimseme, S17), yeni kopya (S6), yeni asıl kayıt.
///
/// <para>Birim birleştirme istemcinin <see cref="CustomerUnitMerge"/>'ü, <c>incomingWinsTie: false</c>
/// (sunucu eşit damgada ilk geleni tutar — C2): sunucuyla eşitliğini C3 testleri kilitliyor, burada
/// sınanan PROTOKOL. Bilinçli farklar: takma ad kuralı (istemciye özgü) burada da çalışır ve metinler
/// kolon sınırına kırpılmaz — senaryolar takma ad FullName'i ve sınırı aşan metni sınamaz; biçim 1
/// (eski sürüm) gönderimi desteklenmez (istemci yalnız biçim 2 gönderir), gelirse fırlatır.</para>
///
/// <para>Akış: <c>ChangeSeq</c> = rowversion, bir gönderimin bütün yazımları kayıt anında numaralanır
/// — EF Core'un komut sırası gibi önce DEĞİŞEN satırlar, sonra EKLENENLER (istemci sıraya bağlı
/// olmamalı, U4). Ufuk = sıradaki rowversion (MIN_ACTIVE_ROWVERSION); imleç eksi ya da ufkun
/// üstündeyse sayfa baştan + <c>CursorReset</c> (S11); boş sayfada <c>nextAfterSeq = afterSeq</c>;
/// kopya satırı yalnız yönlendirme (S12).</para>
/// </summary>
internal sealed class FakeCustomerServer : HttpMessageHandler
{
    internal sealed class Row
    {
        public Guid Id;
        public string Platform = "";
        public string Username = "";
        public Guid? MergedIntoId;
        public bool CreatedByShopper;
        public DateTimeOffset? PurgedAt;
        public DateTimeOffset UpdatedAt;
        public CustomerSyncState Fields = new();
        public long ChangeSeq;

        /// <summary>Sunucudaki <c>WpfCustomerProjection.IdentityKeyOf</c> — istemcinin
        /// <see cref="CustomerIdentity.KeyOf"/>'u birebir aynısı (C1).</summary>
        public string IdentityKey => CustomerIdentity.KeyOf(Username);

        public Row Clone() => new()
        {
            Id = Id, Platform = Platform, Username = Username, MergedIntoId = MergedIntoId,
            CreatedByShopper = CreatedByShopper, PurgedAt = PurgedAt, UpdatedAt = UpdatedAt,
            Fields = Fields.Clone(), ChangeSeq = ChangeSeq,
        };
    }

    /// <summary>Kopya zincirinin çözüm sınırı (sunucu <c>CustomerIdResolver.MaxHops</c>).</summary>
    private const int MaxHops = 3;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Row> _rows = new();
    private long _version;

    public Guid LicenseId { get; } = Guid.NewGuid();
    public string Lisans { get; } = $"lisans-{Guid.NewGuid():N}";

    /// <summary>Asıl kayda hiçbir şey yazılmayan geçici kökenli kopya gönderimi sayısı (S7).</summary>
    public int UntrustedAliasPushes { get; private set; }

    /// <summary>Yayıncının satırına devredilen geçici kayıt sayısı (S8).</summary>
    public int TakenOver { get; private set; }

    private Action? _beforeNextChanges;

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
            var r = new Row
            {
                Id = Guid.NewGuid(), Platform = platform.ToLowerInvariant(), Username = username,
                CreatedByShopper = true, UpdatedAt = DateTimeOffset.UtcNow,
                Fields = new CustomerSyncState { Username = username, FullName = fullName, Phone = phone, Address = address },
            };
            _rows[r.Id] = r;
            r.ChangeSeq = ++_version;
            return r.Id;
        }
    }

    /// <summary>KVKK silmesi (<c>WpfCustomerProjection.MarkPurged</c>): kişisel birimlerin DEĞERLERİ
    /// boşalır (<c>ScrubPersonal</c> — damgalar kalır), not ve kara liste kalır, PurgedAt ilk silme
    /// anını korur.</summary>
    public void Purge(Guid id)
    {
        lock (_gate)
        {
            var r = _rows[id];
            var now = DateTimeOffset.UtcNow;
            ScrubPersonal(r);
            r.PurgedAt ??= now;
            r.Fields.PurgedAt = r.PurgedAt.Value.ToUnixTimeSeconds();   // birleştirme kapısı
            r.UpdatedAt = now;
            r.ChangeSeq = ++_version;
        }
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
        if (path == "/api/v1/me/licenses")
            return FakeHttpMessageHandler.Json(200, $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}}]");

        // Lisans sahipliği (iki uç da başka lisansa 404 döner).
        var prefix = $"/api/v1/licenses/{LicenseId}/wpf-customers/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (path.EndsWith("/sync", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
        {
            var body = JsonSerializer.Deserialize<WpfCustomerSyncRequest>(await req.Content!.ReadAsStringAsync(ct), Web)!;
            if (body.Customers is { Count: > 500 })                            // parti boyutu istemci hatası (S2)
                return FakeHttpMessageHandler.Problem(400, "batch-too-large", "Max 500 customers per batch");
            lock (_gate) return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(Sync(body.Customers), Web));
        }
        if (path.EndsWith("/changes", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
        {
            Interlocked.Exchange(ref _beforeNextChanges, null)?.Invoke();
            var q = req.RequestUri.Query;
            var after = long.Parse(Regex.Match(q, @"afterSeq=(-?\d+)").Groups[1].Value);
            var take = int.Parse(Regex.Match(q, @"take=(\d+)").Groups[1].Value);
            lock (_gate) return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(Changes(after, take), Web));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ── gönderim (LicensesWpfCustomersSyncController.Sync + ApplyBatchAsync) ────────────

    /// <summary>Bir partinin yazımları: kayıt anında (<see cref="Commit"/>) numaralanır.</summary>
    private sealed class Batch
    {
        public readonly List<Row> Modified = new();
        public readonly List<Row> Added = new();

        public void Touch(Row r)
        {
            if (!Added.Contains(r) && !Modified.Contains(r)) Modified.Add(r);
        }
    }

    private WpfCustomerSyncResponse Sync(IReadOnlyList<WpfCustomerSyncItem>? payload)
    {
        if (payload is null || payload.Count == 0) return new WpfCustomerSyncResponse(0, 0, new List<WpfCustomerRedirect>());

        // Null öğe ve bozuk platform/kullanıcı adı yazılmadan SAYILIR; Id'ye göre tekilleştirme (son kazanır).
        var nonNull = payload.Where(i => i is not null).ToList();
        var deduped = nonNull.GroupBy(i => i.Id).Select(g => g.Last()).ToList();
        var items = deduped.Where(IsAcceptable).ToList();
        var skipped = payload.Count - nonNull.Count + (deduped.Count - items.Count);

        var batch = new Batch();
        var existing = items.Where(i => _rows.ContainsKey(i.Id)).ToDictionary(i => i.Id, i => _rows[i.Id]);
        var newItems = items.Where(i => !existing.ContainsKey(i.Id)).ToList();

        // Yeni Id'lerin kimlik araması: asıl kayıt (kopya değil, boş olmayan anahtar). B1'den önceki
        // dönemin seçim kuralı (yayıncı satırı geçiciden önce, sonra en eski UpdatedAt, sonra Id) —
        // tekil indeksle her grupta tek satır olur. Silinmiş asıl kayıt da bulunur (sunucu süzmez).
        var newKeys = newItems.Select(KeyOf).ToHashSet();
        var canonicalByKey = _rows.Values
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

        var redirects = new List<WpfCustomerRedirect>();
        var synced = 0;

        // DEVRALMA (S8) — partinin geri kalanından ÖNCE (sonuç payload sırasına bağlı kalmasın).
        var successorOf = new Dictionary<Guid, Guid>();
        var takeoverItems = new HashSet<Guid>();
        foreach (var item in newItems)
        {
            var key = KeyOf(item);
            if (!canonicalByKey.TryGetValue(key, out var provisional) || !provisional.CreatedByShopper) continue;

            var created = AddCanonical(batch, item);
            var carried = provisional.Fields.StampedOnly();
            if (provisional.PurgedAt is not null) carried = carried.WithoutScrubbedUnits();
            CustomerUnitMerge.Apply(created.Fields, carried);
            ScrubPersonal(provisional);                                       // köken bayrağı KALIR
            provisional.MergedIntoId = created.Id;
            batch.Touch(provisional);
            foreach (var older in _rows.Values.Where(r => r.MergedIntoId == provisional.Id && r.Id != provisional.Id).ToList())
            {
                older.MergedIntoId = created.Id;                              // kopyanın kopyası kalmaz
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
                            UntrustedAliasPushes++;                            // S7: geçici kökenli — yazılmaz
                        else
                        {
                            if (Merge(target, item)) batch.Touch(target);      // UpdatedAt ilerlemez
                            NoteProvisionalWrite(batch, target, item);
                        }
                        redirects.Add(new WpfCustomerRedirect(item.Id, canonicalId));
                    }
                    synced++;
                    continue;
                }

                if (current.PurgedAt is not null)                              // silinmiş: sayılır, yazılmaz
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

            if (canonicalByKey.TryGetValue(key, out var canonical))
            {
                // S6: aynı kişi başka bilgisayarda zaten var — bu Id kopya, veri asıl kayda.
                var alias = new Row
                {
                    Id = item.Id, Platform = key.Platform, Username = item.Username,
                    MergedIntoId = canonical.Id, UpdatedAt = item.UpdatedAt,
                    Fields = new CustomerSyncState { Username = item.Username },
                };
                Add(batch, alias);
                if (Merge(canonical, item)) batch.Touch(canonical);            // UpdatedAt ilerlemez
                redirects.Add(new WpfCustomerRedirect(item.Id, canonical.Id));
                synced++;
                continue;
            }

            canonicalByKey[key] = AddCanonical(batch, item);                   // aynı partide ikincisi buna bağlansın
            synced++;
        }

        Commit(batch);
        return new WpfCustomerSyncResponse(synced + skipped, 0, redirects);
    }

    /// <summary>Kayıt: değişen satırlar, sonra eklenenler (EF Core komut sırası) yeni rowversion alır.</summary>
    private void Commit(Batch batch)
    {
        foreach (var r in batch.Modified) r.ChangeSeq = ++_version;
        foreach (var r in batch.Added) r.ChangeSeq = ++_version;
    }

    private void Add(Batch batch, Row r)
    {
        _rows[r.Id] = r;
        batch.Added.Add(r);
    }

    /// <summary>Kendi Id'siyle ilk gönderimin kuralı: yeni asıl kayıt.</summary>
    private Row AddCanonical(Batch batch, WpfCustomerSyncItem item)
    {
        var r = new Row
        {
            Id = item.Id, Platform = KeyOf(item).Platform, Username = item.Username, UpdatedAt = item.UpdatedAt,
            Fields = new CustomerSyncState { Username = item.Username },
        };
        Merge(r, item);
        Add(batch, r);
        return r;
    }

    /// <summary>Hâlâ asıl kayıt olan geçici satıra yayıncı yazımı: yalnız DAMGALI telefonla (biçim 2)
    /// benimseme (S17). Silinmiş satıra dokunulmaz. Bağlantıların yeniden kanıtı burada modellenmez.</summary>
    private static void NoteProvisionalWrite(Batch batch, Row row, WpfCustomerSyncItem item)
    {
        if (!row.CreatedByShopper || row.PurgedAt is not null) return;
        if (item.Format >= 2 && item.PhoneChangedAt is not null)
        {
            row.CreatedByShopper = false;
            batch.Touch(row);
        }
    }

    /// <summary>Biçim 2 birim kuralları; eşit damga = ilk gelen kalır (incomingWinsTie: false).</summary>
    private static bool Merge(Row target, WpfCustomerSyncItem item)
    {
        if (item.Format < 2)
            throw new NotSupportedException("Sahte sunucu yalnız biçim 2'yi taklit eder (istemci biçim 1 göndermez).");
        return CustomerUnitMerge.Apply(target.Fields, FieldsOf(item));
    }

    /// <summary>Sunucunun <c>ScrubPersonal</c>'ı: kişisel birimlerin değerleri boşalır, damgaları kalır.</summary>
    private static void ScrubPersonal(Row r)
    {
        var f = r.Fields;
        f.FullName = null; f.DisplayName = null;
        f.Phone = null; f.Email = null; f.Tckn = null;
        f.Address = null; f.City = null; f.District = null;
        f.WhatsAppConsent = false; f.SmsConsent = false;
    }

    private Row? CanonicalFor(string platform, string username)
        => _rows.Values.FirstOrDefault(r => r.MergedIntoId is null
            && r.Platform == platform.ToLowerInvariant()
            && r.IdentityKey == CustomerIdentity.KeyOf(username));

    private Guid ResolveBounded(Guid id)
    {
        var current = id;
        for (var hop = 0; hop < MaxHops && _rows.TryGetValue(current, out var r) && r.MergedIntoId is { } next; hop++)
            current = next;
        return current;
    }

    private static (string Platform, string IdentityKey) KeyOf(WpfCustomerSyncItem item)
        => (item.Platform.ToLowerInvariant(), CustomerIdentity.KeyOf(item.Username));

    /// <summary>Sunucu kabul sınırları = kolon sınırları (Platform 32, Username 128).</summary>
    private static bool IsAcceptable(WpfCustomerSyncItem c)
        => !string.IsNullOrWhiteSpace(c.Platform) && c.Platform.Length <= 32
        && !string.IsNullOrWhiteSpace(c.Username) && c.Username.Length <= 128;

    // ── akış (LicensesWpfCustomersPullController.Changes) ───────────────────────────────

    private WpfCustomerChangesPage Changes(long afterSeq, int take)
    {
        take = Math.Clamp(take, 1, 500);
        var horizon = _version + 1;                                           // MIN_ACTIVE_ROWVERSION, işlem yokken
        var cursorReset = afterSeq < 0 || afterSeq >= horizon;
        if (cursorReset) afterSeq = 0;
        var items = _rows.Values
            .Where(r => r.ChangeSeq > afterSeq && r.ChangeSeq < horizon)
            .OrderBy(r => r.ChangeSeq)
            .Take(take)
            .Select(ToChange)
            .ToList();
        return new WpfCustomerChangesPage(items, items.Count == 0 ? afterSeq : items[^1].ChangeSeq, cursorReset);
    }

    private static long? Ms(DateTimeOffset? d) => d?.ToUnixTimeMilliseconds();
    private static DateTimeOffset? At(long? ms) => ms is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    /// <summary>Gelen öğenin birimleri (sunucu <c>FieldsOf</c>): TCKN kırpılır; 11 karakteri aşan TCKN
    /// birimi HİÇ gelmemiş sayılır (değer de damga da düşer — S9).</summary>
    private static CustomerSyncState FieldsOf(WpfCustomerSyncItem i)
    {
        var tckn = string.IsNullOrWhiteSpace(i.Tckn) ? null : i.Tckn.Trim();
        var tcknDropped = tckn is { Length: > 11 };
        return new CustomerSyncState
        {
            Username = i.Username,
            FullName = i.FullName, FullNameChangedAt = Ms(i.FullNameChangedAt),
            DisplayName = i.DisplayName, DisplayNameChangedAt = Ms(i.DisplayNameChangedAt),
            GroupId = i.GroupId, GroupIdChangedAt = Ms(i.GroupIdChangedAt),
            Address = i.Address, City = i.City, District = i.District, AddressChangedAt = Ms(i.AddressChangedAt),
            RecipientPaysActive = i.RecipientPaysActive, RecipientPaysChangedAt = Ms(i.RecipientPaysChangedAt),
            Phone = i.Phone, PhoneChangedAt = Ms(i.PhoneChangedAt),
            Email = i.Email, EmailChangedAt = Ms(i.EmailChangedAt),
            Tckn = tcknDropped ? null : tckn, TcknChangedAt = tcknDropped ? null : Ms(i.TcknChangedAt),
            WhatsAppConsent = i.WhatsAppConsent, WhatsAppConsentChangedAt = Ms(i.WhatsAppConsentChangedAt),
            SmsConsent = i.SmsConsent, SmsConsentChangedAt = Ms(i.SmsConsentChangedAt),
            IsBlacklisted = i.IsBlacklisted, BlacklistReason = i.BlacklistReason,
            BlacklistedAt = i.BlacklistedAt?.ToUnixTimeSeconds(), BlacklistChangedAt = Ms(i.BlacklistChangedAt),
            Notes = i.Notes, NotesChangedAt = Ms(i.NotesChangedAt),
        };
    }

    /// <summary>S12: kopya satırı yalnız yönlendirme (öbür her alan boş/false — PurgedAt ve
    /// CreatedByShopper dahil).</summary>
    private static WpfCustomerChangeItem ToChange(Row r)
    {
        var blank = new WpfCustomerChangeItem(
            r.Id, r.Platform, r.Username, null, null,
            null, null, null, null, null, null,
            null, null, null, null, false, null,
            null, null, null, null, null, null,
            false, null, false, null,
            false, null, null, null, null, null,
            r.ChangeSeq);
        if (r.MergedIntoId is { } target) return blank with { MergedIntoId = target };
        var f = r.Fields;
        return blank with
        {
            PurgedAt = r.PurgedAt,
            FullName = f.FullName, FullNameChangedAt = At(f.FullNameChangedAt),
            DisplayName = f.DisplayName, DisplayNameChangedAt = At(f.DisplayNameChangedAt),
            GroupId = f.GroupId, GroupIdChangedAt = At(f.GroupIdChangedAt),
            Address = f.Address, City = f.City, District = f.District, AddressChangedAt = At(f.AddressChangedAt),
            RecipientPaysActive = f.RecipientPaysActive, RecipientPaysChangedAt = At(f.RecipientPaysChangedAt),
            Phone = f.Phone, PhoneChangedAt = At(f.PhoneChangedAt),
            Email = f.Email, EmailChangedAt = At(f.EmailChangedAt),
            Tckn = f.Tckn, TcknChangedAt = At(f.TcknChangedAt),
            WhatsAppConsent = f.WhatsAppConsent, WhatsAppConsentChangedAt = At(f.WhatsAppConsentChangedAt),
            SmsConsent = f.SmsConsent, SmsConsentChangedAt = At(f.SmsConsentChangedAt),
            IsBlacklisted = f.IsBlacklisted, BlacklistReason = f.BlacklistReason,
            BlacklistedAt = f.BlacklistedAt is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            BlacklistChangedAt = At(f.BlacklistChangedAt),
            Notes = f.Notes, NotesChangedAt = At(f.NotesChangedAt),
            CreatedByShopper = r.CreatedByShopper,
        };
    }
}
