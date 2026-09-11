using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OrderDeck.Core.Customers;

/// <summary>
/// <see cref="CustomerSearch.Matches"/> kuralının SQL karşılığı. Aynı kuralın
/// ikinci bir yerde elle yazılması DEĞİL — terimleri yine
/// <see cref="CustomerSearch"/>'ün kendi ilkelerinden (<c>Fold</c>,
/// <c>NormalizePhoneKey</c>, <c>IsPhoneQuery</c>) üretir, yalnız sonucu
/// <c>INSTR</c> koşullarına ve FTS5 MATCH ifadesine çevirir.
/// </summary>
public sealed class CustomerSearchPlan
{
    /// <summary>Tek bir arama terimi. <see cref="Folded"/> ad alanlarında,
    /// <see cref="PhoneDigits"/> (varsa) telefonda aranır; ikisi VEYA'lı —
    /// <c>Matches</c>'taki "ya haystack'te ya telefonda tutsun" kuralı.</summary>
    public sealed record Term(string Folded, string? PhoneDigits);

    private CustomerSearchPlan(IReadOnlyList<Term> terms, bool phoneOnly, bool matchesNothing)
    {
        Terms = terms;
        PhoneOnly = phoneOnly;
        MatchesNothing = matchesNothing;
    }

    public IReadOnlyList<Term> Terms { get; }

    /// <summary>Sorgunun tamamı bir telefon numarası — ad alanlarına hiç
    /// bakılmaz (<c>Matches</c> bu durumda doğrudan <c>MatchesPhone</c> döner).</summary>
    public bool PhoneOnly { get; }

    /// <summary>Sorgu hiçbir kaydı getiremez (boş metin ya da 4 rakamdan kısa
    /// telefon araması). Çağıran veritabanına hiç gitmemeli.</summary>
    public bool MatchesNothing { get; }

    /// <summary>Her terim trigram alt sınırını aşıyor mu? Aşmıyorsa FTS5 indeksi
    /// KULLANILAMAZ — 3 harften kısa metinde MATCH hata vermeden boş döner
    /// (bkz. <see cref="CustomerSearch.MinTrigramLength"/>).</summary>
    public bool CanUseTrigram => !MatchesNothing && Terms.Count > 0 && Terms.All(t =>
        t.Folded.Length >= CustomerSearch.MinTrigramLength);

    public static CustomerSearchPlan Build(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new CustomerSearchPlan(Array.Empty<Term>(), false, matchesNothing: true);

        // Salt numara: parçalara bölünmez ("0555 111 22 33" dört anlamsız terime
        // düşerdi) ve ad alanlarına hiç bakılmaz.
        if (CustomerSearch.IsPhoneQuery(query))
        {
            var digits = CustomerSearch.NormalizePhoneKey(query);
            if (digits.Length < CustomerSearch.MinPhoneDigits)
                return new CustomerSearchPlan(Array.Empty<Term>(), true, matchesNothing: true);
            return new CustomerSearchPlan(
                new[] { new Term(digits, digits) }, phoneOnly: true, matchesNothing: false);
        }

        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t =>
            {
                var digits = CustomerSearch.NormalizePhoneKey(t);
                return new Term(
                    CustomerSearch.Fold(t),
                    digits.Length >= CustomerSearch.MinPhoneDigits ? digits : null);
            })
            .ToList();

        return terms.Count == 0
            ? new CustomerSearchPlan(Array.Empty<Term>(), false, matchesNothing: true)
            : new CustomerSearchPlan(terms, phoneOnly: false, matchesNothing: false);
    }

    /// <summary>
    /// Katlanmış kolonlar üzerinde çalışan WHERE parçası. <paramref name="alias"/>
    /// tablo takma adı, <paramref name="parameters"/> ise Dapper'a verilecek
    /// sözlüğe doldurulur.
    /// </summary>
    public string BuildWhereClause(string alias, IDictionary<string, object?> parameters)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Terms.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            var t = Terms[i];
            var pName = $"t{i}";
            var pPhone = $"p{i}";

            if (PhoneOnly)
            {
                // Ad alanlarına bakılmaz; boş PhoneKey hiçbir şeyle eşleşmemeli
                // (Matches: normalizedPhone.Length == 0 -> false).
                parameters[pPhone] = t.PhoneDigits;
                sb.Append($"({alias}.PhoneKey <> '' AND INSTR({alias}.PhoneKey, @{pPhone}) > 0)");
                continue;
            }

            parameters[pName] = t.Folded;
            sb.Append($"(INSTR({alias}.SearchKey, @{pName}) > 0");
            if (t.PhoneDigits is not null)
            {
                parameters[pPhone] = t.PhoneDigits;
                sb.Append($" OR ({alias}.PhoneKey <> '' AND INSTR({alias}.PhoneKey, @{pPhone}) > 0)");
            }
            sb.Append(')');
        }
        return sb.ToString();
    }

    /// <summary>
    /// FTS5 MATCH ifadesi. Yalnız <see cref="CanUseTrigram"/> doğruyken anlamlı.
    /// WHERE parçasının birebir aynı mantığı: terimler AND'li, terim içinde
    /// ad/telefon kolonları OR'lu.
    /// </summary>
    public string BuildMatchExpression()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Terms.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            var t = Terms[i];
            sb.Append('(');
            if (PhoneOnly)
            {
                sb.Append("{PhoneKey} : ").Append(Quote(t.PhoneDigits!));
            }
            else
            {
                sb.Append("{SearchKey} : ").Append(Quote(t.Folded));
                // Telefon rakamları en az 4 hane, yani trigram sınırının üstünde.
                if (t.PhoneDigits is not null)
                    sb.Append(" OR {PhoneKey} : ").Append(Quote(t.PhoneDigits));
            }
            sb.Append(')');
        }
        return sb.ToString();
    }

    /// <summary>FTS5 dizgi değişmezi: çift tırnakla sarılır, içindeki çift
    /// tırnaklar ikilenir. Aksi hâlde operatörün yazdığı tırnak sorgu dilinin
    /// bir parçası sanılır ve sözdizimi hatası olurdu.</summary>
    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
