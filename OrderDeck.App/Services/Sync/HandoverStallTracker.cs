using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>Devredilemeyen kendi yayını (§5.7 "Yayın X devredilemedi: N etiket gönderilemiyor", §12 risk 4).</summary>
/// <param name="StartedAt">Yayının başlangıcı (unix saniye).</param>
/// <param name="UnsentLabels">Son turdaki gönderilmemiş etiket sayısı.</param>
public sealed record StuckHandover(string SessionId, long StartedAt, int UnsentLabels);

/// <summary>
/// "Devredilemedi" kuralı (spec 2026-10-13 §5.7, §12 risk 4; W2-09): bitmiş, devri bekletilen kendi yayını, sunucuya
/// ulaşan <see cref="StuckAfterTours"/> ardışık turda gönderilmemiş etiket taşıyor VE sayısı hiç azalmıyorsa
/// devredilemiyor sayılır — bir etiket kalıcı olarak gönderilemiyor (ör. partiyi düşüren 400). Büyük kuyrukla biten
/// yayın turlar boyunca azalarak boşalır (Y9); azalan tur seriyi 1'den yeniden başlatır, uyarı çıkmaz.
///
/// <para><b>Yalnız sunucuya ulaşan tur sayılır</b> (<see cref="Observe"/>'u satış akışı turu, <c>sales/state</c>
/// başarılıyken ve ön gönderimden sonra çağırır): çevrimdışı tur seriyi ne ilerletir ne sıfırlar — bağlantısızlık
/// "gönderilemiyor" değildir. Devri görülen, kuyruğu boşalan ya da listeden düşen yayın seriden çıkar. Bellekte
/// tutulur; yeniden açılışta seri baştan sayılır.</para>
///
/// <para>Saf ve tek iş parçacıklıdır: çağıran (satış akışı turu) kendi tur kilidi altında çağırır.</para>
/// </summary>
public sealed class HandoverStallTracker
{
    /// <summary>Seri bu kadar sunucuya ulaşan tura varınca yayın devredilemiyor sayılır (30 sn'lik turda ~2,5 dk;
    /// akış öğesinin atlanma eşiği U10 ile aynı).</summary>
    public const int StuckAfterTours = 5;

    private Dictionary<string, (int Unsent, int Streak)> _series = new(StringComparer.Ordinal);

    /// <summary>Sunucuya ulaşan bir turun gözlemi: devri bekleyen kendi yayınları ve gönderilmemiş etiket sayıları.</summary>
    /// <returns>Şu an devredilemiyor sayılan yayınlar, başlama sırasıyla.</returns>
    public IReadOnlyList<StuckHandover> Observe(IReadOnlyList<PendingHandover> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        var next = new Dictionary<string, (int Unsent, int Streak)>(StringComparer.Ordinal);
        var stuck = new List<StuckHandover>();
        foreach (var h in held.OrderBy(h => h.StartedAt).ThenBy(h => h.SessionId, StringComparer.Ordinal))
        {
            if (h.UnsentLabels <= 0) continue;                  // kuyruk boş: devir bu ya da sonraki turda
            var streak = _series.TryGetValue(h.SessionId, out var previous) && h.UnsentLabels >= previous.Unsent
                ? previous.Streak + 1
                : 1;
            next[h.SessionId] = (h.UnsentLabels, streak);
            if (streak >= StuckAfterTours) stuck.Add(new StuckHandover(h.SessionId, h.StartedAt, h.UnsentLabels));
        }
        _series = next;
        return stuck;
    }

    /// <summary>Seriyi siler (mod Off, lisans değişimi).</summary>
    public void Reset() => _series = new Dictionary<string, (int Unsent, int Streak)>(StringComparer.Ordinal);
}
