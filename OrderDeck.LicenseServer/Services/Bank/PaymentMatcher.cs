using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Gölge eşleştirme (spec §6). Yalnız <see cref="PaymentMatch"/> yazar; Payment'a dokunmaz.
/// Katmanlar: (1) açıklamada kullanıcı adı — tek aday 0.90; (2) IBAN hafızası 0.85; ikisi aynı müşteriyi
/// gösterirse 0.98; çelişirse öneri yok; (3) ad benzerliği yalnız 0.50 (17.09 kararı: tutar ayırt edici değil).
/// Belirsizlik (çoklu aday) her zaman "öneri yok" — gölge modda yanlış öneri ucuz ama ölçümü kirletir.
/// <para>Aday yalnız aynı lisansın silinmemiş (<c>PurgedAt == null</c>) müşterisidir; IBAN hafızası da ancak böyle
/// bir müşteriyi gösteriyorsa sayılır. Aday listesi (anahtar + token'lar) örnek başına lisans başına BİR kez okunup
/// hesaplanır — eşleştirici scoped'dur (bir çekim koşusu ya da bir istek), partideki her hareket için müşteri tablosu
/// yeniden okunmaz.</para>
/// <para><see cref="BankHasher"/> istemez: hash'ler harekette ve hafızada hazır saklı. Hasher anahtar yokken kurulamaz;
/// eşleştirici dekont onay yolundan da çözülecek ve orayı hiçbir koşulda düşürmemeli.</para>
/// <para>Kanıt (<see cref="PaymentMatch.Evidence"/>, 180 gün saklanır) yalnız katman adı ve kullanıcı adı/ad anahtarı
/// taşır; ham açıklama, IBAN, VKN ya da hash yazılmaz.</para>
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
    private const int SubstringMinLength = 6;
    private const int ExactOnlyMaxLength = 3;

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

    public async Task<PaymentMatch> MatchAsync(BankTransaction tx, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct)
            ?? new PaymentMatch { Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, CreatedAt = DateTimeOffset.UtcNow };
        // İnsan kararı bağlanmış satır yeniden hesaplanmaz.
        if (match.Status is PaymentMatchStatus.ConfirmedByHuman or PaymentMatchStatus.Contradicted or PaymentMatchStatus.ManualOnly)
            return match;

        // Çekim işi giden ve sıfır tutarlı hareketi buraya vermez; yine de gelirse öneri üretilmez.
        var code = (tx.TransactionCode ?? "").Trim();
        var exclusion = tx.Direction != BankTransactionDirection.Incoming ? "direction"
            : tx.Amount == 0 ? "zero"
            : _excludedCodes.Contains(code) ? code
            : null;
        if (exclusion is not null)
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"excluded:{exclusion}", PaymentMatchStatus.NoProposal);
            return await SaveAsync(match, ct);
        }

        var candidates = await CandidatesAsync(tx.LicenseId, ct);
        var text = BankTextNormalizer.Tokenize(tx.Description);
        var pieces = Pieces(text.Tokens);

        // Katman 1 — kullanıcı adı.
        var byUsername = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (c.Key.Length < MinKeyLength) continue;
            var exactToken = text.Tokens.Contains(c.Key);
            var hit = c.Key.Length <= ExactOnlyMaxLength
                ? exactToken
                : exactToken || ContainsAlignedSequence(pieces, c.KeyTokens)
                    || (c.Key.Length >= SubstringMinLength && text.Joined.Contains(c.Key, StringComparison.Ordinal));
            if (hit) byUsername.Add(c);
        }

        // Katman 2 — IBAN hafızası; hafızadaki müşteri artık aday değilse (silinmiş) sayılmaz.
        Guid? byIban = null;
        if (tx.CounterpartyIbanHash is not null)
        {
            var remembered = await _db.CustomerIbanMemories.AsNoTracking()
                .Where(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash)
                .Select(m => (Guid?)m.WpfCustomerId).FirstOrDefaultAsync(ct);
            if (remembered is { } id && candidates.Any(c => c.Id == id)) byIban = id;
        }

        if (byUsername.Count > 1)
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"ambiguous:{byUsername.Count}", PaymentMatchStatus.NoProposal);
            return await SaveAsync(match, ct);
        }
        if (byUsername.Count == 1)
        {
            var id = byUsername[0].Id; var key = byUsername[0].Key;
            if (byIban is { } ib && ib != id)
            {
                Set(match, null, PaymentMatchLayer.None, 0m, $"conflict:username={key},iban-memory", PaymentMatchStatus.NoProposal);
                return await SaveAsync(match, ct);
            }
            var both = byIban == id;
            Set(match, id, PaymentMatchLayer.UsernameInDescription, both ? BothConfidence : UsernameConfidence,
                both ? $"username={key};iban-memory" : $"username={key}", PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }
        if (byIban is { } ibanCustomer)
        {
            Set(match, ibanCustomer, PaymentMatchLayer.IbanMemory, IbanConfidence, "iban-memory", PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }

        // Katman 3 — ad benzerliği (yalnız öneri).
        var nameHits = candidates
            .Where(c => c.NameTokens.Count >= 2 && ContainsSequence(text.Tokens, c.NameTokens))
            .ToList();
        if (nameHits.Count == 1)
        {
            Set(match, nameHits[0].Id, PaymentMatchLayer.NameAmount, NameConfidence,
                "name=" + string.Join(' ', nameHits[0].NameTokens), PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }
        Set(match, null, PaymentMatchLayer.None, 0m, nameHits.Count > 1 ? $"ambiguous-name:{nameHits.Count}" : "no-signal", PaymentMatchStatus.NoProposal);
        return await SaveAsync(match, ct);
    }

    /// <summary>Müşterinin eşleştirme biçimi: bitişik anahtar ("aysegul34"), harf/rakam sınırında bölünmüş parçaları
    /// ("ayse","gul","34") ve ad token'ları.</summary>
    private sealed record Candidate(Guid Id, string Key, IReadOnlyList<string> KeyTokens, IReadOnlyList<string> NameTokens);

    private async Task<IReadOnlyList<Candidate>> CandidatesAsync(Guid licenseId, CancellationToken ct)
    {
        if (_candidates.TryGetValue(licenseId, out var cached)) return cached;
        var rows = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(c => c.LicenseId == licenseId && c.PurgedAt == null)
            .Select(c => new { c.Id, c.Username, c.FullName }).ToListAsync(ct);
        var list = rows.Select(r => new Candidate(r.Id, BankTextNormalizer.UsernameKey(r.Username),
            BankTextNormalizer.UsernameTokens(r.Username), BankTextNormalizer.Tokenize(r.FullName).Tokens)).ToList();
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

    private static void Set(PaymentMatch m, Guid? customer, PaymentMatchLayer layer, decimal confidence, string evidence, PaymentMatchStatus status)
    {
        m.ProposedWpfCustomerId = customer; m.Layer = layer; m.Confidence = confidence;
        m.Evidence = evidence.Length > 500 ? evidence[..500] : evidence; m.Status = status; m.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<PaymentMatch> SaveAsync(PaymentMatch m, CancellationToken ct)
    {
        if (_db.Entry(m).State == EntityState.Detached) _db.PaymentMatches.Add(m);
        await _db.SaveChangesAsync(ct);
        _log.LogDebug("Gölge eşleştirme: hareket={BankTransactionId} durum={Status} katman={Layer} güven={Confidence}",
            m.BankTransactionId, m.Status, m.Layer, m.Confidence);
        return m;
    }
}
