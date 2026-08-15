using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Catalog;

/// <summary>Eşleşmenin nasıl bulunduğu — çekmecenin açılıp açılmayacağını belirler.</summary>
public enum AxisMatchKind
{
    /// <summary>Hiçbir değer bulunamadı.</summary>
    None,
    /// <summary>Token eşitliğiyle (ya da eş anlamlıyla) doğrudan bulundu.</summary>
    Exact,
    /// <summary>Bitişik yazımın bölünmesiyle TAHMİN edildi; onay ister.</summary>
    Combination
}

/// <param name="Kind">Eşleşmenin nasıl bulunduğu.</param>
/// <param name="Values">Eksen değerlerinin kanonik hâli, eksen sırasında.</param>
public sealed record AxisMatchResult(AxisMatchKind Kind, IReadOnlyList<string> Values)
{
    /// <summary>Hiçbir şey bulunamadığında dönen paylaşılan boş sonuç.</summary>
    public static readonly AxisMatchResult Empty =
        new(AxisMatchKind.None, Array.Empty<string>());

    /// <summary>
    /// Çekmece açılmalı mı? Tasarımın kuralı: <b>yalnız</b> tam ve tek eşleşmede
    /// akış kesilmeden sipariş yazılır. Kombinasyon tek değere inse bile onay
    /// ister — o bir tahmin, operatörün gördüğü bir şey değil.
    /// </summary>
    public bool NeedsPicker => Kind != AxisMatchKind.Exact || Values.Count != 1;
}

/// <summary>
/// İzleyici yorumundan eksen değerlerini çıkarır. <b>Substring yok, fuzzy yok</b>:
/// yalnız token eşitliği. Sebep somut — "l" ile "1" birbirine benziyor; yanlış
/// beden yazmak iade ve çift yönlü kargo demek. Emin olmadığımızda hiçbir şey
/// işaretlemeyip operatöre soruyoruz.
/// </summary>
public static class AxisValueMatcher
{
    // Normalize edilmiş hâlleriyle (büyük harf, Türkçe katlanmış).
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
        { "BEDEN", "BEDENI", "NUMARA", "NUMARASI", "NO" };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["SMALL"] = "S", ["KUCUK"] = "S",
        ["MEDIUM"] = "M", ["ORTA"] = "M",
        ["LARGE"] = "L", ["BUYUK"] = "L"
    };

    // Noktalama token'ın UCUNDAN kırpılır, İÇİNDEN değil: "m!" → "M" olurken
    // "36-38" bozulmadan kalmalı.
    private static readonly char[] EdgePunctuation = ".,;:!?()[]{}\"'".ToCharArray();

    // Bitişik yazım bölmesinin girdi sınırı. Uzun token'da olası bölme sayısı
    // üstel büyür; 12 hane hiçbir gerçek beden yazımını dışarıda bırakmıyor.
    private const int MaxCombinationTokenLength = 12;

    /// <param name="comment">İzleyicinin ham yorumu.</param>
    /// <param name="activeCode">
    /// Operatörün kutudaki aktif yayın kodu. Kod metinden <b>silinir</b>; aranmaz.
    /// Bu ayrım çok kelimeli kodları ("Güzel Elbise") sorunsuz kılar — kodun ne
    /// olduğunu zaten biliyoruz, keşfetmemiz gerekmiyor.
    /// </param>
    /// <param name="axisValues">İzleyici ekseninin kanonik değerleri, eksen sırasında.</param>
    public static AxisMatchResult Match(
        string? comment, string? activeCode, IReadOnlyList<string> axisValues)
    {
        if (axisValues.Count == 0) return AxisMatchResult.Empty;

        var tokens = Tokenize(comment);
        if (tokens.Count == 0) return AxisMatchResult.Empty;

        var codeTokens = Tokenize(activeCode);
        while (RemoveSequence(tokens, codeTokens)) { }

        tokens.RemoveAll(Fillers.Contains);
        if (tokens.Count == 0) return AxisMatchResult.Empty;

        // Uzun değer dizisi önce denenir: "50 ML" varken "ML" tek başına
        // tüketilmesin.
        var candidates = axisValues
            .Select((v, i) => new Candidate(i, Tokenize(v)))
            .Where(c => c.Tokens.Count > 0)
            .OrderByDescending(c => c.Tokens.Count)
            .ToList();

        // SortedSet: sonuç her zaman EKSEN sırasında ve tekil çıkar.
        var hits = new SortedSet<int>();

        // 1) Tam tarama — tüketerek. Aynı değer iki kez yazıldıysa bir kez sayılır.
        foreach (var c in candidates)
            while (RemoveSequence(tokens, c.Tokens))
                hits.Add(c.Index);

        if (hits.Count > 0)
            return new AxisMatchResult(AxisMatchKind.Exact, Project(axisValues, hits));

        // 2) Eş anlamlılar — YALNIZ tam tarama boş dönünce. Aksi hâlde ekseninde
        //    gerçekten "Orta" yazan ürün "orta"yı M'ye çevirip eşleşmeyi kaybederdi.
        var singles = candidates.Where(c => c.Tokens.Count == 1).ToList();
        foreach (var t in tokens)
        {
            if (!Synonyms.TryGetValue(t, out var target)) continue;
            var c = singles.FirstOrDefault(x => x.Tokens[0] == target);
            if (c is not null) hits.Add(c.Index);
        }

        if (hits.Count > 0)
            return new AxisMatchResult(AxisMatchKind.Exact, Project(axisValues, hits));

        // 3) Bitişik yazım bölmesi — son çare, sonucu her hâlükârda onaya gider.
        foreach (var token in tokens)
        {
            if (token.Length > MaxCombinationTokenLength) continue;

            var solutions = new List<List<int>>();
            Collect(token, 0, singles, new List<int>(), solutions, limit: 2);

            // İki durum birbirine benziyor ama sonuçları taban tabana zıt:
            //
            // Birden çok bölme = GERÇEK BELİRSİZLİK. "LSM" hem L+S+M hem LS+M
            // olabiliyor; birini seçersek YANLIŞ beden yazma riski var — bu da
            // iade ve çift yönlü kargo demek. Tüm eşleşmeyi bırakıp operatöre
            // soruyoruz.
            if (solutions.Count > 1) return AxisMatchResult.Empty;

            // Hiç bölme yok = kelime zaten beden değil. Gerçek sohbet "lütfen",
            // "abi", "kardeşim" doludur; bunlar sadece gürültü. Eşleşmeyen her
            // sohbet kelimesi gibi atlanır — önceki token'da bulunmuş sağlam bir
            // sonucu çöpe attırmaz.
            if (solutions.Count == 0) continue;

            foreach (var i in solutions[0]) hits.Add(i);
        }

        return hits.Count == 0
            ? AxisMatchResult.Empty
            : new AxisMatchResult(AxisMatchKind.Combination, Project(axisValues, hits));
    }

    private sealed record Candidate(int Index, List<string> Tokens);

    private static IReadOnlyList<string> Project(IReadOnlyList<string> axisValues, SortedSet<int> hits)
        => hits.Select(i => axisValues[i]).ToList();

    private static List<string> Tokenize(string? text)
    {
        var normalized = SearchNormalizer.Normalize(text);
        if (normalized.Length == 0) return new List<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(EdgePunctuation))
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>
    /// <paramref name="needle"/> dizisini <paramref name="tokens"/> içinde
    /// <b>bitişik alt dizi</b> olarak arar ve ilk bulduğunu siler. Dönüş: silindi mi.
    /// </summary>
    private static bool RemoveSequence(List<string> tokens, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || needle.Count > tokens.Count) return false;

        for (var i = 0; i + needle.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (string.Equals(tokens[i + j], needle[j], StringComparison.Ordinal)) continue;
                ok = false;
                break;
            }
            if (!ok) continue;

            tokens.RemoveRange(i, needle.Count);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tek token'ı eksen değerlerinin ardışık birleşimi olarak bölmeye çalışır.
    /// En az İKİ parça arar: tek parçalık bölme zaten tam eşleşmedir ve bu
    /// noktaya gelinmişse tam tarama başarısız olmuştur.
    /// </summary>
    private static void Collect(
        string token, int pos, List<Candidate> singles,
        List<int> path, List<List<int>> found, int limit)
    {
        if (found.Count >= limit) return;

        if (pos == token.Length)
        {
            if (path.Count >= 2) found.Add(new List<int>(path));
            return;
        }

        foreach (var c in singles)
        {
            var value = c.Tokens[0];
            if (pos + value.Length > token.Length) continue;
            if (string.CompareOrdinal(token, pos, value, 0, value.Length) != 0) continue;

            path.Add(c.Index);
            Collect(token, pos + value.Length, singles, path, found, limit);
            path.RemoveAt(path.Count - 1);

            if (found.Count >= limit) return;
        }
    }
}
