using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// <c>CustomerProjectionFullSync</c> göçünün SQL backfill'i
/// (<c>LOWER(LTRIM(RTRIM(Username)))</c>) ile
/// <see cref="WpfCustomerProjection.IdentityKeyOf"/>'un ürettiği IdentityKey
/// ASCII, Latin-1 ve Latin Ext-A aralığında (U+0000–U+017F — Türkçe
/// ğ/ı/İ/ş dahil, bunlar Latin Ext-A'da) ÖRTÜŞÜYOR, ama iki durumda
/// AYRIŞIYOR: (a) kenarlarda U+0020 DIŞI boşluk-benzeri karakterler (NBSP,
/// TAB, …) — SQL'in LTRIM/RTRIM'i yalnız U+0020'yi kırpar, .NET'in
/// <c>Trim()</c>'i daha geniş bir boşluk kümesini tanır; (b) bu aralığın
/// dışındaki bazı harfler (Latin Ext-B/D, Yunanca/Kiril ekleri, Gürcüce,
/// Cherokee, letterlike semboller, ek düzlem harfleri) — SQL Server
/// koleksiyonunun büyük/küçük harf tablosu ile .NET'in Unicode tablosu bu
/// aralıkta aynı değil. Bu iş o ayrışan satırları .NET tarafında YENİDEN
/// hesaplayıp düzeltir; SQL backfill'i tekrar koşturmak aynı ayrışmayı yine
/// üretir — çözüm yalnız burada, .NET tarafında.
///
/// <para>Ayrıca göçteki <c>IdentityKey</c> AddColumn'ının <c>NEWID()</c>
/// varsayılanını gerçek anahtarına çevirir: deploy otomatik geri alma
/// penceresinde (göç UYGULANMIŞ ama eski imaj kolonu TANIMIYOR) o imajın
/// açtığı satırlar NEWID ile benzersiz ama anlamsız bir anahtarla kalır.</para>
///
/// <para><b>İDEMPOTENT, BEKÇİ semantiği</b>
/// (<see cref="OrderDeck.LicenseServer.Services.Privacy.TcknBackfillJob"/>
/// ile aynı mantık — bkz. o sınıfın dokümanı): prod'da 3.996 satırın
/// yalnızca 177'si ASCII-dışı bir kullanıcı adı taşıyor, dolayısıyla
/// göçten hemen sonraki ilk koşuda bu sayıdan FAZLA düzeltme beklenmez
/// (gerçekte muhtemelen çok daha az — ayrışma yalnız yukarıdaki iki
/// kategoride olur), sonraki koşularda sıfır. Bir geri alma penceresinden
/// SONRA sıfırdan farklı bir sayı görmek de BEKLENEN bir durumdur (o
/// pencerede NEWID satırları açılmıştır); günlük satırı deploy
/// doğrulamasının kanıtıdır, tek başına alarm değildir.</para>
///
/// <para><b>Sıralama KRİTİK: bu iş PR-1'in birleştirme (merge) işinden ÖNCE
/// koşmalı.</b> Birleştirme IdentityKey'e göre gruplar; SQL backfill'in ürettiği
/// yanlış (boş olmayan ama .NET'le uyuşmayan) bir anahtar, aynı kullanıcının
/// doğru-anahtarlı satırıyla eşleşmeyi KAÇIRIR — birleştirme hiç çalışmasa
/// da aynı kişi iki ayrı "asıl" kayıt olarak kalır.</para>
///
/// <para><b>Çakışma politikası (B1 tekil indeksi,
/// <see cref="CustomerIdentityIndex"/>):</b> indeks asıl kayıtlar arasında
/// <c>(LicenseId, Platform, IdentityKey)</c> tekilliğini koyar. Bir satırın
/// düzeltilmiş anahtarı BAŞKA bir asıl kaydın anahtarıyla ÇAKIŞABİLİR — ikisi
/// GERÇEKTEN aynı kişi (ör. geri alma penceresinde NEWID ile açılmış satır),
/// yalnız henüz birleştirilmemiş. Çakışma satır başına yakalanır (SQL Server
/// 2601/2627), sayılır ve DİĞER satırlarla devam edilir — tek bir çakışma
/// koşuyu DÜŞÜRMEZ. Geçişin SONUNDA çakışma olan her lisans için
/// <see cref="CustomerIdentityMergeJob.RunAsync"/> (apply) koşar: gruplama
/// HESAPLANAN anahtarla olduğundan iki satır aynı gruba düşer, biri kopyaya
/// döner ve filtreli indeksin dışına çıkar; ardından onarım geçişi BİR kez
/// daha yapılır. Birleştirme bir grubu eşzamanlı değişiklik yüzünden atlarsa o
/// çakışma ikinci geçişte yine sayılır, sonraki koşu yeniden dener (döngü
/// yok). Günlüğe yalnız sayılar ve lisans/satır Id'leri.</para>
///
/// <para><b>Satır başına KARŞILAŞTIR-VE-DEĞİŞTİR (CAS), izlenen entity +
/// SaveChanges DEĞİL</b> —
/// <see cref="OrderDeck.LicenseServer.Services.Privacy.TcknBackfillJob"/> ile
/// aynı desen ve gerekçe: adaylar <c>AsNoTracking</c> ile salt okunur çekilir;
/// okuma ile yazma arasındaki pencerede <c>Username</c> değişebilir (WPF
/// sync) ya da satır tamamen silinebilir. CAS BİLEREK <c>MergedIntoId</c>'ye
/// bakmıyor: bir satır arada birleştirilmiş (kopya olmuş) olsa bile
/// IdentityKey'ini düzeltmek hâlâ DOĞRU ve ZARARSIZDIR — kopyalar silinmez,
/// kimliğin güncel/tutarlı olması ileride (teşhis, rapor) yine işe yarar; bu
/// yüzden CAS'ın <c>WHERE</c>'i yalnız <c>Id</c> + <c>Username</c> +
/// <c>IdentityKey</c>'e bakar.</para>
///
/// <para><b>Boş anahtar ASLA yazılmaz:</b> <see cref="WpfCustomerProjection.IdentityKeyOf"/>
/// yalnızca tamamen boşluktan oluşan bir <c>Username</c> için <c>""</c>
/// döner — yazılsaydı o satır, aynı boş anahtara düşen TÜM diğer
/// "yalnızca boşluk" kullanıcılarla birleştirme işinde YANLIŞ gruplanırdı.
/// Bu satırlar atlanır ve sayıları (+ Id'leri) uyarı olarak loglanır —
/// kişisel veri (kullanıcı adının kendisi) ASLA loglanmaz, yalnız sayaçlar
/// ve Id'ler.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IdentityKeyRepairJob
{
    private readonly LicenseDbContext _db;
    private readonly CustomerIdentityMergeJob _merge;
    private readonly ILogger<IdentityKeyRepairJob> _log;

    public IdentityKeyRepairJob(LicenseDbContext db, CustomerIdentityMergeJob merge, ILogger<IdentityKeyRepairJob> log)
    {
        _db = db;
        _merge = merge;
        _log = log;
    }

    /// <summary>Onarım geçişi; çakışma olduysa (B1) o lisansları birleştirip
    /// geçişi BİR kez daha yapar (bkz. sınıf dokümanı). Döner: iki geçişte
    /// düzeltilen satır sayısı.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var first = await RepairPassAsync(ct);
        if (first.CollidedLicenses.Count == 0)
            return first.Fixed;

        // Çakışma: aynı kişinin başka bir asıl kaydı zaten o anahtarda (geri
        // alma penceresinde NEWID ile açılmış satır). Birleştirme HESAPLANAN
        // anahtarla gruplar: iki satır aynı gruba düşer, biri kopyaya döner ve
        // filtreli indeksin dışına çıkar — ikinci geçiş kalan anahtarı yazabilir.
        // Grubu atlanan (eşzamanlı değişiklik) lisansın çakışması ikinci geçişte
        // yine sayılır; sonraki koşu yeniden dener.
        var merged = 0;
        var failed = 0;
        foreach (var licenseId in first.CollidedLicenses)
        {
            var report = await _merge.RunAsync(licenseId, apply: true, ct);
            merged += report.Groups - report.FailedGroups;
            failed += report.FailedGroups;
        }
        _db.ChangeTracker.Clear();
        _log.LogWarning(
            "Kimlik anahtarı onarımı: {Licenses} lisansta çakışma — birleştirme koştu ({Merged} kişi birleşti, {Failed} grup atlandı), onarım bir kez daha geçiyor. Lisanslar: {LicenseIds}",
            first.CollidedLicenses.Count, merged, failed, first.CollidedLicenses);

        var second = await RepairPassAsync(ct);
        return first.Fixed + second.Fixed;
    }

    /// <param name="Fixed">Bu geçişte düzeltilen satır.</param>
    /// <param name="CollidedLicenses">B1 indeksine çarpan satırların lisansları.</param>
    private sealed record RepairPass(int Fixed, IReadOnlyList<Guid> CollidedLicenses);

    private async Task<RepairPass> RepairPassAsync(CancellationToken ct)
    {
        // Hacim küçük (prod'da ~4.000 satır): adayları çekip ayrımı
        // bellekte yapmak, kuralı IdentityKeyOf'ta TEK yerde tutar.
        // AsNoTracking: bu satırlar hiç izlenmeyecek — gerçek yazma
        // aşağıdaki CAS'tan geçiyor (bkz. sınıf dokümanı).
        // OrderBy(Id): tarama sırası ORDER BY'sız bırakılırsa SQL Server'ın
        // hiçbir GARANTİSİ olmaz (genelde clustered index sırası görünür ama
        // bu bir uygulama ayrıntısı, sözleşme değil). Çakışma sonrası diğer
        // satırlarla devam edilmesi (aşağıdaki catch) deterministik test
        // edilebilsin diye tarama sırası burada AÇIKÇA Id'ye sabitleniyor.
        // IgnoreQueryFilters: kopyalar varsayılan sorgulardan gizli (A5b) ama
        // onlar da onarılır (bkz. sınıf dokümanı, CAS paragrafı).
        var rows = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.LicenseId, p.Username, p.IdentityKey })
            .ToListAsync(ct);

        var fixedCount = 0;
        var totalEmptyKeyRows = 0;
        var skippedEmptyIds = new List<Guid>();
        var casMissIds = new List<Guid>();
        var collisionIds = new List<Guid>();
        var collidedLicenses = new List<Guid>();

        foreach (var row in rows)
        {
            var expected = WpfCustomerProjection.IdentityKeyOf(row.Username);
            if (expected.Length == 0)
                totalEmptyKeyRows++; // A7 birleştirme grubu riski — düzeltilsin/düzeltilmesin, sayılır.

            if (string.Equals(expected, row.IdentityKey, StringComparison.Ordinal))
                continue; // zaten doğru

            if (expected.Length == 0)
            {
                skippedEmptyIds.Add(row.Id);
                continue; // boş anahtar YAZILMAZ — bkz. sınıf dokümanı
            }

            try
            {
                var n = await RepairIfUnchangedAsync(row.Id, row.Username, row.IdentityKey, ct);
                if (n > 0) fixedCount += n;
                else casMissIds.Add(row.Id); // okuma ile yazma arasında değişti — sonraki koşu yeniden dener
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // B1 tekil indeksiyle çakışma — bkz. sınıf dokümanı. Bu satır
                // ATLANIR, diğer satırlarla devam edilir; lisansı geçişten sonra
                // birleştirilir (RunAsync).
                collisionIds.Add(row.Id);
                if (!collidedLicenses.Contains(row.LicenseId)) collidedLicenses.Add(row.LicenseId);
            }
        }

        if (skippedEmptyIds.Count > 0)
            _log.LogWarning(
                "Kimlik anahtarı onarımı: {Count} satır boş anahtar üreteceği için ATLANDI — Id'ler: {Ids}",
                skippedEmptyIds.Count, skippedEmptyIds);
        if (casMissIds.Count > 0)
            _log.LogWarning(
                "Kimlik anahtarı onarımı: {Count} satır okuma ile yazma arasında değişti (CAS ıskaladı) — Id'ler: {Ids}",
                casMissIds.Count, casMissIds);
        if (collisionIds.Count > 0)
            _log.LogWarning(
                "Kimlik anahtarı onarımı: {Count} satır çakışma — birleştirme gerekir — Id'ler: {Ids}",
                collisionIds.Count, collisionIds);
        if (totalEmptyKeyRows > 0)
            _log.LogInformation(
                "Kimlik anahtarı onarımı: {Count} satırın hesaplanan anahtarı boş (A7 birleştirme grubu riski)",
                totalEmptyKeyRows);
        _log.LogInformation("Kimlik anahtarı onarımı: {Count} satır düzeltildi", fixedCount);
        return new RepairPass(fixedCount, collidedLicenses);
    }

    /// <summary>
    /// Bir satırın IdentityKey'ini, YALNIZ <paramref name="readUsername"/> ve
    /// <paramref name="readKey"/> hâlâ satırdaki değerse düzeltir (satır
    /// başına CAS — bkz. sınıf dokümanı). Satır arada Username'i değişmiş ya
    /// da tamamen silinmişse 0 döner, hiçbir şey yazmaz — ama arada
    /// BİRLEŞTİRİLMİŞ (MergedIntoId dolmuş) olması bu metodu ETKİLEMEZ, CAS
    /// buna bakmaz (bkz. sınıf dokümanı). B1 tekil indeksiyle bir ÇAKIŞMA
    /// burada SqlException fırlatır — bu metot onu yutmaz, yalnız onarım
    /// geçişinin çağırma yeri yutar (satır başına, diğer satırları etkilemesin
    /// diye; politika <see cref="RunAsync"/>'te). Public: deterministik yarış testleri bu
    /// metodu doğrudan çağırıp satırı ARADA değiştirip artık bayat olan
    /// okumalarla çağırabiliyor.
    /// </summary>
    public Task<int> RepairIfUnchangedAsync(Guid id, string readUsername, string readKey, CancellationToken ct)
    {
        var expected = WpfCustomerProjection.IdentityKeyOf(readUsername);
        if (expected.Length == 0)
            return Task.FromResult(0); // defans — RunAsync zaten elemeli ama burada da garanti

        // Username CI_AS: düz '==' "ayşe" ile "ayşe🌸"yi eşit sayabilir.
        // BIN2'ye COLLATE etmek büyük/küçük harf, aksan ve emoji/tam-genişlik
        // farklarını ayırt eden bir karşılaştırmaya zorluyor — ama "tam"
        // eşitlik DEĞİL: SQL Server'ın '=' operatörü, collation'dan bağımsız
        // olarak SONDAKİ U+0020'yi hâlâ yok sayar (ANSI dolgu kuralı).
        // Zararsız: IdentityKeyOf zaten Trim() ile U+0020'yi kırpıyor, yani
        // bu kuralın etki edebileceği fark burada hiç oluşmaz.
        // IgnoreQueryFilters: okuma gibi CAS da kopyayı görmeli — filtreli kalsa
        // kopyanın UPDATE'i sessizce 0 satır etkiler ve "ıskaladı" sayılırdı.
        return _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.Id == id
                && EF.Functions.Collate(p.Username, "Latin1_General_100_BIN2") == readUsername
                && p.IdentityKey == readKey)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.IdentityKey, expected), ct);
    }
}
