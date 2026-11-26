using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Gölge eşleştirme (spec §6). Yalnız <see cref="PaymentMatch"/> yazar; Payment'a dokunmaz.
/// Katmanlar: (1) açıklamada kullanıcı adı — tek kişi 0.90; (2) IBAN hafızası 0.85; ikisi aynı kişiyi
/// gösterirse 0.98; çelişirse öneri yok; (3) ad benzerliği yalnız 0.50 (17.09 kararı: tutar ayırt edici değil).
/// Belirsizlik (birden çok kişi) her zaman "öneri yok" — gölge modda yanlış öneri ucuz ama ölçümü kirletir.
/// <para>Aday yalnız aynı lisansın silinmemiş (<c>PurgedAt == null</c>) müşterisidir; IBAN hafızası da ancak böyle
/// bir müşteriyi gösteriyorsa sayılır. Aday listesi (anahtar + token'lar + kişi) örnek başına lisans başına BİR kez okunup
/// hesaplanır — eşleştirici scoped'dur (sink'in bir çekim koşusu boyunca açık alt kapsamı, bir tarama koşusu ya da bir
/// istek), partideki her hareket için müşteri tablosu yeniden okunmaz.</para>
/// <para><b>Kişi (tasarım §2 "Kişi", §2.4; S2b).</b> Katmanlar satır değil kişi sayar. Kişi, aynı <c>GroupId</c>'yi paylaşan
/// asıl kayıtlardır: ham değer ordinal karşılaştırılır (kırpma ve harf katlaması yok), boş ya da yalnız boşluk gruplanmamıştır
/// (<see cref="PersonMembership.IsGrouped"/>); anahtar <see cref="PaymentPersonKey.ForCanonical"/>, aday sorgusunun okuduğu
/// <c>GroupId</c>'den. Aynı kişinin IG ve TT satırı belirsizlik, kullanıcı adının bir satırı ile IBAN'ın öbür satırı çelişki
/// üretmez. Kanıttaki sayılar (<c>ambiguous:{n}</c>, <c>ambiguous-name:{n}</c>) kişi sayısıdır. Kişi bir asıl kaydıyla önerilir:
/// kullanıcı adında vuran satır, kişinin birden çok satırı vurduysa IBAN bağının satırı aralarındaysa o, yoksa en küçük Id; yalnız
/// IBAN'da bağın satırı; ad katmanında en küçük Id. "En küçük" Id'nin "N" biçiminin ordinal karşılaştırmasıdır ve bellekte
/// yapılır, SQL'de sıralanmaz (uniqueidentifier sırası <see cref="Guid.CompareTo(Guid)"/>'nunkinden farklıdır); kanıttaki
/// kullanıcı adı anahtarı önerilen satırınkidir. Her yazım satıra <see cref="PersonLevelVersion"/> yazar
/// (<see cref="PaymentMatch.MatcherVersion"/>): dışlama, belirsizlik ve yeniden değerlendirme dahil; sürüm satırın son hesabının
/// üreticisidir, boş sürüm satır düzeyi eşleştiricinin (S2b öncesi) satırıdır. <see cref="PaymentMatch.SupportedByIbanMemory"/>'yi
/// de eşleştirici yazar: öneri kişisi IBAN hafızasında (0.98 ve 0.85). İki müşteri Id'sinin aynı kişi olduğunu
/// <see cref="SamePersonAsync"/> aynı aday listesinden söyler.</para>
/// <para><b>Para birimi (tasarım §3).</b> Para birimi TL/TRY olmayan hareket müşteri havalesi sayılmaz: yön, sıfır tutar ve
/// dışlanan koddan sonra dışlanır, kanıtı <c>excluded:currency</c> (<see cref="CurrencyExclusion"/>). Değer
/// (<see cref="BankTransaction.Currency"/>) <see cref="ObifinClient"/>'tan kırpılmış ama harfi normalleştirilmemiş gelir, boşu
/// "TL" yapılır; çekim işi sütun sınırına (3 karakter) keser. Kural bu yüzden kırpar ve harfe bakmaz (<see cref="IsTryCurrency"/>);
/// boş değer (başka bir yoldan) dışlanır. Prod'daki dağılım merge'den önce ölçülür (S2b Görev 14, sorgu (1)): TL/TRY dışında bir
/// yazım çıkarsa kural tahminle değil ona göre genişletilir. <see cref="IsExcluded"/>'ın K-6 tüketicileri de (kaldırmada geri
/// sarma, hesap reddi, <c>PaymentMatchReconciler.RemainingConfirmationsAsync</c>) TL dışı hareketi doğrulama saymaz: S2b'den önce
/// böyle bir harekette işaretlenmiş doğrulama artık kalan sayılmaz, kaldırılırsa da bağın süresi geri sarılmaz. Payı merge öncesi
/// sorgu (1)'dedir; 0'dan büyükse sahip karar verir.</para>
/// <para><see cref="BankHasher"/> istemez: hash'ler harekette ve hafızada hazır saklı. Hasher anahtar yokken kurulamaz;
/// eşleştirici dekont onay yolundan da çözülecek ve orayı hiçbir koşulda düşürmemeli.</para>
/// <para>Kanıt (<see cref="PaymentMatch.Evidence"/>; hareketin açıklamasıyla birlikte silinir, bkz.
/// <see cref="BankDataRetentionJob"/>) yalnız katman adı ve kullanıcı adı anahtarı
/// taşır; ham açıklama, IBAN, VKN, hash ya da müşterinin adı yazılmaz. Ad kişisel veridir ve kanıta hiç girmez; ad
/// katmanı yalnız eşleşen token sayısını yazar ("name:2"), ad gerekirse önerilen müşteriden okunur — silinmişse orada
/// da yoktur. Kullanıcı adı anahtarı taşıyan kanıtı KVKK silmesi (ShopperPurgeService) boşaltır. Çekimden sonra IBAN hafızası
/// değişince yeniden hesaplanan satırın kanıtı <see cref="ReevaluationEvidenceSuffix"/> eki taşır (<see cref="ReevaluateAsync"/>).</para>
/// <para>Kapsamının scoped DbContext'inde koşar: sink'in kendi alt kapsamı (çekim işinin bağlamı DEĞİL), tarama işinin
/// kapsamı, dekont onayı sonrası bağdaştırma işi (<see cref="PaymentMatchReconcileJob"/>) ya da admin isteği. Kapsamı
/// başkasıyla paylaşabileceği için kaydedemediği öneriyi izlemede bırakmaz, yoksa kapsamın sonraki her SaveChanges'i onu
/// yeniden dener (bkz. <see cref="SaveAsync"/>).</para>
/// <para><b>Eşzamanlılık.</b> <see cref="PaymentMatch.UpdatedAt"/> jetondur. Satır okunduktan sonra değiştiyse (arada
/// verilen insan kararı ya da eşzamanlı bir yeniden hesap) kayıt reddedilir; satır taze okunur ve hesap BİR kez yeniden
/// yapılır: taze satır insan kararıysa dokunulmadan döner. İkinci çakışma çağırana çıkar. Yeniden değerlendirme
/// (<see cref="ReevaluateAsync"/>) ilk çakışmayı çağırana bırakır: çağıran hareketi taze okuyup kendisi yeniden dener.</para>
/// </summary>
public sealed class PaymentMatcher
{
    public const decimal UsernameConfidence = 0.90m;
    public const decimal IbanConfidence = 0.85m;
    public const decimal BothConfidence = 0.98m;
    public const decimal NameConfidence = 0.50m;

    /// <summary>Bundan kısa kullanıcı adı anahtarı aday sayılmaz. Boş anahtar (yalnız emoji, Latin dışı yazı ya da
    /// noktalama) her açıklamanın "içinde" bulunurdu; NFKD sembolleri kısa ASCII'ye iner ("№"→"no", "™"→"tm",
    /// "①"→"1") ve açıklamadaki genel token'lara çarpar.</summary>
    private const int MinKeyLength = 3;

    /// <summary>Bu uzunluktan itibaren anahtar bitişik/ayrık yazım farkıyla da bulunur ("burakyildiz" ↔
    /// "BURAK YILDIZ"), ama yalnız açıklama token sınırında: bkz. <see cref="ContainsJoinedTokens"/>.</summary>
    private const int SubstringMinLength = 6;
    private const int ExactOnlyMaxLength = 3;

    /// <summary>Banka açıklamalarında hemen her harekette geçen kalıp kelimeler (normalize biçimde). Kullanıcı adı
    /// yalnız bunlardan (ve rakamdan) oluşuyorsa katman 1'e girmez: bkz. <see cref="Candidate.UsernameUsable"/>.
    /// Gerçek açıklamalar görüldükçe genişletilir.</summary>
    private static readonly HashSet<string> BoilerplateTokens = new(StringComparer.Ordinal)
    {
        "havale", "eft", "fast", "gelen", "giden", "gonderen", "gonderilen", "odeme", "aciklama",
        "ref", "sorgu", "iban", "tl", "try", "banka", "bankasi", "sube", "subesi", "merkez",
    };

    private readonly LicenseDbContext _db;
    private readonly HashSet<string> _excludedCodes;
    private readonly ILogger<PaymentMatcher> _log;
    private readonly Dictionary<Guid, IReadOnlyList<Candidate>> _candidates = [];

    public PaymentMatcher(LicenseDbContext db, IOptions<BankOptions> opt, ILogger<PaymentMatcher> log)
    {
        _db = db; _log = log;
        // Binder varsayılan ["CCP"]'nin sonuna ekler, liste tekrar taşıyabilir: küme olarak tutulur.
        _excludedCodes = opt.Value.ExcludedTransactionCodes
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Yeniden değerlendirmenin (<see cref="ReevaluateAsync"/>) kanıta eklediği ek: öneri çekim anında değil, sonradan
    /// (IBAN hafızası değişince) üretildi. Faz 2 ölçümü (<see cref="PaymentMatchMetrics"/>) bu ekli satırların dekont kararlarını
    /// eşiğin örnekleminden ayırır. ';' ile başlar: KVKK silmesinin kullanıcı adı araması (anahtardan sonra son, ';' ya da ',')
    /// bozulmaz.</summary>
    public const string ReevaluationEvidenceSuffix = ";reeval";

    /// <summary>Para birimi TL/TRY olmayan hareketin dışlama nedeni (kanıt <c>excluded:currency</c>; sınıf özeti, "Para birimi").</summary>
    public const string CurrencyExclusion = "currency";

    /// <summary>Kişi düzeyi eşleştiricinin sürümü (<see cref="PaymentMatch.MatcherVersion"/>; sınıf özeti, "Kişi"). Boş sürüm satır
    /// düzeyi eşleştiricinin (S2b öncesi) satırıdır.</summary>
    public const int PersonLevelVersion = 2;

    public async Task<PaymentMatch> MatchAsync(BankTransaction tx, CancellationToken ct)
    {
        try
        {
            return await MatchOnceAsync(tx, null, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // SaveAsync satırı DB'deki haliyle tazeledi; insan kararıysa MatchOnceAsync onu dokunmadan döner.
            _log.LogInformation("Gölge eşleştirme: hareket={BankTransactionId} önerisi okunduktan sonra değişti; taze satırla "
                + "yeniden hesaplanıyor", tx.Id);
            return await MatchOnceAsync(tx, null, ct);
        }
    }

    /// <summary><see cref="MatchAsync"/>'in aynısı (aynı katmanlar, aynı insan kararı kilidi, aynı jeton), yalnız kanıta
    /// <see cref="ReevaluationEvidenceSuffix"/> eklenir ve çakışma (DbUpdateConcurrencyException) yeniden denenmeden çağırana
    /// çıkar: satır SaveAsync'te DB'deki haliyle tazelenmiş olur. Çekimden sonra IBAN hafızası değişince var olan kararsız satırı
    /// yeniden hesaplamak için (<c>PaymentMatchReconciler.ReevaluateUndecidedAsync</c>). Çağıran hareketi yeniden denemeden önce
    /// taze okur: karşı yazan bir KVKK silmesiyse açıklama gitmiştir, bayat hareketle yeniden hesap silinmiş metinden türeyen kanıtı
    /// geri yazardı. Dışlama kararı hafızadan bağımsızdır, eki almaz.</summary>
    public Task<PaymentMatch> ReevaluateAsync(BankTransaction tx, CancellationToken ct) => MatchOnceAsync(tx, ReevaluationEvidenceSuffix, ct);

    /// <summary>Lisansın önbellekteki aday listesini atar (sınıf özeti): sonraki hesap müşterileri DB'den yeniden okur. Yeniden
    /// değerlendirme çakışmadan sonra çağırır; karşı yazan bir KVKK silmesiyse silinen müşteri önbellekte aday kalmasın.</summary>
    public void ForgetCandidates(Guid licenseId) => _candidates.Remove(licenseId);

    /// <summary>İki müşteri Id'si aynı kişi mi (sınıf özeti, "Kişi"): aynı Id, ya da ikisi de lisansın adayı ve kişi anahtarları
    /// ordinal eşit. Aday olmayan Id'de (silinmiş, kopya, başka lisansın ya da bilinmeyen Id) kişi okunmaz, karşılaştırma satır
    /// düzeyidir. Kişi düzeyi (<see cref="PersonLevelVersion"/>) önerinin dekontun müşterisiyle karşılaştırılması içindir
    /// (bağdaştırıcı, S2b Görev 7).</summary>
    public async Task<bool> SamePersonAsync(Guid licenseId, Guid a, Guid b, CancellationToken ct)
    {
        if (a == b) return true;
        var candidates = await CandidatesAsync(licenseId, ct);
        var personA = candidates.FirstOrDefault(c => c.Id == a)?.PersonKey;
        var personB = candidates.FirstOrDefault(c => c.Id == b)?.PersonKey;
        return personA is not null && string.Equals(personA, personB, StringComparison.Ordinal);
    }

    /// <summary>Hareket müşteri havalesi sayılmaz mı: giden, sıfır tutarlı, işlem kodu
    /// <see cref="BankOptions.ExcludedTransactionCodes"/>'ta (POS tahsilatı vb.) ya da para birimi TL/TRY değil (sınıf özeti,
    /// "Para birimi"). Eşleştirici ona öneri üretmez; bağdaştırıcı da onu dekontun adayı saymaz. Karar hareketin kendi
    /// alanlarından verilir, kanıttan değil (kanıt saklama süresinde boşaltılır).</summary>
    public bool IsExcluded(BankTransaction tx) => ExclusionReason(tx) is not null;

    /// <summary>Dışlama nedeni (kanıta yazılır) ya da null. Yön, sıfır tutar ve kod para biriminden önce sınanır.</summary>
    private string? ExclusionReason(BankTransaction tx)
    {
        var code = (tx.TransactionCode ?? "").Trim();
        return tx.Direction != BankTransactionDirection.Incoming ? "direction"
            : tx.Amount == 0 ? "zero"
            : _excludedCodes.Contains(code) ? code
            : !IsTryCurrency(tx.Currency) ? CurrencyExclusion
            : null;
    }

    /// <summary>Para birimi TL/TRY mi: kırpılmış değer "TL" ya da "TRY", harf duyarsız (sınıf özeti, "Para birimi").</summary>
    internal static bool IsTryCurrency(string? currency)
    {
        var value = (currency ?? "").Trim();
        return string.Equals(value, "TL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "TRY", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PaymentMatch> MatchOnceAsync(BankTransaction tx, string? evidenceSuffix, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct)
            ?? new PaymentMatch { Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, CreatedAt = DateTimeOffset.UtcNow };
        // İnsan kararı bağlanmış satır yeniden hesaplanmaz.
        if (match.Status is PaymentMatchStatus.ConfirmedByHuman or PaymentMatchStatus.Contradicted or PaymentMatchStatus.ManualOnly)
            return match;

        // Çekim işi giden ve sıfır tutarlı hareketi buraya vermez; yine de gelirse öneri üretilmez.
        var exclusion = ExclusionReason(tx);
        if (exclusion is not null)
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"excluded:{exclusion}", PaymentMatchStatus.NoProposal, supportedByIban: false);
            return await SaveAsync(match, ct);
        }
        // Buradan sonraki her sonuç IBAN hafızasının o anki haline bağlı olabilir: yeniden değerlendirmede ek alır.
        var suffix = evidenceSuffix ?? "";

        var candidates = await CandidatesAsync(tx.LicenseId, ct);
        var text = BankTextNormalizer.Tokenize(tx.Description);
        var pieces = Pieces(text.Tokens);

        // Katman 1 — kullanıcı adı.
        var byUsername = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (!c.UsernameUsable) continue;
            var exactToken = text.Tokens.Contains(c.Key);
            var hit = c.Key.Length <= ExactOnlyMaxLength
                ? exactToken
                : exactToken || ContainsAlignedSequence(pieces, c.KeyTokens)
                    || (c.Key.Length >= SubstringMinLength && ContainsJoinedTokens(text.Tokens, c.Key));
            if (hit) byUsername.Add(c);
        }

        // Katman 2 — IBAN hafızası; hafızadaki müşteri artık aday değilse (silinmiş) sayılmaz. Bağın satırı kişisine çevrilir.
        Candidate? byIban = null;
        if (tx.CounterpartyIbanHash is not null)
        {
            var remembered = await _db.CustomerIbanMemories.AsNoTracking()
                .Where(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash)
                .Select(m => (Guid?)m.WpfCustomerId).FirstOrDefaultAsync(ct);
            if (remembered is { } id) byIban = candidates.FirstOrDefault(c => c.Id == id);
        }

        // Katmanlar satır değil kişi sayar (sınıf özeti, "Kişi"); kanıttaki sayı kişi sayısıdır.
        var usernamePersons = PersonCount(byUsername);
        if (usernamePersons > 1)
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"ambiguous:{usernamePersons}{suffix}", PaymentMatchStatus.NoProposal,
                supportedByIban: false);
            return await SaveAsync(match, ct);
        }
        if (usernamePersons == 1)
        {
            var ibanInPerson = byIban is not null && byIban.PersonKey == byUsername[0].PersonKey;
            // Öneri satırı: kişinin vuran satırlarından IBAN bağınınki, yoksa en küçük Id.
            var row = (ibanInPerson ? byUsername.FirstOrDefault(c => c.Id == byIban!.Id) : null) ?? SmallestId(byUsername);
            var key = row.Key;
            // Kanıttaki kullanıcı adı biçimleri KVKK silmesinde (ShopperPurgeService 3b) tam anahtarla aranır: anahtardan sonra
            // son, ';' ya da ','. Biçim değişirse orası da değişir. Yeniden değerlendirme eki ';' ile başlar.
            if (byIban is not null && !ibanInPerson)
            {
                Set(match, null, PaymentMatchLayer.None, 0m, $"conflict:username={key},iban-memory{suffix}", PaymentMatchStatus.NoProposal,
                    supportedByIban: false);
                return await SaveAsync(match, ct);
            }
            Set(match, row.Id, PaymentMatchLayer.UsernameInDescription, ibanInPerson ? BothConfidence : UsernameConfidence,
                (ibanInPerson ? $"username={key};iban-memory" : $"username={key}") + suffix, PaymentMatchStatus.Proposed,
                supportedByIban: ibanInPerson);
            return await SaveAsync(match, ct);
        }
        if (byIban is not null)
        {
            Set(match, byIban.Id, PaymentMatchLayer.IbanMemory, IbanConfidence, "iban-memory" + suffix, PaymentMatchStatus.Proposed,
                supportedByIban: true);
            return await SaveAsync(match, ct);
        }

        // Katman 3 — ad benzerliği (yalnız öneri).
        var nameHits = candidates
            .Where(c => c.NameTokens.Count >= 2 && ContainsSequence(text.Tokens, c.NameTokens))
            .ToList();
        var namePersons = PersonCount(nameHits);
        if (namePersons == 1)
        {
            // Ad kanıta yazılmaz (kişisel veri, silme yolunun dışında kalırdı): yalnız eşleşen token sayısı.
            var row = SmallestId(nameHits);
            Set(match, row.Id, PaymentMatchLayer.NameAmount, NameConfidence, $"name:{row.NameTokens.Count}{suffix}",
                PaymentMatchStatus.Proposed, supportedByIban: false);
            return await SaveAsync(match, ct);
        }
        Set(match, null, PaymentMatchLayer.None, 0m,
            (namePersons > 1 ? $"ambiguous-name:{namePersons}" : "no-signal") + suffix, PaymentMatchStatus.NoProposal,
            supportedByIban: false);
        return await SaveAsync(match, ct);
    }

    /// <summary>Satırların düştüğü kişi sayısı (<see cref="Candidate.PersonKey"/>, ordinal).</summary>
    private static int PersonCount(IEnumerable<Candidate> rows) => rows.Select(c => c.PersonKey).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Id'si "N" biçiminde ordinal en küçük satır (sınıf özeti, "Kişi"). Bellekte sıralanır: SQL Server'ın
    /// uniqueidentifier sırası <see cref="Guid.CompareTo(Guid)"/>'nunkinden farklıdır.</summary>
    private static Candidate SmallestId(IEnumerable<Candidate> rows) => rows.MinBy(c => c.Id.ToString("N"), StringComparer.Ordinal)!;

    /// <summary>Müşterinin eşleştirme biçimi: bitişik anahtar ("aysegul34"), harf/rakam sınırında bölünmüş parçaları
    /// ("ayse","gul","34"), ad token'ları ve kişisi (<see cref="PaymentPersonKey.ForCanonical"/>; sınıf özeti, "Kişi").</summary>
    private sealed record Candidate(Guid Id, string Key, IReadOnlyList<string> KeyTokens, IReadOnlyList<string> NameTokens,
        string PersonKey)
    {
        /// <summary>Katman 1'e girer mi: anahtar en az <see cref="MinKeyLength"/> uzun VE en az bir parçası banka
        /// kalıbı olmayan harf dizisi. Harfsiz anahtar açıklamadaki tutar/tarih/referansa taşar: Tokenize noktalamada
        /// böler ("1.234,56" → 1,234,56; "12.03.2026" → 12,03,2026), bitişik token kuralı parçaları yeniden birleştirir
        /// ("123456", "12032026"), kısa olanı tam token olarak bulunur ("2026", "1000"). Yalnız kalıp kelimeli anahtar
        /// ("havale", "gelen_havale", "fast1000") lisansın hemen her hareketine çarpar. Tek böyle müşteri ilgili her
        /// hareketi yanlış öneriye, gerçek müşteri de eşleşince belirsizliğe (öneri yok) çevirirdi. Müşteri aday
        /// listesinde kalır: IBAN hafızası onu yine gösterir.</summary>
        public bool UsernameUsable { get; } = Key.Length >= MinKeyLength
            && KeyTokens.Any(t => t.Any(char.IsAsciiLetter) && !BoilerplateTokens.Contains(t));
    }

    private async Task<IReadOnlyList<Candidate>> CandidatesAsync(Guid licenseId, CancellationToken ct)
    {
        if (_candidates.TryGetValue(licenseId, out var cached)) return cached;
        var rows = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(c => c.LicenseId == licenseId && c.PurgedAt == null)
            .Select(c => new { c.Id, c.Username, c.FullName, c.DisplayName, c.GroupId }).ToListAsync(ct);
        // Gerçek ad yoksa (form adı girilmemiş müşteri — masaüstünün R3-02 yedeği
        // kalkınca FullName boş kalır) ad katmanı takma adla (DisplayName) dener.
        var list = rows.Select(r => new Candidate(r.Id, BankTextNormalizer.UsernameKey(r.Username),
            BankTextNormalizer.UsernameTokens(r.Username),
            BankTextNormalizer.Tokenize(string.IsNullOrWhiteSpace(r.FullName) ? r.DisplayName : r.FullName).Tokens,
            PaymentPersonKey.ForCanonical(r.Id, r.GroupId))).ToList();
        _candidates[licenseId] = list;
        return list;
    }

    /// <summary>Açıklama parçası: token harf/rakam sınırında bölünür; asıl token'ın başı/sonu olup olmadığı tutulur.</summary>
    private readonly record struct Piece(string Text, bool StartsToken, bool EndsToken);

    /// <summary>Açıklama token'larını kullanıcı adıyla AYNI kuralla böler (<see cref="BankTextNormalizer.UsernameTokens"/>):
    /// Tokenize "1a"yı bölmez, kullanıcı adı parçaları ise "1","a" olur — iki taraf aynı biçimde karşılaştırılmalı.</summary>
    private static List<Piece> Pieces(IReadOnlyList<string> tokens)
    {
        var pieces = new List<Piece>(tokens.Count);
        foreach (var token in tokens)
        {
            var parts = BankTextNormalizer.UsernameTokens(token);
            for (var i = 0; i < parts.Count; i++) pieces.Add(new Piece(parts[i], i == 0, i == parts.Count - 1));
        }
        return pieces;
    }

    /// <summary>Parça dizisi eşleşmesi yalnız asıl token sınırında başlar ve biter: "ali34x" içinde "ali","34"
    /// bulunmaz — "ali34x" başka bir kullanıcı adıdır.</summary>
    private static bool ContainsAlignedSequence(List<Piece> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || haystack.Count < needle.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            if (!haystack[i].StartsToken || !haystack[i + needle.Count - 1].EndsToken) continue;
            var ok = true;
            for (var j = 0; j < needle.Count; j++) if (haystack[i + j].Text != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Bitişik/ayrık yazım: anahtar, açıklamadaki ardışık TAM token'ların birleşimi olmalı — eşleşme bir
    /// token'ın başında başlar, bir token'ın sonunda biter. "burak yildiz" de "burakyildiz" de "burakyildiz"i bulur;
    /// sınırsız alt dize ise iki komşu kelimeye yayılır ya da rakama taşardı: "aygul senol" içinde "gulsen",
    /// "ali canpolat" içinde "alican", "kaya 1234" içinde "kaya12", "99123456" içinde "123456".</summary>
    private static bool ContainsJoinedTokens(IReadOnlyList<string> tokens, string key)
    {
        for (var start = 0; start < tokens.Count; start++)
        {
            var length = 0;
            for (var end = start; end < tokens.Count; end++)
            {
                var token = tokens[end];
                if (length + token.Length > key.Length
                    || string.CompareOrdinal(key, length, token, 0, token.Length) != 0)
                    break;
                length += token.Length;
                if (length == key.Length) return true;
            }
        }
        return false;
    }

    private static bool ContainsSequence(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || haystack.Count < needle.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++) if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Hesabı satıra yazar; her yazım sürümü de yazar (sınıf özeti, "Kişi"). <paramref name="supportedByIban"/>: öneri
    /// kişisi IBAN hafızasında (<see cref="PaymentMatch.SupportedByIbanMemory"/>).</summary>
    private static void Set(PaymentMatch m, Guid? customer, PaymentMatchLayer layer, decimal confidence, string evidence, PaymentMatchStatus status,
        bool supportedByIban)
    {
        m.ProposedWpfCustomerId = customer; m.Layer = layer; m.Confidence = confidence;
        m.Evidence = evidence.Length > 500 ? evidence[..500] : evidence; m.Status = status;
        m.SupportedByIbanMemory = supportedByIban; m.MatcherVersion = PersonLevelVersion; m.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Kaydeder; başarısız kayıt izlemede iz bırakmaz. Eşleştiricinin kapsamı paylaşılabilir: öneri
    /// Added/Modified kalsaydı kapsamın sonraki her SaveChanges'i (partinin kalan hareketleri, bağdaştırıcının kendi
    /// yazısı) onu yeniden dener ve düşerdi. Yeni satır, hareket başına tek öneri indeksinde eşzamanlı bir çağrıya
    /// yenildiyse kazananın satırı döner: iki çağıran tek satırda buluşur. Var olan satır okunduktan sonra değiştiyse
    /// (jeton) satır DB'deki haliyle tazelenir ve çakışma <see cref="MatchAsync"/>'e (yeniden değerlendirmede çağırana) çıkar.</summary>
    private async Task<PaymentMatch> SaveAsync(PaymentMatch m, CancellationToken ct)
    {
        var added = _db.Entry(m).State == EntityState.Detached;
        if (added) _db.PaymentMatches.Add(m);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (added)
        {
            _db.Entry(m).State = EntityState.Detached;
            var winner = await _db.PaymentMatches.FirstOrDefaultAsync(x => x.BankTransactionId == m.BankTransactionId, ct);
            if (winner is null) throw;
            _log.LogInformation("Gölge eşleştirme: hareket={BankTransactionId} önerisini eşzamanlı başka bir çağrı yazdı; o satır geçerli",
                m.BankTransactionId);
            return winner;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Tazelenen satır izleyicide DB'deki gibi Unchanged kalır (satır silindiyse ayrılır): bekleyen yazı yok.
            // Tazeleme de düşerse okunduğu hale döner.
            var entry = _db.Entry(m);
            try
            {
                await entry.ReloadAsync(ct);
            }
            catch
            {
                Revert(entry);
                throw;
            }
            throw;
        }
        catch
        {
            var entry = _db.Entry(m);
            if (added) entry.State = EntityState.Detached;
            else Revert(entry);
            throw;
        }
        _log.LogDebug("Gölge eşleştirme: hareket={BankTransactionId} durum={Status} katman={Layer} güven={Confidence}",
            m.BankTransactionId, m.Status, m.Layer, m.Confidence);
        return m;
    }

    /// <summary>Okunduğu hale döner: izlenen nesne DB'deki satırla aynı kalır, sonraki SaveChanges bir şey yazmaz.</summary>
    private static void Revert(EntityEntry entry)
    {
        entry.CurrentValues.SetValues(entry.OriginalValues);
        entry.State = EntityState.Unchanged;
    }
}
