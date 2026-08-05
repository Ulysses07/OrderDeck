using System;
using System.Collections.Generic;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir <see cref="InstagramCommentWatermark.Advance"/> çağrısının sonucu.</summary>
/// <param name="NewComments">Yayınlanacak yorumlar, <b>kronolojik</b> (eski→yeni) sırada.</param>
/// <param name="Overflowed">Sayfa taştı, mesaj kaybedildi — polling aralığı sıkılaştırılmalı.</param>
/// <param name="Primed">Bu çağrı ilk çekimdi; hiçbir şey yayınlanmadı (geçmiş basılmaz).</param>
public readonly record struct WatermarkResult(
    IReadOnlyList<InstagramComment> NewComments,
    bool Overflowed,
    bool Primed);

/// <summary>
/// Yeni yorum tespiti. Comments ucunda timestamp filtresi yok
/// (<i>"Comments cannot be filtered by timestamp"</i>), sayfa başına en fazla
/// 50 kayıt, sıralama ters kronolojik.
///
/// <para><b>Neden id-yürüyüşü değil:</b> "son görülen id'ye kadar yürü"
/// yaklaşımında yayıncı o yorumu silerse id listeden kaybolur, algoritma
/// eşleşme bulamaz ve sayfanın tamamını yeni sanıp tekrar basar.</para>
///
/// <para><b>Kural:</b> bir yorum yeni sayılır eğer
/// <c>timestamp &gt; lastTimestamp</c> <b>veya</b>
/// (<c>timestamp == lastTimestamp</c> <b>ve</b> id daha önce görülmediyse).
/// Karşılaştırma daima Meta zamanı ile Meta zamanı arasında.</para>
///
/// <para><b>Taşma tespiti:</b> sayfadaki <b>en eski</b> yorum bile
/// watermark'tan yeniyse sayfa taşmış, arada mesaj kaybedilmiştir. Yanlış
/// pozitif riski var (nadir: bir yorum silinip sayfa küçüldüğünde) ama zararı
/// yok — sonuç sadece polling aralığının bir tur sıkılaşması.</para>
///
/// <para>Thread-safe <b>değil</b>; tek polling döngüsünden çağrılır.</para>
/// </summary>
public sealed class InstagramCommentWatermark
{
    private string? _mediaId;
    private long _lastTimestamp;
    private readonly HashSet<string> _seenAtLastTimestamp = new(StringComparer.Ordinal);
    private int _maxPageSize;
    private bool _primed;

    /// <summary>Şu an izlenen yayının media id'si; hiç çekim yapılmadıysa null.</summary>
    public string? MediaId => _mediaId;

    /// <summary>İlk çekim yapıldı mı (geçmiş yutuldu mu).</summary>
    public bool IsPrimed => _primed;

    /// <summary>
    /// Bir çekimin sonucunu işler. <paramref name="page"/> Graph'ın döndürdüğü
    /// sırayla (ters kronolojik) verilebilir — sıralama burada yapılır.
    /// </summary>
    public WatermarkResult Advance(string mediaId, IReadOnlyList<InstagramComment> page)
    {
        if (!string.Equals(mediaId, _mediaId, StringComparison.Ordinal))
        {
            // Yeni yayın → temiz sayfa.
            _mediaId = mediaId;
            _primed = false;
            _lastTimestamp = 0;
            _seenAtLastTimestamp.Clear();
            _maxPageSize = 0;
        }

        if (page.Count > _maxPageSize) _maxPageSize = page.Count;

        if (!_primed)
        {
            _primed = true;
            Adopt(page);
            return new WatermarkResult(Array.Empty<InstagramComment>(), false, true);
        }

        var fresh = new List<InstagramComment>();
        foreach (var c in page)
        {
            if (c.TimestampUnix > _lastTimestamp ||
                (c.TimestampUnix == _lastTimestamp && !_seenAtLastTimestamp.Contains(c.Id)))
            {
                fresh.Add(c);
            }
        }

        // Sayfa doluydu ve hiçbir tanıdık yorum kalmamış → arada kayıp var.
        bool overflowed = page.Count > 0
                          && fresh.Count == page.Count
                          && page.Count >= _maxPageSize;

        fresh.Sort(CompareChronological);

        // Watermark'ı sayfanın TAMAMINA göre ilerlet, sadece yenilere göre
        // değil: aynı saniyedeki "zaten görülmüş" id'ler de sette kalmalı,
        // yoksa bir sonraki çekimde tekrar yeni sayılırlar.
        Adopt(page);

        return new WatermarkResult(fresh, overflowed, false);
    }

    private void Adopt(IReadOnlyList<InstagramComment> page)
    {
        if (page.Count == 0) return;

        long max = long.MinValue;
        foreach (var c in page)
            if (c.TimestampUnix > max) max = c.TimestampUnix;

        if (max < _lastTimestamp) return;

        if (max > _lastTimestamp)
        {
            _lastTimestamp = max;
            _seenAtLastTimestamp.Clear();
        }

        foreach (var c in page)
            if (c.TimestampUnix == max) _seenAtLastTimestamp.Add(c.Id);
    }

    /// <summary>Birincil anahtar timestamp, ikincil id. Aynı saniyedeki gerçek
    /// sıra bilinemez ama sıralamanın <b>deterministik</b> olması şart —
    /// aksi hâlde iki çekim aynı yorumları farklı sırada basardı.</summary>
    private static int CompareChronological(InstagramComment a, InstagramComment b)
    {
        int t = a.TimestampUnix.CompareTo(b.TimestampUnix);
        return t != 0 ? t : string.CompareOrdinal(a.Id, b.Id);
    }
}
