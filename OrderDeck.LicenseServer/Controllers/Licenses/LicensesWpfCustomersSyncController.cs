using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperLinking;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App'in lokal Customer kayıtlarını LicenseServer'a periyodik bulk sync.
/// Server-side WpfCustomerProjection tablosuna upsert. Sipariş eşleşmesi:
/// shopper-app kullanıcısı bir yayıncıya bağlanırken (LicenseId, Platform,
/// Username) ile match yapılır; match retroactive olarak burada da çalıştırılır
/// (sync sırasında yeni eşleşen link.WpfCustomerId güncellenir).
///
/// <para>Çok bilgisayarlı senkron: alanlar birim damgalarıyla
/// <see cref="CustomerFieldMerge"/> kurallarına göre yazılır. Bu partide yeni
/// olan bir Id'nin kimliği (platform + <see cref="WpfCustomerProjection.IdentityKey"/>)
/// başka bir asıl kayıtta zaten varsa Id KOPYA olarak bağlanır, verisi asıl
/// kayda yazılır ve yanıtta yönlendirme döner; istemci yerel satırını asıl
/// kaydın Id'sine taşır.</para>
///
/// <para>Shopper'ın açtığı GEÇİCİ kayıt
/// (<see cref="WpfCustomerProjection.CreatedByShopper"/>, A5c): birleştirme
/// telefon kanıtını delemez. Yayıncı kişiyi kendi Id'siyle gönderince
/// (devralma) yayıncının satırı asıl kayıt olur, geçici satır kopyaya döner ve
/// taşınan bağlantılar yayıncının telefonuna karşı kanıtı yeniden ister;
/// geçici kökenli kopyanın gönderimi asıl kayda yazılmaz; hâlâ asıl kayıt olan
/// geçici satıra gelen her yazımda da bağlantılar kanıtı yeniden ister.</para>
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/v1/licenses/{licenseId:guid}/wpf-customers")]
public sealed class LicensesWpfCustomersSyncController : ControllerBase
{
    /// <summary>Eşzamanlılık çakışmasında partinin en çok kaç kez
    /// uygulanacağı: ilk deneme + BİR yeniden deneme (bkz. <see cref="Sync"/>).</summary>
    private const int MaxAttempts = 2;

    private readonly LicenseDbContext _db;
    private readonly CustomerIdentityMerger _merger;
    private readonly TcknProtector _tckn;
    private readonly ILogger<LicensesWpfCustomersSyncController> _logger;

    public LicensesWpfCustomersSyncController(
        LicenseDbContext db, CustomerIdentityMerger merger, TcknProtector tckn,
        ILogger<LicensesWpfCustomersSyncController> logger)
    {
        _db = db;
        _merger = merger;
        _tckn = tckn;
        _logger = logger;
    }

    /// <param name="Platform">Null olabilir (bkz. <see cref="IsAcceptable"/>):
    /// null olmayan referans tipi ASP.NET'te örtük ZORUNLU sayılır ve tek bir
    /// null alan model doğrulamasında TÜM partiyi 400'e düşürürdü. Kural tek
    /// yerde — kabul edilmeyen öğe yalnız kendini düşürür.</param>
    /// <param name="Username">Platform ile aynı gerekçe.</param>
    /// <param name="Format">1 = eski istemci (yalnız ilk yedi alan, damgasız).
    /// 2 = tam alan + BİRİM damgaları (bkz. CustomerSyncFields). Eski istemci
    /// alanı göndermez → 1.</param>
    public sealed record SyncItem(
        Guid Id,
        string? Platform,
        string? Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt,
        int Format = 1,
        DateTimeOffset? FullNameChangedAt = null,
        string? DisplayName = null,
        DateTimeOffset? DisplayNameChangedAt = null,
        string? GroupId = null,
        DateTimeOffset? GroupIdChangedAt = null,
        string? City = null,
        string? District = null,
        DateTimeOffset? AddressChangedAt = null,
        bool RecipientPaysActive = false,
        DateTimeOffset? RecipientPaysChangedAt = null,
        DateTimeOffset? PhoneChangedAt = null,
        string? Email = null,
        DateTimeOffset? EmailChangedAt = null,
        string? Tckn = null,
        DateTimeOffset? TcknChangedAt = null,
        bool WhatsAppConsent = false,
        DateTimeOffset? WhatsAppConsentChangedAt = null,
        bool SmsConsent = false,
        DateTimeOffset? SmsConsentChangedAt = null,
        bool IsBlacklisted = false,
        string? BlacklistReason = null,
        DateTimeOffset? BlacklistedAt = null,
        DateTimeOffset? BlacklistChangedAt = null,
        string? Notes = null,
        DateTimeOffset? NotesChangedAt = null)
    {
        /// <param name="tcknProtected">ŞİFRELİ TCKN — çağıran (<c>FieldsOf</c>)
        /// üretir. Düz <c>Tckn</c> alanlara asla girmez.</param>
        public CustomerSyncFields ToFields(string? tcknProtected) => new()
        {
            FullName = FullName, FullNameChangedAt = FullNameChangedAt,
            DisplayName = DisplayName, DisplayNameChangedAt = DisplayNameChangedAt,
            GroupId = GroupId, GroupIdChangedAt = GroupIdChangedAt,
            Address = Address, City = City, District = District, AddressChangedAt = AddressChangedAt,
            RecipientPaysActive = RecipientPaysActive, RecipientPaysChangedAt = RecipientPaysChangedAt,
            Phone = Phone, PhoneChangedAt = PhoneChangedAt,
            Email = Email, EmailChangedAt = EmailChangedAt,
            TcknProtected = tcknProtected, TcknChangedAt = TcknChangedAt,
            WhatsAppConsent = WhatsAppConsent, WhatsAppConsentChangedAt = WhatsAppConsentChangedAt,
            SmsConsent = SmsConsent, SmsConsentChangedAt = SmsConsentChangedAt,
            IsBlacklisted = IsBlacklisted, BlacklistReason = BlacklistReason,
            BlacklistedAt = BlacklistedAt, BlacklistChangedAt = BlacklistChangedAt,
            Notes = Notes, NotesChangedAt = NotesChangedAt,
        };

        /// <summary>Birim damgaları — saat kayması denetimi için.</summary>
        public IEnumerable<DateTimeOffset?> Stamps() =>
        [
            FullNameChangedAt, DisplayNameChangedAt, GroupIdChangedAt, AddressChangedAt,
            RecipientPaysChangedAt, PhoneChangedAt, EmailChangedAt, TcknChangedAt,
            WhatsAppConsentChangedAt, SmsConsentChangedAt, BlacklistChangedAt, NotesChangedAt,
        ];
    }

    public sealed record SyncRequest(List<SyncItem> Customers);

    /// <summary>Kopya olarak bağlanan her Id için asıl kayıt. İstemci yerel
    /// satırını bu Id'ye taşır. Eski istemciler alanı yok sayar.</summary>
    public sealed record SyncRedirect(Guid Id, Guid CanonicalId);

    public sealed record SyncResponse(int Synced, int RetroactiveMatches, List<SyncRedirect> Redirects);

    /// <summary>Bir partinin uygulanma sonucu (kayıt öncesi). Sayaçlar günlüğe
    /// kayıttan SONRA yazılır: yeniden denenen ilk denemenin sayısı
    /// kaydedilmemiş bir şeyi anlatırdı.</summary>
    /// <param name="LinksUnbound">Telefon kanıtını yeniden veremeyip beklemeye
    /// alınan Shopper bağlantısı sayısı (devralma ya da geçici satıra yazım).</param>
    /// <param name="TakenOver">Yayıncının satırına devredilen geçici kayıt sayısı.</param>
    /// <param name="UntrustedAliasPushes">Asıl kayda hiçbir şey yazılmayan
    /// geçici kökenli kopya gönderimi sayısı.</param>
    private sealed record BatchOutcome(
        int Synced, List<SyncRedirect> Redirects, int LinksUnbound, int TakenOver, int UntrustedAliasPushes);

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(Guid licenseId, [FromBody] SyncRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Customers is null || req.Customers.Count == 0)
            return Ok(new SyncResponse(0, 0, new List<SyncRedirect>()));

        // Parti boyutu istemci hatasıdır (veri değil): 400 kalır.
        if (req.Customers.Count > 500)
            return Problem(title: "batch-too-large", statusCode: 400, detail: "Max 500 customers per batch");

        // Aşağıdaki elemeler aynı kuraldan: TEK bir öğe partiyi ASLA düşürmez.
        // Düşürseydi aynı hata her denemede tekrarlanır, istemcinin imleci hiç
        // ilerlemez, aynı parti sonsuza kadar yeniden gönderilir ve arkasındaki
        // müşteriler rehin kalırdı (2026-08-14'te sahada 565 müşteri 12 gün
        // böyle bekledi). Elenen öğe yazılmadan SAYILIR; günlüğe yalnız Id'ler.

        // (0) Null öğe: tekilleştirme Id'ye erişirken NullReferenceException →
        // 500 olurdu. Id'si yok; günlüğe yalnız sayı.
        var nonNull = req.Customers.Where(c => c is not null).ToList();
        var nullItems = req.Customers.Count - nonNull.Count;
        if (nullItems > 0)
            _logger.LogWarning(
                "Müşteri senkronu: {Count} null öğe geldi, yazılmadan sayıldı (lisans {LicenseId})",
                nullItems, licenseId);

        // (1) Id'ye göre TEKİLLEŞTİR (orders/sync deseni): aynı Id iki kez
        // gelirse ikinci giriş aynı yeni satırı ikinci kez izlemeye ekler →
        // izleme istisnası → 500. Son giriş kazanır: payload sırası istemcinin
        // niyet sırasıdır.
        var deduped = nonNull.GroupBy(c => c.Id).Select(g => g.Last()).ToList();

        // (2) Bozuk (null, boş ya da kolon sınırını aşan) platform/kullanıcı
        // adı. İstemci bunları göndermeden önce kendisi de eliyor
        // (WpfCustomerProjectionSyncService); bu, başka bir istemcinin ya da
        // sürümün kuyruğunu kilitlemesin diye. Öğe zaten eşleşemezdi —
        // eşleşme platform + kullanıcı adına dayanıyor.
        var invalid = deduped.Where(c => !IsAcceptable(c)).Select(c => c.Id).ToList();
        if (invalid.Count > 0)
            _logger.LogWarning(
                "Müşteri senkronu: {Count} öğe geçersiz platform/kullanıcı adıyla geldi, yazılmadan sayıldı (lisans {LicenseId}): {Ids}",
                invalid.Count, licenseId, IdsForLog(invalid));
        var candidates = deduped.Where(IsAcceptable).ToList();

        // (3) Başka bir lisansta kayıtlı Id: lisansa göre aranan satırlar onu
        // bulamaz, öğe "yeni" sanılır ve INSERT birincil anahtara çarpar (ör.
        // yeniden verilen bir lisansın bilgisayarı aynı yerel Id'leri yeniden
        // gönderir). Başka yayıncının satırına ASLA yazılmaz. Kopyalar da
        // aranır (IgnoreQueryFilters): birincil anahtar onları da kapsar. Yalnız
        // Id seçilir — o satırın kişisel verisi bu isteğe hiç yüklenmez.
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var foreign = await _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.LicenseId != licenseId && candidateIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (foreign.Count > 0)
            _logger.LogWarning(
                "Müşteri senkronu: {Count} öğenin Id'si başka bir lisansta kayıtlı, yazılmadan sayıldı (lisans {LicenseId}): {Ids}",
                foreign.Count, licenseId, IdsForLog(foreign));
        var foreignIds = foreign.ToHashSet();
        var items = candidates.Where(c => !foreignIds.Contains(c.Id)).ToList();
        var skipped = nullItems + invalid.Count + foreign.Count;

        var ids = items.Select(c => c.Id).ToList();
        var now = DateTimeOffset.UtcNow;

        // Saat kayması: damgalar bilgisayar saatidir; ileri saatli bilgisayar,
        // gerçek zaman yetişene kadar her çakışmayı kazanır. KIRPILMAZ (kırpılsa
        // sunucu ile istemci damgası farklı kalır, satır gidip gelirdi); yalnız
        // günlüğe yazılır ki yayıncıya "saatini düzelt" denebilsin.
        var futureLimit = now.AddMinutes(5);
        var futureStamps = items.Where(c => c.Format >= 2)
            .Sum(c => c.Stamps().Count(st => st > futureLimit));
        if (futureStamps > 0)
            _logger.LogWarning(
                "Müşteri senkronu: {Count} alan damgası sunucu saatinin 5 dakikadan fazla ilerisinde (lisans {LicenseId}) — bilgisayar saati ileri olabilir",
                futureStamps, licenseId);

        // Eşzamanlılık çakışması (DbUpdateConcurrencyException) partiyi hemen
        // 409'la reddetmez: değişiklik izleyicisi temizlenir ve parti TAZE
        // okumayla BAŞTAN bir kez daha uygulanır (ShopperPurgeService.SaveAsync
        // deseni); ikinci çakışma 409. Çakışmanın tipik kaynağı birleştiricinin
        // dokunduğu bir sipariş/bakiye satırıdır — 500 müşterilik partiyi bunun
        // için reddetmek canlı yayında gereksiz gürültü (A4 kalite incelemesi).
        //
        // Silmeyi geri ALMAZ: yeniden deneme bayat değerleri yeniden yükleyip
        // tekrar kaydetmiyor, kuralları taze satıra yeniden uyguluyor. Purge bu
        // isteğin okumasından sonra tombstone yazdıysa (PurgedAt jetonu
        // çakışmanın kaynağı), taze okuma PurgedAt'ı görür ve kişisel alanlara
        // hiçbir şey yazılmaz.
        BatchOutcome outcome;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                outcome = await ApplyBatchAsync(licenseId, items, ids, now, ct);
                await _db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxAttempts)
            {
                _logger.LogInformation(
                    "Müşteri senkronu eşzamanlı bir yazımla çakıştı (lisans {LicenseId}, varlık {Entities}); parti taze okumayla bir kez yeniden uygulanıyor",
                    licenseId, ConflictingEntities(ex));
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // İzleyici boş bırakılır: yarım uygulanmış parti bu istekteki
                // sonraki bir SaveChanges'le diske inmesin.
                _db.ChangeTracker.Clear();
                _logger.LogWarning(
                    "Müşteri senkronu yeniden denemede de çakıştı (lisans {LicenseId}, varlık {Entities}); 409 dönülüyor",
                    licenseId, ConflictingEntities(ex));
                return Problem(
                    title: "sync-conflict",
                    detail: "Müşteri verisi eşzamanlı değişti; güncel durumla yeniden deneyin.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        if (outcome.TakenOver + outcome.LinksUnbound + outcome.UntrustedAliasPushes > 0)
            _logger.LogInformation(
                "Müşteri senkronu (lisans {LicenseId}): {TakenOver} geçici kayıt yayıncının satırına devredildi, {LinksUnbound} Shopper bağlantısı telefon kanıtını yeniden veremeyip beklemeye alındı, {Untrusted} geçici kökenli kopya gönderimi asıl kayda yazılmadı",
                licenseId, outcome.TakenOver, outcome.LinksUnbound, outcome.UntrustedAliasPushes);

        // Retroactive match: for newly-synced (or updated) projections, find any
        // ShopperBroadcasterLink with matching (LicenseId, Platform, Username) where
        // WpfCustomerId is null, and set it. Drive-by — avoids needing a cron job.
        //
        // Burada da telefon kanıtı şart; kural WpfCustomerLinkMatcher'da. Bu üçüncü
        // kopyanın kapısız kalması, kayıt ve katılma akışlarındaki düzeltmeleri
        // TAMAMEN boşa çıkarırdı: kanıt gelmediği için beklemede bırakılan bağlantı
        // bir sonraki WPF sync'inde buradan sessizce bağlanırdı.
        //
        // Bu aynı zamanda kurtarma yolu: yayıncı WPF'te müşterinin telefonunu
        // girdiğinde sync o telefonu buraya taşır ve beklemedeki bağlantı kendiliğinden
        // kurulur — ayrı bir onay ekranı gerekmiyor.
        // Ham payload'ı burada yeniden kullanma: yukarıda tombstone olduğu için
        // atlanan öğe hâlâ telefon taşıyabilir. İlk kayıttan sonra yalnız DB'de
        // gerçekten var olan ve PurgedAt IS NULL satırlar eşleştirmeye adaydır.
        // Kopyalar (MergedIntoId dolu) aday değildir: kişisel alanları boş, bir
        // bağlantı yalnız asıl kayda bağlanmalı. Bu isteğin yönlendirdiği asıl
        // kayıtlar ise adaydır: kopya gönderimi asıl kaydın telefonunu doldurmuş
        // olabilir — bekleyen bağlantı asıl kaydın kendi bilgisayarının
        // gönderimini beklemesin. Devralmada (A5c) asıl kayıt olan yayıncı
        // satırı zaten bu partinin Id'lerinde: gerçek müşterinin bekleyen
        // bağlantısı yayıncının telefonuyla aynı istekte bağlanır.
        var matchIds = ids.Concat(outcome.Redirects.Select(r => r.CanonicalId)).Distinct().ToList();
        var matchableProjections = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId
                && matchIds.Contains(p.Id)
                && p.PurgedAt == null
                && p.MergedIntoId == null)
            .ToListAsync(ct);

        var retroactiveMatches = 0;
        foreach (var projection in matchableProjections)
        {
            var unmatchedLinks = await _db.ShopperBroadcasterLinks
                .Where(l => l.LicenseId == licenseId
                    && l.WpfCustomerId == null
                    && l.LeftAt == null
                    && l.Platform == projection.Platform
                    && l.Username == projection.Username)
                .Select(l => new
                {
                    Link = l,
                    l.Shopper!.Phone,
                    l.Shopper.PhoneVerifiedAt,
                })
                .ToListAsync(ct);
            foreach (var row in unmatchedLinks)
            {
                if (!WpfCustomerLinkMatcher.PhoneProves(
                        projection.Phone, row.Phone, row.PhoneVerifiedAt)) continue;
                row.Link.WpfCustomerId = projection.Id;
                retroactiveMatches++;
            }
        }
        if (retroactiveMatches > 0)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Ana parti YUKARIDA kaydedildi; eşleştirme yan iş. İstisnayı
                // yalnız arada SİLİNEN bağlantı doğurur (ör. purge bağlantıları
                // siler: UPDATE 0 satıra düşer) — ShopperBroadcasterLink'te
                // eşzamanlılık jetonu yok, araya giren bir değişiklik çakışma
                // sayılmaz. 500 dönmek istemciye kaydedilmiş partiyi boşuna
                // yeniden gönderttirirdi.
                // Eşleştirme sonraki bir gönderimde yeniden denenir; sayaç yalnız
                // gerçekten kaydedileni söyler (kayıt bütün ya da hiç).
                _logger.LogWarning(
                    "Müşteri senkronu: {Count} geriye dönük eşleştirme eşzamanlı bir yazımla çakıştı, kaydedilmedi (lisans {LicenseId})",
                    retroactiveMatches, licenseId);
                _db.ChangeTracker.Clear();
                retroactiveMatches = 0;
            }
        }

        return Ok(new SyncResponse(outcome.Synced + skipped, retroactiveMatches, outcome.Redirects));
    }

    /// <summary>
    /// Partiyi izleyiciye uygular (SaveChanges ÇAĞIRMAZ). Her çağrı satırları
    /// veritabanından TAZE okur; çakışmadan sonraki yeniden deneme bu yüzden
    /// izleyici temizlendikten sonra aynen yeniden çağrılabilir.
    /// </summary>
    private async Task<BatchOutcome> ApplyBatchAsync(
        Guid licenseId, List<SyncItem> items, List<Guid> ids, DateTimeOffset now, CancellationToken ct)
    {
        // IgnoreQueryFilters ŞART: kopyalar varsayılan sorgulardan gizli (A5b).
        // Filtreli kalsa eski sürümün kopya Id'siyle gönderimi "yeni satır"
        // sanılır, aynı birincil anahtarla INSERT denenir → 500 → istemcinin
        // kuyruğu kilitlenir. Aşağıdaki bilinen-kopya yolu bu satırın
        // MergedIntoId'sini görüp veriyi asıl kayda yönlendirir.
        var existing = await _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        // Kimlik araması: bu partide yeni olan Id'lerin (platform, anahtar)
        // asıl kayıtları. Bölüm B'deki tekil indeks kurulana kadar bir kimliğin
        // birden çok asıl kaydı olabilir. Yayıncı satırı varken Shopper'ın
        // açtığı GEÇİCİ satır asıl kayıt seçilmez (A5c: beyan, yayıncı verisinin
        // asıl kaydı olamaz; seçilen geçici satır aşağıda devralınır); sonra en
        // eski UpdatedAt'li olan, eşitlikte küçük Id — sorgu sırasından
        // bağımsız, her istek aynı kaydı seçsin (birleştirme işi sonra hepsini
        // toparlar).
        var newItems = items.Where(c => !existing.ContainsKey(c.Id)).ToList();
        var platforms = newItems.Select(c => KeyOf(c).Platform).Distinct().ToList();
        var keys = newItems.Select(c => KeyOf(c).IdentityKey).Distinct().ToList();
        var canonicalByKey = (await _db.WpfCustomerProjections
                .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null
                    && platforms.Contains(p.Platform) && keys.Contains(p.IdentityKey))
                .ToListAsync(ct))
            .GroupBy(p => (p.Platform, p.IdentityKey))
            .ToDictionary(g => g.Key, g => g
                .OrderBy(p => p.CreatedByShopper).ThenBy(p => p.UpdatedAt).ThenBy(p => p.Id)
                .First());

        // Yönlendirilmiş Id'lerin asıl kayıtları (bilinen kopya yeniden gönderiyor).
        // Hedef de bir kopyaysa (zincir — birleştirme işi düzleştirir, olmamalı)
        // asıl kayıt sayılmaz: kopya satırına asla veri yazılmaz.
        var mergedTargets = existing.Values.Where(p => p.MergedIntoId is not null)
            .Select(p => p.MergedIntoId!.Value).Distinct().ToList();
        var targets = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && mergedTargets.Contains(p.Id) && p.MergedIntoId == null)
            .ToDictionaryAsync(p => p.Id, ct);

        var redirects = new List<SyncRedirect>();
        var newAliases = new List<(Guid From, Guid To)>();
        // Bağlı bağlantıları birleştirmeden SONRA yeniden kanıt isteyen asıl
        // kayıtlar: devralmada açılan yayıncı satırı ve yayıncı yazımı alan
        // geçici satırlar. Id'ye göre — aynı satıra partide birden çok öğe
        // düşerse kanıt bir kez, son durumla.
        var reprove = new Dictionary<Guid, WpfCustomerProjection>();
        var synced = 0;
        var untrustedAliasPushes = 0;

        // DEVRALMA (A5c) — partinin geri kalanından ÖNCE. Yeni bir Id'nin
        // kimliğinin asıl kaydı Shopper'ın açtığı GEÇİCİ kayıtsa (S; silinmiş
        // olsun olmasın): adı/telefonu/adresi kişinin kendi beyanı, bağlantısı
        // kanıtsız — o adla ilk kaydolan saldırgan olabilir. Yayıncı bu kişiyi
        // KENDİ Id'siyle (W) gönderdi: W, yeni bir satır gibi ASIL kayıt olur
        // (yönlendirme yok), S onun kopyasına döner.
        //   - S'nin beyanı boşaltılır (ScrubPersonal; PurgedAt'e dokunmaz — bu
        //     bir KVKK silmesi değil) ve bayrağı KÖKEN olarak kalır: S'yi eski
        //     `since` ingest'iyle indirmiş bilgisayar onu kendi Id'siyle
        //     yankılamayı sürdürür; geçici kökenli kopyanın gönderimi aşağıda
        //     asıl kayda hiç yazılmaz. S asıl kayıt kalsaydı o yankı (damgasız
        //     telefon son gönderimle yazılır) shopper'ın telefonunu geri getirir,
        //     geriye dönük eşleştirme saldırganı yeniden bağlardı.
        //   - S'nin siparişleri, bağlantıları, bakiyesi… W'ye taşınır (kayıttan
        //     önce, RepointReferencesAsync); S'ye yönlenmiş eski kopyalar W'ye
        //     yönlenir — kopyanın kopyası kalmaz. Taşınan bağlantılar W'nin
        //     telefonuna karşı yeniden kanıt ister.
        //   - Silinmiş S de böyle devralınır: silinen shopper'ın beyanıdır,
        //     gerçek müşterinin kaydı değil; yayıncının verisi W'ye iner.
        // Döngüden ÖNCE yapılır: aynı partide S'nin kendi Id'si ya da S'nin bir
        // kopyası nerede gelirse gelsin S'yi kopya olarak görsün — sonuç
        // payload sırasına bağlı kalmasın.
        var takenOver = new List<(Guid From, Guid To)>();
        var takeoverItems = new HashSet<Guid>();
        foreach (var item in newItems)
        {
            var key = KeyOf(item);
            if (!canonicalByKey.TryGetValue(key, out var provisional) || !provisional.CreatedByShopper) continue;

            var created = AddCanonical(licenseId, item);
            provisional.ScrubPersonal();
            provisional.MergedIntoId = created.Id;
            provisional.UpdatedAt = now;
            var olderAliases = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId && p.MergedIntoId == provisional.Id)
                .ToListAsync(ct);
            foreach (var alias in olderAliases)
            {
                alias.MergedIntoId = created.Id;
                alias.UpdatedAt = now;
            }

            canonicalByKey[key] = created; // partide aynı kimliğin sonraki Id'leri W'ye bağlansın
            targets[created.Id] = created; // S ve eski kopyalar artık W'ye yönlü
            reprove[created.Id] = created;
            takenOver.Add((provisional.Id, created.Id));
            takeoverItems.Add(item.Id);
        }

        foreach (var item in items)
        {
            // Devralmada asıl kayıt olarak açıldı (yukarıda).
            if (takeoverItems.Contains(item.Id))
            {
                synced++;
                continue;
            }

            var key = KeyOf(item);

            if (existing.TryGetValue(item.Id, out var current))
            {
                if (current.MergedIntoId is { } canonicalId)
                {
                    if (targets.TryGetValue(canonicalId, out var target))
                    {
                        if (current.CreatedByShopper)
                        {
                            // GEÇİCİ KÖKENLİ kopya (devralınan ya da birleştirilen
                            // Shopper satırı): gönderimi güvenilmez — eski `since`
                            // ingest'i shopper'ın beyanını yayıncı bilgisayarına
                            // sıradan müşteri olarak indirdi, o bilgisayar onu bu
                            // Id'yle geri yankılıyor. Asıl kayda HİÇBİR şey yazılmaz
                            // (boşu doldurmak da yok: boş telefona shopper'ın
                            // telefonu dolsa sonraki "kanıt" kendiliğinden geçerdi).
                            // Bedeli: o bilgisayarda bu Id'yle yapılan düzenleme,
                            // PR-3 satırı yerelde asıl kayda taşıyana kadar
                            // sunucuya ulaşmaz.
                            untrustedAliasPushes++;
                        }
                        else
                        {
                            // Bilinen kopya: istemci henüz yerelde taşımamış. Veri
                            // asıl kayda aynı birim kurallarıyla yazılır (damgalı
                            // yeni değer kazanır, damgasız yalnız damgasız boşu
                            // doldurur). Asıl kayıt geçici olabilir (birleştirme
                            // işinden sonra): kural kendi Id'siyle yazımdakiyle aynı.
                            if (Merge(target, item, viaCopy: true)) target.UpdatedAt = now;
                            NoteProvisionalWrite(target, item, reprove);
                        }
                        // Yönlendirme her durumda (yeniden) söylenir.
                        redirects.Add(new SyncRedirect(item.Id, canonicalId));
                    }
                    else
                    {
                        // Asıl kayıt yok (olmamalı — savunma): kopya satırına ASLA
                        // yazılmaz, yönlendirme de söylenmez (gösterecek asıl kayıt
                        // yok). Sayılır ki istemcinin imleci ilerlesin. Günlüğe
                        // yalnız Id'ler.
                        _logger.LogWarning(
                            "Müşteri senkronu: kopya {AliasId} için asıl kayıt {CanonicalId} bulunamadı (lisans {LicenseId}); öğe yazılmadan sayıldı",
                            item.Id, canonicalId, licenseId);
                    }
                    synced++;
                    continue;
                }

                // Silinmiş kayıt: kişisel alanlara DOKUNMA. Yayıncının kendi
                // bilgisayarındaki kopya silinmediği için (WPF ingest yalnızca
                // yeni satır ekliyor, var olanı güncellemiyor) bu satır her
                // push'ta ad/telefon/adresi geri getirirdi; silme tek bir
                // yayında yorum yazılmasıyla sessizce geri alınırdı.
                // Sayılıyor ama yazılmıyor: istemcinin watermark'ı ilerlesin,
                // aynı parti sonsuza kadar yeniden gönderilmesin.
                if (current.PurgedAt is not null)
                {
                    synced++;
                    continue;
                }

                // Username/Platform mevcut Id'de GÜNCELLENMEZ: WPF bir satırın
                // kullanıcı adını hiç değiştirmiyor (CustomerRepository'de
                // `UPDATE … SET Username` yok) ve değişseydi kimlik anahtarı
                // başka bir asıl kaydın üstüne kayabilirdi.
                if (Merge(current, item, viaCopy: false)) current.UpdatedAt = item.UpdatedAt;
                NoteProvisionalWrite(current, item, reprove);
                synced++;
                continue;
            }

            if (canonicalByKey.TryGetValue(key, out var canonical))
            {
                // Aynı kişi başka bilgisayarda zaten var: bu Id kopya olarak
                // kaydedilir (eski Id'yle geç gelen veri yönlensin diye), veri
                // asıl kayda aynı birim kurallarıyla yazılır. Asıl kayıt burada
                // geçici OLAMAZ: yeni Id'lerin geçici asıl kayıtları yukarıda
                // devralındı.
                _db.WpfCustomerProjections.Add(new WpfCustomerProjection
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    Platform = key.Platform,
                    Username = item.Username!,
                    MergedIntoId = canonical.Id,
                    UpdatedAt = now,
                });
                if (Merge(canonical, item, viaCopy: true)) canonical.UpdatedAt = now;
                redirects.Add(new SyncRedirect(item.Id, canonical.Id));
                newAliases.Add((item.Id, canonical.Id));
                synced++;
                continue;
            }

            canonicalByKey[key] = AddCanonical(licenseId, item); // aynı partide ikinci kopya buna bağlansın
            synced++;
        }

        // Kopya bu bilgisayardan daha önce sipariş/kargo göndermiş olabilir:
        // onlar eski Id'yi taşıyor. Devralınan geçici satıra bağlı her şey de
        // (Shopper bağlantısı, eski ingest'le o Id'ye düşmüş siparişler…) yeni
        // asıl kayda. Aynı kayıt işleminde.
        foreach (var (from, to) in newAliases.Concat(takenOver))
            await _merger.RepointReferencesAsync(licenseId, from, to, ct);

        var linksUnbound = await ReproveLinksAsync(
            licenseId, reprove.Values, takenOver.Select(t => t.From).ToList(), ct);

        return new BatchOutcome(synced, redirects, linksUnbound, takenOver.Count, untrustedAliasPushes);
    }

    /// <summary>Kimlik: (küçük harf platform, kimlik anahtarı). Öğe
    /// IsAcceptable'dan geçti — Platform/Username null değil (`!`).</summary>
    private static (string Platform, string IdentityKey) KeyOf(SyncItem item)
        => (item.Platform!.ToLowerInvariant(), WpfCustomerProjection.IdentityKeyOf(item.Username!));

    /// <summary>Öğeden yeni bir ASIL kayıt açar: kimliğin bu lisanstaki ilk
    /// kaydı ya da devralma (yayıncının satırı geçici kaydın yerine asıl kayıt
    /// olur). Kendi Id'siyle ilk gönderimin kuralı (viaCopy: false).</summary>
    private WpfCustomerProjection AddCanonical(Guid licenseId, SyncItem item)
    {
        var created = new WpfCustomerProjection
        {
            Id = item.Id,
            LicenseId = licenseId,
            Platform = KeyOf(item).Platform,
            Username = item.Username!,
            UpdatedAt = item.UpdatedAt,
        };
        Merge(created, item, viaCopy: false);
        _db.WpfCustomerProjections.Add(created);
        return created;
    }

    /// <summary>
    /// Hâlâ ASIL kayıt olan geçici (Shopper'ın açtığı) satıra yayıncıdan gelen
    /// yazım — kendi Id'siyle ya da yayıncı kökenli bir kopya üzerinden
    /// (devralma ve geçici kökenli kopyanın gönderimi ayrı, bkz.
    /// ApplyBatchAsync). Bağlı bağlantılar her durumda yeniden kanıt ister; aynı
    /// telefonun yankısı kanıtı değiştirmez. Benimseme (bayrağın kalkması) yalnız
    /// DAMGALI telefonla (format 2, PhoneChangedAt dolu): eski <c>since</c>
    /// ingest'i beyanı yayıncı bilgisayarına sıradan müşteri olarak indirip
    /// damgasız geri yankılıyor — yankı benimseme sayılsaydı beyan yayıncı
    /// verisi sayılırdı. Silinmiş satıra yazım olmaz: dokunulmaz.
    /// </summary>
    private static void NoteProvisionalWrite(
        WpfCustomerProjection row, SyncItem item, Dictionary<Guid, WpfCustomerProjection> reprove)
    {
        if (!row.CreatedByShopper || row.PurgedAt is not null) return;
        if (item.Format >= 2 && item.PhoneChangedAt is not null) row.CreatedByShopper = false;
        reprove[row.Id] = row;
    }

    /// <summary>
    /// Verilen asıl kayıtlara bağlı (ayrılmamış) bağlantılar satırın ŞİMDİKİ
    /// telefonuna karşı yeniden kanıt ister
    /// (<see cref="WpfCustomerLinkMatcher.PhoneProves"/>); veremeyen beklemeye
    /// düşer (WpfCustomerId = null). Normal akışlar — geriye dönük eşleştirme,
    /// telefon doğrulaması — yayıncının telefonu eşleşince yeniden bağlar.
    /// SaveChanges ÇAĞIRMAZ: değişiklikler partiyle aynı kayıtta; yeniden
    /// deneme bunu da taze okumayla baştan yapar.
    /// </summary>
    /// <param name="movedFrom">Bağlantıları bu istekte, henüz kaydedilmeden bu
    /// satırlardan birine taşınan Id'ler (devralınan geçici satırlar). Sorgu
    /// veritabanındaki değere bakar ve taşınan bağlantı orada hâlâ eski Id'yi
    /// taşır: onu da arar; karar bağlantının izleyicideki GÜNCEL değerine
    /// (yeni asıl kayıt) göre verilir.</param>
    private async Task<int> ReproveLinksAsync(
        Guid licenseId, IReadOnlyCollection<WpfCustomerProjection> rows,
        IReadOnlyCollection<Guid> movedFrom, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var byId = rows.ToDictionary(r => r.Id);
        var dbIds = byId.Keys.Concat(movedFrom).Select(id => (Guid?)id).ToList();
        var bound = await _db.ShopperBroadcasterLinks
            .Where(l => l.LicenseId == licenseId && l.LeftAt == null && dbIds.Contains(l.WpfCustomerId))
            .Select(l => new { Link = l, l.Shopper.Phone, l.Shopper.PhoneVerifiedAt })
            .ToListAsync(ct);

        var unbound = 0;
        foreach (var row in bound)
        {
            // İzlenen örnek döner (kimlik çözümü): WpfCustomerId GÜNCEL değer.
            if (row.Link.WpfCustomerId is not { } id || !byId.TryGetValue(id, out var projection)) continue;
            if (WpfCustomerLinkMatcher.PhoneProves(projection.Phone, row.Phone, row.PhoneVerifiedAt)) continue;
            row.Link.WpfCustomerId = null;
            unbound++;
        }
        return unbound;
    }

    /// <summary>
    /// Gelen öğenin alanları; düz TCKN burada şifrelenir. Sınır şifrelemeden
    /// ÖNCE düz metne uygulanır, şifreli metin asla kırpılmaz. 11 karakteri
    /// aşan değer bozuktur: TCKN birimi HİÇ GELMEMİŞ sayılır — değer de damga da
    /// düşer. Damga taşınıp değer tutulsaydı sunucu ile istemci aynı damgada
    /// farklı değerde kalır, bir daha hiç eşitlenmezdi. Tek bozuk alan ne
    /// partiyi 400'e düşürüp istemcinin kuyruğunu kilitlesin ne geçerli bir
    /// TCKN'yi silsin.
    /// </summary>
    private CustomerSyncFields FieldsOf(SyncItem item)
    {
        var plain = TcknValidator.Normalize(item.Tckn);
        return plain is { Length: > 11 }
            ? item.ToFields(tcknProtected: null) with { TcknChangedAt = null }
            : item.ToFields(_tckn.Protect(plain)); // Protect(null/boş) → null
    }

    /// <summary>Format 2 birim kurallarıyla yazar (hedef kaydın kendisi de
    /// olabilir, bir kopyanın asıl kaydı da — kural aynı). Format 1: kendi Id'si
    /// için eski sürüm kuralı (ApplyLegacy: dolu telefon/adres son gönderimle
    /// güncellenir); KOPYA Id'siyle gelen eski sürüm gönderimi ise yalnız boşu
    /// doldurur. Yoksa asıl kaydın sahibi bilgisayar da eski sürümse ikisi aynı
    /// kaydı sırayla ezip telefonu/adresi gidip getirirdi — Shopper telefon
    /// kanıtı, İYS aynası ve WhatsApp etiket kuralı bu telefona bakıyor (A3
    /// yeniden incelemesi).</summary>
    private bool Merge(WpfCustomerProjection target, SyncItem item, bool viaCopy) => item.Format >= 2
        ? CustomerFieldMerge.Apply(target, FieldsOf(item))
        : viaCopy
            ? CustomerFieldMerge.Apply(target, new CustomerSyncFields
                { FullName = item.FullName, Phone = item.Phone, Address = item.Address })
            : CustomerFieldMerge.ApplyLegacy(target, item.FullName, item.Phone, item.Address);

    /// <summary>Çakışan varlıkların tür adları — günlük için; kişisel veri
    /// taşımaz.</summary>
    private static string ConflictingEntities(DbUpdateConcurrencyException ex)
        => string.Join(",", ex.Entries.Select(e => e.Metadata.ClrType.Name).Distinct());

    /// <summary>Sunucunun kabul sınırları = kolon sınırları (Platform 32,
    /// Username 128). İstemci aynı sınırları göndermeden önce de uygular
    /// (WpfCustomerProjectionSyncService.IsServerAcceptable).</summary>
    private static bool IsAcceptable(SyncItem c)
        => !string.IsNullOrWhiteSpace(c.Platform) && c.Platform.Length <= 32
        && !string.IsNullOrWhiteSpace(c.Username) && c.Username.Length <= 128;

    /// <summary>Günlüğe yazılan Id sayısının üst sınırı: yeniden verilen bir
    /// lisansın ilk gönderiminde bir partide 500 Id olabilir.</summary>
    private const int MaxLoggedIds = 20;

    /// <summary>Günlük için Id listesi (en çok <see cref="MaxLoggedIds"/>, kalanı
    /// sayıyla) — kişisel veri taşımaz.</summary>
    private static string IdsForLog(IReadOnlyCollection<Guid> ids)
        => ids.Count <= MaxLoggedIds
            ? string.Join(",", ids)
            : string.Join(",", ids.Take(MaxLoggedIds)) + $" (+{ids.Count - MaxLoggedIds})";
}
