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
/// <para><b>Çakışma (gelecekteki B1 tekil indeksi):</b> PR-1'in B1 task'ı
/// <c>(LicenseId, Platform, IdentityKey)</c> için <c>WHERE MergedIntoId IS
/// NULL</c> filtreli bir TEKİL indeks ekleyecek. O indeks varken, bir satırın
/// düzeltilmiş anahtarı BAŞKA bir kanonik satırın (henüz birleştirme işi
/// tarafından işlenmemiş bir eski-imaj kopyasının) anahtarıyla ÇAKIŞABİLİR —
/// ikisi GERÇEKTEN aynı kimliğe ait, yalnız henüz birleştirilmemiş. Bu durum
/// satır başına yakalanır (SQL Server 2601/2627), sayılır ve DİĞER satırlarla
/// devam edilir — tek bir çakışma bütün koşuyu DÜŞÜRMEZ. Çakışan satırın
/// anahtarı olduğu gibi (yanlış ama benzersiz) kalır; birleştirme işi onu
/// zaten doğru ele alacaktır. Bu korumanın ŞİMDİDEN eklenmesi bilerek: B1
/// bu işten SONRA gelse bile, iki işin sırası karışırsa (ör. elle yeniden
/// koşturma) bu iş asla patlamamalı.</para>
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
    private readonly ILogger<IdentityKeyRepairJob> _log;

    public IdentityKeyRepairJob(LicenseDbContext db, ILogger<IdentityKeyRepairJob> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        // Hacim küçük (prod'da ~4.000 satır): adayları çekip ayrımı
        // bellekte yapmak, kuralı IdentityKeyOf'ta TEK yerde tutar.
        // AsNoTracking: bu satırlar hiç izlenmeyecek — gerçek yazma
        // aşağıdaki CAS'tan geçiyor (bkz. sınıf dokümanı).
        var rows = await _db.WpfCustomerProjections.AsNoTracking()
            .Select(p => new { p.Id, p.Username, p.IdentityKey })
            .ToListAsync(ct);

        var fixedCount = 0;
        var totalEmptyKeyRows = 0;
        var skippedEmptyIds = new List<Guid>();
        var casMissIds = new List<Guid>();
        var collisionIds = new List<Guid>();

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
                // Gelecekteki B1 tekil indeksiyle çakışma — bkz. sınıf dokümanı.
                // Bu satır ATLANIR, diğer satırlarla devam edilir.
                collisionIds.Add(row.Id);
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
        return fixedCount;
    }

    /// <summary>
    /// Bir satırın IdentityKey'ini, YALNIZ <paramref name="readUsername"/> ve
    /// <paramref name="readKey"/> hâlâ satırdaki değerse düzeltir (satır
    /// başına CAS — bkz. sınıf dokümanı). Satır arada Username'i değişmiş ya
    /// da tamamen silinmişse 0 döner, hiçbir şey yazmaz — ama arada
    /// BİRLEŞTİRİLMİŞ (MergedIntoId dolmuş) olması bu metodu ETKİLEMEZ, CAS
    /// buna bakmaz (bkz. sınıf dokümanı). Gelecekteki B1 tekil indeksiyle bir
    /// ÇAKIŞMA burada SqlException fırlatır — bu metot onu yutmaz, yalnız
    /// <see cref="RunAsync"/>'in çağırma yeri yutar (satır başına, diğer
    /// satırları etkilemesin diye). Public: deterministik yarış testleri bu
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
        return _db.WpfCustomerProjections
            .Where(p => p.Id == id
                && EF.Functions.Collate(p.Username, "Latin1_General_100_BIN2") == readUsername
                && p.IdentityKey == readKey)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.IdentityKey, expected), ct);
    }
}
