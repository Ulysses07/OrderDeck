using System.Collections.Generic;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Yayınlanmış yorum id'lerinin sınırlı halkası (HashSet + FIFO kuyruğu →
/// O(1) ekleme ve tahliye).
///
/// <para><b>R11-CHAT01 — neden poller'ın DIŞINDA:</b> ardışık anketler
/// çakışır (<c>order=reverse_chronological</c> aynı en yeni yorumları limit
/// dışına kayana kadar döndürür), bu yüzden yayınlanan id'ler hatırlanır.
/// Hafıza poller nesnesinin alanıyken, her yeniden bağlanma onu sıfırlıyordu:
/// hosted service döngüsü her turda YENİ bir
/// <see cref="FacebookLiveCommentsStream"/> kuruyor. Yeni poller'ın ilk
/// anketi son 100 yorumu getirir ve hepsini "yeni" sayıp tekrar yayımlardı.</para>
///
/// <para>R10-CHAT01 bu yolu daha da sık gezilir yaptı: geçici hata artık
/// videoyu karalisteye almıyor, backoff ile AYNI yayına geri bağlanıyor.
/// Yani kopuk ağda her backoff turu sohbete 100 yorumluk bir tekrar
/// basardı — sipariş alırken "kodu ilk yazan alır" akışında yanıltıcı.</para>
///
/// <para>Hafıza video kimliğine bağlı: <see cref="ResetFor"/> yalnız bağlanılan
/// video DEĞİŞTİĞİNDE temizler. Aynı videoya yeniden bağlanmak hatırlamayı
/// sürdürür; yeni yayın ise temiz başlar (farklı yayının id'leri bu halkayı
/// boşuna doldurmasın).</para>
///
/// <para>Tek poller döngüsünden kullanılır; kilit yok.</para>
/// </summary>
public sealed class FacebookSeenComments
{
    private readonly int _capacity;
    private readonly HashSet<string> _ids;
    private readonly Queue<string> _order;
    private string? _videoId;

    public FacebookSeenComments(int capacity = 5000)
    {
        _capacity = capacity;
        _ids = new HashSet<string>(capacity);
        _order = new Queue<string>(capacity);
    }

    /// <summary>
    /// Halkayı verilen videoya bağlar. Video değiştiyse hafızayı temizler,
    /// aynıysa dokunmaz (yeniden bağlanma tekrarı engellensin diye).
    /// </summary>
    public void ResetFor(string videoId)
    {
        if (_videoId == videoId) return;
        _videoId = videoId;
        _ids.Clear();
        _order.Clear();
    }

    /// <summary>
    /// İlk kez görülüyorsa true döner ve id'yi kaydeder; zaten görülmüşse false.
    /// </summary>
    public bool TryAdd(string id)
    {
        if (!_ids.Add(id)) return false;
        _order.Enqueue(id);
        if (_order.Count > _capacity)
            _ids.Remove(_order.Dequeue());
        return true;
    }
}
