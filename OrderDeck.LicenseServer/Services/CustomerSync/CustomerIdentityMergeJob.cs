using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperLinking;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Bir kerelik: aynı kişinin (platform + kimlik anahtarı) birden çok asıl
/// kaydını tek kayıtta toplar. Kuru çalıştırma hiçbir şey yazmaz; Burak'ın
/// onayı için taşınacak kayıtları ve asıl kayıtla kopyalar arasındaki
/// çelişkileri SAYILARLA raporlar (kişisel veri yok).
///
/// <para><b>Gruplama</b> saklı IdentityKey ile değil bellekte hesaplanan
/// <see cref="WpfCustomerProjection.IdentityKeyOf"/> ile: göçün SQL dolgusu ya
/// da geri alınmış bir deploy'un NEWID varsayılanı saklı anahtarı bozmuş
/// olabilir; aynı kişinin satırları yine aynı gruba düşer. Hesaplanan anahtarı
/// boş (yalnız boşluk) kullanıcı adları HARİÇ — onarım işinin atlama kuralıyla
/// aynı; yoksa lisansın bütün boş adlı satırları tek kişi sanılırdı. B1'in
/// kapısı SQL Server'ın kendi kurallarıyla sayar
/// (<see cref="CountDuplicateHeadsAsync"/>); CLI sonunda ikisini de denetler.</para>
///
/// <para><b>Asıl kayıt:</b> Shopper'ın açtığı geçici kayıt, yayıncının satırı
/// varken asıl olamaz (A5c); sonra en erken siparişi olan; hiçbirinin siparişi
/// yoksa en eski UpdatedAt; eşitlikte küçük Id ("N" metni). Projeksiyonda
/// oluşturma zamanı tutulmadığı için "en eski kayıt"ın tek dürüst ölçüsü bu.</para>
///
/// <para><b>Alanlar</b> istemci gönderimiyle AYNI birim kurallarıyla aktarılır
/// (<see cref="CustomerFieldMerge.Apply"/>). Bugünkü satırların hiçbirinde
/// damga yok; pratikte doldurma kipi: kopyalar UpdatedAt'i en yeniden eskiye
/// (eşitlikte Id sırasıyla — sonuç sorgunun dönüş sırasına kalmasın) gezilir,
/// asıl kayıtta boş olan alan ilk dolu değerle doldurulur (adres bloğu bütün
/// olarak; eski sürümün takma ad yedeği kullanıcı adıyla tanınır). İki tarafta
/// da dolu ve farklı olan değerde asıl kaydınki kalır; ötekiler birleştirme
/// öncesi yedekte — kuru çalıştırma bunları alan başına grup sayısıyla gösterir.
/// Her kopya alan kaynağıdır, iki süzgeçle: geçici kopyadan yalnız DAMGALI
/// birimler (<see cref="CustomerSyncFields.StampedOnly"/> — damgasız alanları
/// kaydolanın kendi beyanı; boş telefonu doldursa kanıt kendiliğinden geçerdi),
/// silinmiş (PurgedAt) kopyanın boşaltılmış kişisel birimleri hiç
/// (<see cref="CustomerSyncFields.WithoutScrubbedUnits"/> — oradaki "damgalı
/// boş" bilinçli silme değil). Kara liste ve iş notu gibi yayıncı kararları
/// ikisinden de kaybolmaz. Herhangi bir (geçici olmayan) kopya KVKK ile
/// silinmişse kişinin tamamı silinmiş sayılır; ilk silme tarihi açıkça en
/// erkeni. Geçici satırın silinmişliği kişiye YAYILMAZ: sahte bir hesabın KVKK
/// silmesi gerçek müşterinin asıl kaydını silmesin.</para>
///
/// <para><b>Asıl kaydın UpdatedAt'i ilerletilmez</b> — yalnız kişi bu koşuda
/// silinmiş sayıldıysa (mezar taşı eski istemcilere ulaşsın). Eski
/// istemcilerin <c>since</c> ingest'i kullanıcı adını HARF DUYARLI eşler:
/// ilerleyen UpdatedAt asıl kaydı, yalnız harf farklı kopyayı tutan bilgisayara
/// YENİ bir yerel müşteri olarak indirirdi. Değişiklik akışı rowversion
/// kullanır, etkilenmez. Asıl kaydın eşzamanlılık jetonu (PurgedAt) yine
/// denetlenir: arada silinen asıl kayıt grubu geri aldırır.</para>
///
/// <para><b>Kopya</b> silinmez: kişisel alanları boşaltılır (PurgedAt'e
/// dokunulmaz — eşzamanlılık jetonu), MergedIntoId = asıl kayıt; geçici
/// kopyanın CreatedByShopper bayrağı köken olarak kalır; UpdatedAt'i KORUNUR
/// (elle PR-1 öncesi imaja dönülürse eski <c>since</c> onu yeniden dağıtmasın).
/// Ona zaten yönlenmiş kopyalar da asıl kayda yönlendirilir (zincir olmaz;
/// kalan zinciri <see cref="CountChainsAsync"/> sayar, CLI son koşulu). Kopyaya bağlı
/// sipariş/kargo/bakiye/bağlantılar <see cref="CustomerIdentityMerger"/> ile
/// taşınır; her kopya DOĞRUDAN asıl kayda (zincirleme birleştirme tek
/// SaveChanges içinde yapılmaz — birleştiricinin bakiye araması buna dayanır).</para>
///
/// <para><b>Geçici kopyadan taşınan bağlantı</b> — AYRILMIŞ olanı dahil:
/// ShopperPurgeService ayrılmış bağlantıdan ulaştığı projeksiyonu da siler,
/// kanıtsız kalsa saldırganın KVKK silme talebi asıl kaydı silerdi — asıl
/// kaydın telefonuyla yeniden kanıt ister
/// (<see cref="WpfCustomerLinkMatcher.PhoneProves"/>); veremeyen beklemeye düşer
/// (asıl kayıt da geçiciyse hepsi) — normal kanıt akışları yeniden bağlar. Kanıt
/// grubun TÜM kopyaları işlendikten ve silme uygulandıktan SONRA, kaydedilecek
/// telefona karşı verilir: kopya döngüsünün ara durumuna karşı verilseydi sonuç
/// kopyaların işlenme sırasına bağlı kalırdı — sonradan işlenen bir kopyanın
/// damgalı telefonu kanıtı geçmiş bağlantıyı başka numaralı kayda bağlı
/// bırakırdı, silinen kişinin kaydına da (sync ucunun ReproveLinksAsync'i gibi:
/// parti bittikten sonra, şimdiki telefona karşı).</para>
///
/// <para>Grup başına ayrı işlem. Eşzamanlılık çakışması (bu arada bir KVKK
/// silmesi, bakiye uygulaması, sipariş senkronu) ya da başka bir veritabanı
/// hatası (kilitlenme 1205, zaman aşımı, eşzamanlı bakiye uygulamasının açtığı
/// satıra 2601 …) o grubu geri alır ve sayılır; günlüğe yalnız istisna tipi ve
/// SQL hata numarası yazılır (iletisi tekil anahtar değerini — telefon olabilir
/// — taşıyabilir). İş sıradaki gruba geçer; yeniden koşmak yalnız kalanları
/// bulur (idempotent). Rapor sayıları (FailedGroups ve LinksUnbound hariç)
/// uygulamada da "bulunan" sayılardır: atlanan grubun satırları da
/// içlerindedir.</para>
///
/// <para><b>"@" ikizi kipi</b> (<c>atTwins</c>; CLI <c>--at-twins</c>,
/// 2026-10-08): 2026-08-05'ten v0.9.9'a dek Instagram resmi API yolu kullanıcı
/// adını "@ad" diye yazdı; form ve eski eklenti "ad" yazıyordu. Kimlik
/// anahtarları farklı olduğundan aynı kişinin İKİ asıl kaydı oldu (prod'da 512
/// Instagram kişisi) — olağan kip onları aynı kişi saymaz. v0.9.9 "@"sız yazar
/// ve bilgisayardaki arama öbür yazıma düşer; ikizleri tek kayıtta toplayan bu
/// kiptir. Yalnız Instagram/TikTok/Facebook (<see cref="AtTwinPlatforms"/> —
/// orada '@' adın parçası değil; YouTube hiç gruplanmaz); grup anahtarı baştaki
/// '@'leri atılmış kimlik anahtarı (<see cref="AtTwinKeyOf"/>). Asıl kayıt
/// "@"SIZ yazım: form ve yeni yorumlar onu kullanır — "@"lı satır asıl kalsaydı
/// "ad" anahtarının asıl kaydı olmaz, sonraki gönderim aynı kişiye yeniden ayrı
/// bir asıl kayıt açardı. Sıra: geçici satır kuralı (A5c) yine önce; sonra CANLI
/// satır (silinmiş asıl kayıt bilgisayarlarda hiç açılmaz — canlı "@" kopyasından
/// ona yönlendirme "@"sız satırı görmemiş bilgisayarda yok sayılır ve kişisel veri
/// orada kalırdı; canlı asıl kaydı ise silinmişlik kuralı boşaltır ve silme mezar
/// taşı olarak iner); sonra "@"sız yazım; sonra olağan sıra. "@"lı asıl kayıtla
/// biten grup ayrıca sayılır (<see cref="Report.AtSpelledCanonicals"/>). Geri
/// kalan her şey (alan aktarımı, boşaltma, başvuruların taşınması, zincir,
/// silinmişlik, yeniden kanıt) olağan kiple AYNI. Bilgisayarlar yönlendirmeyi
/// (kopya → asıl kayıt) değişiklik akışından alır ve yerel başvurularını (etiket,
/// kargo, ödeme işi) asıl kayda taşır.</para>
///
/// <para><b>Grup birliği</b> (yalnız "@" ikizi kipi): ikizler FARKLI kişi
/// gruplarındaysa birim kuralı ya asıl kaydı kopyanın grubuna taşır (damgalı
/// kopya) ya da kopyanın grubunu kimliksiz bırakır (ikisi damgasız) — kişinin
/// grubu her bilgisayarda bölünür ve sonradan <c>group-customers</c> da onaramaz
/// (çiftler "zaten birlikte" görünür). Bu yüzden grubun işleminde, masaüstündeki
/// elle birleştirme (<c>MergeIntoGroup</c>) gibi bütün numaraların üyeleri
/// toplanır: hedef asıl kaydın birleştirme ÖNCESİ numarası, yoksa kopyanınki;
/// asıl kayıt hedefe yazılıp kopyalarınkinden yeni damgayla damgalanır, öbür
/// numaraların asıl ve silinmemiş bütün üyeleri hedefe taşınır, son grupta kara
/// liste yayılır (<see cref="CustomerGroupUnion"/> — group-customers ile ORTAK
/// kod). Olağan kipte YOK: o kip E2 olarak incelenip prod'da koştu ve B1 tekil
/// indeksinden beri bulacak grubu yok; değişmesi bir kazanç getirmeden incelenmiş
/// bir yolu değiştirirdi. Olağan kip numara farkını yalnız sayar
/// (<see cref="Report.GroupConflicts"/>).</para>
/// </summary>
public sealed class CustomerIdentityMergeJob
{
    /// <summary>B1 tekil indeksinin kapısı, BİREBİR: indeksin kapsadığı satırlar
    /// (<see cref="CustomerIdentityIndex.Filter"/> — asıl kayıt, boş olmayan
    /// anahtar) arasında (LicenseId, Platform, IdentityKey) yinelenen grup sayısı
    /// — SQL Server'ın kendi karşılaştırma kurallarıyla (Platform harf duyarsız,
    /// sondaki boşluk yok sayılır; IdentityKey BIN2). İşin bellekteki
    /// gruplamasından geniştir: o bir şeyi kaçırırsa bu yakalar. B1 göçünün
    /// (<c>CustomerProjectionUniqueIdentity</c>) kapısı bu metnin BAYT BAYT
    /// kopyasını koşar — operatörün gördüğü sayı göçün denetlediğidir. İkisi EŞİT
    /// kalmalı: biri değişirse öteki de (göç tarihsel olduğu için sabite
    /// bağlanmadı); kaymayı CustomerProjectionUniqueIdentityMigrationTests
    /// yakalar.</summary>
    public const string DuplicateHeadsSql =
        "SELECT COUNT(*) FROM (SELECT 1 x FROM WpfCustomerProjections WHERE MergedIntoId IS NULL AND IdentityKey <> N'' GROUP BY LicenseId, Platform, IdentityKey HAVING COUNT(*) > 1) d";

    /// <summary>Zincir: kopyası da kopya olan satır (tüm lisanslar). İş
    /// birleştirdiği grubun eski kopyalarını düzleştirir; geriye kalan zincir
    /// bir yarışın izidir (--apply sırasında açık kalan bir bilgisayar). Sync ucu
    /// zinciri sınırlı adımda çözer ama B1 öncesi son koşul sıfır ister.</summary>
    public const string ChainsSql =
        "SELECT COUNT(*) FROM WpfCustomerProjections a JOIN WpfCustomerProjections b ON a.MergedIntoId = b.Id WHERE b.MergedIntoId IS NOT NULL";

    /// <summary>"@" ikizi kipinin gezdiği platformlar (küçük harf): '@' adın
    /// parçası değil — masaüstündeki <c>CustomerIdentity</c>'nin "@"sız tuttuğu
    /// üç platformla aynı. YouTube yok: sohbet satırının adı kanal kimliği,
    /// "@tanıtıcı" ayrı bir şey.</summary>
    public static readonly IReadOnlySet<string> AtTwinPlatforms =
        new HashSet<string>(["instagram", "tiktok", "facebook"], StringComparer.Ordinal);

    /// <summary>"@" ikizi kipinin grup anahtarı: kimlik anahtarının
    /// (<see cref="WpfCustomerProjection.IdentityKeyOf"/>) baştaki '@'leri
    /// atılmış hâli. Geriye bir şey kalmazsa (ad yalnız '@'lerden oluşuyorsa)
    /// kırpılmamış anahtar: yoksa "@", "@@" ve bütün böyle adlar tek kişi
    /// sayılırdı. Boş anahtar (yalnız boşluk) boş kalır — olağan kipteki gibi
    /// gruplanmaz.</summary>
    public static string AtTwinKeyOf(string username)
    {
        var key = WpfCustomerProjection.IdentityKeyOf(username);
        var bare = key.TrimStart('@');
        return bare.Length == 0 ? key : bare;
    }

    private readonly LicenseDbContext _db;
    private readonly CustomerIdentityMerger _merger;
    private readonly ILogger<CustomerIdentityMergeJob> _log;

    public CustomerIdentityMergeJob(
        LicenseDbContext db, CustomerIdentityMerger merger, ILogger<CustomerIdentityMergeJob>? log = null)
    {
        _db = db;
        _merger = merger;
        _log = log ?? NullLogger<CustomerIdentityMergeJob>.Instance;
    }

    /// <param name="Groups">Bulunan kopyalı kişi sayısı (kuru çalıştırmada ve
    /// uygulamada aynı anlam).</param>
    /// <param name="FailedGroups">Geri alınıp atlanan grup (eşzamanlılık ya da
    /// başka bir veritabanı hatası); yeniden koşu tamamlar.</param>
    /// <param name="LinksUnbound">Geçici kopyadan taşınıp telefon kanıtını
    /// veremediği için beklemeye düşen Shopper bağlantısı (ayrılmışlar dahil);
    /// yalnız uygulamada ve yalnız kaydedilen gruplardan.</param>
    public sealed record Report(
        int Groups, int CopyRows, int OrdersToMove, int ShipmentsToMove,
        int LinksToMove, int BalancesToSum, int PurgedGroups, int FailedGroups, int LinksUnbound = 0)
    {
        /// <summary>Platform (küçük harf) başına kopyalı kişi; toplamı <see cref="Groups"/>.</summary>
        public IReadOnlyDictionary<string, int> GroupsByPlatform { get; init; } = ImmutableDictionary<string, int>.Empty;

        /// <summary>Kullanıcı adının ordinal olarak birden çok farklı yazımı olan
        /// grup (harf ya da kenar boşluğu farkı).</summary>
        public int VariantGroups { get; init; }

        // Asıl kayıt ile en az bir kopyada İKİSİ de dolu (boşluktan ibaret değil)
        // ama farklı (CustomerFieldMerge.SameText) olan grup sayıları — bu
        // değerlerde asıl kaydınki kalır.
        public int PhoneConflicts { get; init; }
        /// <summary>Adres bloğu bütün olarak: iki blok da dolu ve birleştirme
        /// kopyanınkini aynı adres saymıyor (bir parça iki tarafta dolu ve farklı,
        /// ya da iki tarafta dolu ortak parça yok — CustomerFieldMerge'in blok
        /// doldurma kuralı).</summary>
        public int AddressConflicts { get; init; }
        public int EmailConflicts { get; init; }
        public int NameConflicts { get; init; }
        public int NotesConflicts { get; init; }
        /// <summary>Satırları (asıl kayıt + kopyalar) birden çok FARKLI grup
        /// numarası taşıyan grup (kırpılmış, ORDİNAL — numara bir kimlik, metin
        /// değil; bilgisayarlar da onu bayt bayt karşılaştırır).
        /// <para>"@" ikizi kipinde bu gruplar BİRLEŞTİRİLİR: numaraların bütün
        /// üyeleri tek numarada toplanır (bkz. sınıf dokümanı, "Grup birliği") —
        /// sayı "birleştirilen grup" demektir. Olağan kipte yalnız sayılır: hangi
        /// numaranın kalacağını öteki alanlardaki gibi birim kuralı
        /// (<see cref="CustomerFieldMerge.Apply"/>, damga) seçer, kaybeden
        /// numaranın öbür üyeleri o grupta kalır.</para></summary>
        public int GroupConflicts { get; init; }

        /// <summary>"@" ikizi kipi: seçilen asıl kaydı "@"lı yazım olan grup —
        /// "@"sız satır geçici (Shopper'ın açtığı) ya da silinmiş, ya da grupta
        /// hiç yok (ör. "@ad" ile "@@ad"). Bu kişilerin "ad" anahtarının asıl kaydı
        /// yok: sonraki "@"sız gönderim yeniden ayrı kayıt açabilir. Olağan kipte
        /// hep 0.</summary>
        public int AtSpelledCanonicals { get; init; }

        // Kopyalardan asıl kayda taşınacak öteki kayıtlar (CustomerIdentityMerger'in
        // taşıdıklarıyla aynı yüklemler).
        public int BalanceTransactionsToMove { get; init; }
        public int IbanMemoriesToMove { get; init; }
        public int PaymentMatchesToMove { get; init; }
        public int WaConversationsToMove { get; init; }
    }

    /// <summary>Projeksiyonu olan lisanslar (CLI <c>--all</c>).</summary>
    public async Task<IReadOnlyList<Guid>> LicenseIdsAsync(CancellationToken ct)
        => await _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Select(p => p.LicenseId).Distinct().OrderBy(x => x).ToListAsync(ct);

    /// <summary>Kimlik anahtarı hesaplananla uyuşmayan satır sayısı — onarım
    /// işinin yüklemiyle BİREBİR (boş hesaplanan anahtar sayılmaz: iş onu asla
    /// yazmaz). Kopyalar dahil, tüm lisanslar.</summary>
    public async Task<int> CountMismatchedKeysAsync(CancellationToken ct)
    {
        var rows = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Select(p => new { p.Username, p.IdentityKey })
            .ToListAsync(ct);
        return rows.Count(r =>
        {
            var key = WpfCustomerProjection.IdentityKeyOf(r.Username);
            return key != "" && key != r.IdentityKey;
        });
    }

    /// <summary><see cref="DuplicateHeadsSql"/>'i koşturur: tüm lisanslarda
    /// B1'in tekil indeksine çarpacak yinelenen asıl kayıt grubu sayısı.</summary>
    public Task<int> CountDuplicateHeadsAsync(CancellationToken ct) => CountAsync(DuplicateHeadsSql, ct);

    private async Task<int> CountAsync(string sql, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary><see cref="ChainsSql"/>'i koşturur: tüm lisanslarda kopyası da
    /// kopya olan satır sayısı.</summary>
    public Task<int> CountChainsAsync(CancellationToken ct) => CountAsync(ChainsSql, ct);

    /// <param name="atTwins">"@" ikizi kipi (bkz. sınıf dokümanı): yalnız
    /// <see cref="AtTwinPlatforms"/>, anahtar <see cref="AtTwinKeyOf"/>, asıl
    /// kayıt "@"sız yazım.</param>
    public async Task<Report> RunAsync(Guid licenseId, bool apply, CancellationToken ct, bool atTwins = false)
    {
        var groups = await FindGroupsAsync(licenseId, atTwins, ct);
        var tally = new Tally();
        foreach (var group in groups)
        {
            try
            {
                await MergeGroupAsync(licenseId, group.Ids, apply, atTwins, tally, ct);
            }
            catch (DbUpdateException ex) // DbUpdateConcurrencyException dahil
            {
                Skipped(licenseId, ex, tally);
            }
            catch (SqlException ex)
            {
                Skipped(licenseId, ex, tally);
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        return new Report(groups.Count, tally.Copies, tally.Orders, tally.Shipments, tally.Links,
            tally.Balances, tally.Purged, tally.Failed, tally.Unbound)
        {
            GroupsByPlatform = groups.GroupBy(g => g.Platform)
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            VariantGroups = groups.Count(g => g.Variant),
            PhoneConflicts = tally.PhoneConflicts,
            AddressConflicts = tally.AddressConflicts,
            EmailConflicts = tally.EmailConflicts,
            NameConflicts = tally.NameConflicts,
            NotesConflicts = tally.NotesConflicts,
            GroupConflicts = tally.GroupConflicts,
            AtSpelledCanonicals = tally.AtSpelledCanonicals,
            BalanceTransactionsToMove = tally.BalanceTransactions,
            IbanMemoriesToMove = tally.IbanMemories,
            PaymentMatchesToMove = tally.PaymentMatches,
            WaConversationsToMove = tally.WaConversations,
        };
    }

    /// <summary>Lisansta bugün kaç kopyalı kişi (grup) var — <see cref="RunAsync"/>
    /// ile AYNI gruplama. CLI "@" ikizi kipinin son koşulu: uygulamadan sonra 0
    /// olmalı.</summary>
    public async Task<int> CountGroupsAsync(Guid licenseId, bool atTwins, CancellationToken ct)
        => (await FindGroupsAsync(licenseId, atTwins, ct)).Count;

    private sealed record Group(string Platform, List<Guid> Ids, bool Variant);

    /// <summary>Gruplama (bkz. sınıf dokümanı): asıl kayıtlar, bellekte hesaplanan
    /// anahtarla; boş anahtar hariç; "@" ikizi kipinde yalnız
    /// <see cref="AtTwinPlatforms"/> ve <see cref="AtTwinKeyOf"/>.</summary>
    private async Task<List<Group>> FindGroupsAsync(Guid licenseId, bool atTwins, CancellationToken ct)
    {
        var heads = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null)
            .Select(p => new { p.Id, p.Platform, p.Username })
            .ToListAsync(ct);
        return heads
            .Select(h => new
            {
                h.Id, h.Username, Platform = h.Platform.ToLowerInvariant(),
                Key = atTwins ? AtTwinKeyOf(h.Username) : WpfCustomerProjection.IdentityKeyOf(h.Username),
            })
            .Where(h => h.Key != "" && (!atTwins || AtTwinPlatforms.Contains(h.Platform)))
            .GroupBy(h => (h.Platform, h.Key))
            .Where(g => g.Count() > 1)
            .Select(g => new Group(
                g.Key.Platform,
                g.Select(h => h.Id).ToList(),
                g.Select(h => h.Username).Distinct(StringComparer.Ordinal).Count() > 1))
            .ToList();
    }

    /// <summary>Bir grubu sayar, <paramref name="apply"/> ise kendi işleminde
    /// birleştirir. Sayımlar ancak grubun bütün okumaları bittikten sonra
    /// toplanır: okuma yarıda düşen grup yarım sayılmaz. Hata çağırana çıkar —
    /// işlem kapsamdan çıkarken geri alınır.</summary>
    private async Task MergeGroupAsync(
        Guid licenseId, List<Guid> ids, bool apply, bool atTwins, Tally tally, CancellationToken ct)
    {
        var rows = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && ids.Contains(p.Id))
            .ToListAsync(ct);
        if (rows.Count < 2)
            return; // bu arada başka bir koşu birleştirdi

        var hexes = rows.Select(r => r.Id.ToString("N")).ToList();
        var orderStats = (await _db.Orders
                .Where(o => o.LicenseId == licenseId && hexes.Contains(o.CustomerId))
                .GroupBy(o => o.CustomerId)
                .Select(g => new { CustomerId = g.Key, First = g.Min(o => o.AddedAt), Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.CustomerId);

        var canonical = rows
            // Shopper'ın açtığı geçici kayıt asıl olamaz, yayıncının satırı varken (A5c).
            .OrderBy(r => r.CreatedByShopper ? 1 : 0)
            // "@" ikizi kipi: önce CANLI satır. Silinmiş asıl kayıt bilgisayarlarda
            // hiç açılmaz; canlı "@" kopyasından ona yönlendirme, "@"sız satırı
            // hiç görmemiş bilgisayarda yok sayılır ve kişisel veri orada kalırdı.
            // Canlı asıl kayıtta ise aşağıdaki silinmişlik kuralı onu boşaltır,
            // silme bilgisayarlara mezar taşı olarak ulaşır (v0.9.9 iki yazıma da
            // uygular).
            .ThenBy(r => atTwins && r.PurgedAt is not null ? 1 : 0)
            // "@" ikizi kipi: "@"sız yazım asıl kayıt — form ve yeni yorumlar onu
            // kullanır (bkz. sınıf dokümanı). Siparişin daha eski olması da onu
            // geçmez: asıl kayıt "@"lı kalsaydı "ad" anahtarının asıl kaydı olmazdı.
            .ThenBy(r => atTwins && r.Username.Trim().StartsWith('@') ? 1 : 0)
            .ThenBy(r => orderStats.TryGetValue(r.Id.ToString("N"), out var s) ? s.First : DateTimeOffset.MaxValue)
            .ThenBy(r => r.UpdatedAt)
            .ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal)
            .First();
        // Eşitlikte Id: sıra sorgunun dönüş sırasına kalmasın (alan doldurma ilk dolu değeri alır).
        var others = rows.Where(r => r.Id != canonical.Id)
            .OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id).ToList();
        var otherIds = others.Select(r => r.Id).ToList();
        var otherHexes = others.Select(r => r.Id.ToString("N")).ToList();

        var shipments = await _db.Shipments
            .CountAsync(s => s.LicenseId == licenseId && otherHexes.Contains(s.CustomerId), ct);
        var links = await _db.ShopperBroadcasterLinks
            .CountAsync(l => l.LicenseId == licenseId && l.WpfCustomerId != null && otherIds.Contains(l.WpfCustomerId.Value), ct);
        var balances = await _db.CustomerBalances
            .CountAsync(b => b.LicenseId == licenseId && otherIds.Contains(b.WpfCustomerId) && b.Balance != 0m, ct);
        var balanceTransactions = await _db.CustomerBalanceTransactions
            .CountAsync(t => t.LicenseId == licenseId && otherIds.Contains(t.WpfCustomerId), ct);
        var ibanMemories = await _db.CustomerIbanMemories
            .CountAsync(m => m.LicenseId == licenseId && otherIds.Contains(m.WpfCustomerId), ct);
        var paymentMatches = await _db.PaymentMatches
            .CountAsync(m => m.LicenseId == licenseId
                && ((m.ProposedWpfCustomerId != null && otherIds.Contains(m.ProposedWpfCustomerId.Value))
                    || (m.ActualWpfCustomerId != null && otherIds.Contains(m.ActualWpfCustomerId.Value))), ct);
        var waConversations = await _db.WaConversations
            .CountAsync(c => c.LicenseId == licenseId && c.WpfCustomerId != null && otherIds.Contains(c.WpfCustomerId.Value), ct);
        // Geçici (Shopper'ın açtığı) satırın silinmişliği kişiye YAYILMAZ:
        // o satır kişinin değil kaydolanın beyanı — sahte bir hesabın KVKK
        // silmesi gerçek müşterinin asıl kaydını silmesin (A5c).
        var anyPurged = rows.Any(r => r.PurgedAt is not null && !r.CreatedByShopper);

        tally.Copies += others.Count;
        tally.Orders += others.Sum(r => orderStats.TryGetValue(r.Id.ToString("N"), out var s) ? s.Count : 0);
        tally.Shipments += shipments;
        tally.Links += links;
        tally.Balances += balances;
        tally.BalanceTransactions += balanceTransactions;
        tally.IbanMemories += ibanMemories;
        tally.PaymentMatches += paymentMatches;
        tally.WaConversations += waConversations;
        if (others.Any(c => Conflicting(canonical.Phone, c.Phone))) tally.PhoneConflicts++;
        if (others.Any(c => AddressConflicting(canonical, c))) tally.AddressConflicts++;
        if (others.Any(c => Conflicting(canonical.Email, c.Email))) tally.EmailConflicts++;
        if (others.Any(c => Conflicting(canonical.FullName, c.FullName))) tally.NameConflicts++;
        if (others.Any(c => Conflicting(canonical.Notes, c.Notes))) tally.NotesConflicts++;
        if (rows.Select(r => CustomerGroupUnion.KeyOf(r.GroupId)).OfType<string>()
                .Distinct(StringComparer.Ordinal).Count() > 1) tally.GroupConflicts++;
        if (atTwins && canonical.Username.Trim().StartsWith('@')) tally.AtSpelledCanonicals++;
        if (anyPurged) tally.Purged++;

        if (!apply)
            return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var becomesPurged = anyPurged && canonical.PurgedAt is null;
        // Grup birliği ("@" ikizi kipi) asıl kaydın birleştirme ÖNCESİ numarasını hedefler.
        var groupBefore = canonical.GroupId;
        // Geçici kopyalardan taşınan bağlantılar (ayrılmışlar dahil):
        // kanıt aşağıda, bütün kopyalar ve silme uygulandıktan SONRA.
        var provisionalLinks = new List<ShopperBroadcasterLink>();
        foreach (var copy in others)
        {
            // Her kopya alan kaynağıdır, iki süzgeçle: Shopper'ın açtığı
            // geçici kopyadan yalnız DAMGALI birimler (yayıncının kararı;
            // damgasız birim kaydolanın kendi beyanı — boş telefonu doldursa
            // kanıt kendiliğinden geçerdi); silinmiş kopyanın boşaltılmış
            // kişisel birimleri hiç (oradaki "damgalı boş" bilinçli silme
            // değil). Kara liste ve not gibi yayıncı kararları böylece
            // ikisinden de kaybolmaz.
            var fields = CustomerSyncFields.From(copy);
            if (copy.CreatedByShopper) fields = fields.StampedOnly();
            if (copy.PurgedAt is not null) fields = fields.WithoutScrubbedUnits();
            CustomerFieldMerge.Apply(canonical, fields);
            // Taşımadan ÖNCE yüklenir (izlenen örnekler): birleştirici aynı
            // örneklerin WpfCustomerId'sini asıl kayda çevirir. Ayrılmış
            // bağlantılar DAHİL: birleştirici onları da taşır ve
            // ShopperPurgeService ayrılmış bağlantıdan ulaştığı projeksiyonu
            // da siler — kanıtsız kalsa saldırganın KVKK silme talebi asıl
            // kaydı silerdi.
            if (copy.CreatedByShopper)
                provisionalLinks.AddRange(await _db.ShopperBroadcasterLinks
                    .Where(l => l.LicenseId == licenseId && l.WpfCustomerId == copy.Id)
                    .Include(l => l.Shopper)
                    .ToListAsync(ct));
            // Kopyanın boşaltılması silme kararı DEĞİL: PurgedAt'e dokunulmaz
            // (eşzamanlılık jetonu — aç/kapa yapmak eşzamanlı bir purge'ü ezerdi).
            // Kopyaya dönen satırın UpdatedAt'i KORUNUR (zincirden düzleştirilenler
            // de): PR-1 tarafında okuyan yok; elle PR-1 öncesi bir imaja dönülürse
            // eski `since` (kopya filtresi yok) yeni zamanlı kopyaları bütün
            // bilgisayarlara ikinci müşteri olarak yeniden dağıtırdı (bkz.
            // deploy/README "PR-1 geri dönüş tabanı"). Satır yine yazılır:
            // rowversion ilerler, değişiklik akışı yönlendirmeyi taşır.
            copy.ScrubPersonal();
            copy.MergedIntoId = canonical.Id;

            var chained = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId && p.MergedIntoId == copy.Id)
                .ToListAsync(ct);
            foreach (var alias in chained)
                alias.MergedIntoId = canonical.Id;

            await _merger.RepointReferencesAsync(licenseId, copy.Id, canonical.Id, ct);
        }
        if (anyPurged)
        {
            var purgedAt = rows.Where(r => r.PurgedAt is not null && !r.CreatedByShopper).Min(r => r.PurgedAt!.Value);
            // Asıl kayıt zaten silinmişse kendi (belki daha geç) tarihini
            // korumaz: ilk silme tarihi açıkça en erkeni.
            canonical.ScrubPersonal();
            canonical.PurgedAt = canonical.PurgedAt is { } p && p < purgedAt ? p : purgedAt;
        }
        if (atTwins)
            await UnionGroupsAsync(licenseId, canonical, groupBefore, others, now, ct);
        // Kaydedilecek telefona karşı (bkz. sınıf dokümanı).
        var groupUnbound = 0; // yalnız işlem başarılıysa toplama eklenir
        foreach (var link in provisionalLinks)
        {
            if (canonical.CreatedByShopper
                || !WpfCustomerLinkMatcher.PhoneProves(canonical.Phone, link.Shopper.Phone, link.Shopper.PhoneVerifiedAt))
            {
                link.WpfCustomerId = null; // beklemeye: normal kanıt akışları yeniden bağlar
                groupUnbound++;
            }
        }
        // UpdatedAt yalnız kişi bu koşuda silindiyse (bkz. sınıf dokümanı). Asıl
        // kayıt yine de her koşulda yazılır: PurgedAt "değişti" işaretlenir ki
        // UPDATE'in WHERE'i jetonu taşısın — alanı değişmeyen asıl kayıt hiç
        // yazılmasaydı arada gelen bir KVKK silmesi fark edilmez, birleştirme
        // ve yeniden kanıt silinmiş kayda bayat telefonla uygulanırdı.
        if (becomesPurged) canonical.UpdatedAt = now;
        _db.Entry(canonical).Property(c => c.PurgedAt).IsModified = true;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        tally.Unbound += groupUnbound;
    }

    /// <summary>
    /// "@" ikizi kipinin grup birliği (bkz. sınıf dokümanı, "Grup birliği"):
    /// grubun işleminde, alan aktarımından ve silinmişlikten SONRA. Hedef asıl
    /// kaydın birleştirme ÖNCESİ (kırpılmış) numarası, yoksa kopyaların ordinal en
    /// küçüğü. Asıl kayıt hedefe yazılır ve damgalanır — numara birden çoksa ya da
    /// değeri değiştiyse; hedef dışındaki numaraların bütün asıl, silinmemiş
    /// üyeleri hedefe taşınır; son grupta kara liste yayılır
    /// (<see cref="CustomerGroupUnion"/>, group-customers ile ORTAK).
    /// </summary>
    private async Task UnionGroupsAsync(
        Guid licenseId, WpfCustomerProjection canonical, string? groupBefore,
        IReadOnlyList<WpfCustomerProjection> copies, DateTimeOffset now, CancellationToken ct)
    {
        var copyGroups = copies.Select(c => CustomerGroupUnion.KeyOf(c.GroupId)).OfType<string>().ToList();
        var target = CustomerGroupUnion.KeyOf(groupBefore)
                     ?? copyGroups.Order(StringComparer.Ordinal).FirstOrDefault();
        if (target is null) return; // kimsenin grubu yok

        var groups = new HashSet<string>(copyGroups, StringComparer.Ordinal) { target };
        if (CustomerGroupUnion.KeyOf(canonical.GroupId) is { } merged) groups.Add(merged);
        var canonicalChanges = !string.Equals(canonical.GroupId, groupBefore, StringComparison.Ordinal)
                               || !string.Equals(canonical.GroupId, target, StringComparison.Ordinal);
        if (groups.Count == 1 && !canonicalChanges)
            return; // tek numara ve asıl kayıt zaten onda: birleştirilecek bir şey yok

        // Asıl kaydın damgası kopyalarınkinden de yeni olmalı: bilgisayar
        // yönlendirmede kopyanın yerel satırını asıl kayda birim kuralıyla katar
        // (RekeyCore) — kopyanın damgalı numarası yerelde kazanır, gönderilir ve
        // birliği yeniden bölerdi; damgasız numara da yerelde boş asıl kaydı
        // doldururdu. Değeri değişmese de damgalanır (birden çok numara varken).
        var newest = copies.Select(c => c.GroupIdChangedAt).Append(canonical.GroupIdChangedAt).Max();
        canonical.GroupId = target;
        canonical.GroupIdChangedAt = CustomerGroupUnion.StampAt(now, newest);

        // Üyeler bu işlemde izlenen örnekler: asıl kayıt ve kopyalar zaten izleniyor
        // (kopyalar bellekte kopya — yükleyici onları süzer).
        var members = await CustomerGroupUnion.LoadGroupRowsAsync(_db, licenseId, groups, ct);
        CustomerGroupUnion.MoveToGroup(members, target, now);
        CustomerGroupUnion.PropagateBlacklist(members.Append(canonical).DistinctBy(r => r.Id).ToList(), now);
    }

    private void Skipped(Guid licenseId, Exception ex, Tally tally)
    {
        tally.Failed++;
        _log.LogWarning(
            "Kimlik birleştirme: grup geri alındı ve atlandı (lisans {LicenseId}): {ExceptionType}, SQL hata {SqlError}",
            licenseId, ex.GetType().Name, SqlErrorOf(ex)?.ToString(CultureInfo.InvariantCulture) ?? "yok");
    }

    /// <summary>İstisna zincirindeki ilk SQL hata numarası, yoksa null —
    /// günlüğe yazılabilecek tek ayrıntı (iletisi kişisel veri taşıyabilir).
    /// <see cref="CustomerGroupingJob"/> da kullanır.</summary>
    internal static int? SqlErrorOf(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SqlException sql) return sql.Number;
        return null;
    }

    /// <summary>İki taraf da dolu (boşluktan ibaret değil) ama farklı
    /// (<see cref="CustomerFieldMerge.SameText"/>: kırpılmış, harf ve I/İ/ı/i
    /// farkı yok).</summary>
    private static bool Conflicting(string? canonicalValue, string? copyValue)
        => !string.IsNullOrWhiteSpace(canonicalValue) && !string.IsNullOrWhiteSpace(copyValue)
           && !CustomerFieldMerge.SameText(canonicalValue, copyValue);

    /// <summary>Bkz. <see cref="Report.AddressConflicts"/>.</summary>
    private static bool AddressConflicting(WpfCustomerProjection canonical, WpfCustomerProjection copy)
    {
        if (BlockEmpty(canonical) || BlockEmpty(copy)) return false;
        (string? A, string? B)[] parts =
            [(canonical.Address, copy.Address), (canonical.City, copy.City), (canonical.District, copy.District)];
        return parts.Any(p => Conflicting(p.A, p.B)) || !parts.Any(p => CustomerFieldMerge.SameText(p.A, p.B));
    }

    private static bool BlockEmpty(WpfCustomerProjection p)
        => string.IsNullOrWhiteSpace(p.Address) && string.IsNullOrWhiteSpace(p.City) && string.IsNullOrWhiteSpace(p.District);

    private sealed class Tally
    {
        public int Copies, Orders, Shipments, Links, Balances, Purged, Failed, Unbound;
        public int BalanceTransactions, IbanMemories, PaymentMatches, WaConversations;
        public int PhoneConflicts, AddressConflicts, EmailConflicts, NameConflicts, NotesConflicts, GroupConflicts;
        public int AtSpelledCanonicals;
    }
}
