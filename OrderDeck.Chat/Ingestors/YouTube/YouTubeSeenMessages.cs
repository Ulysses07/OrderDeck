namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// YouTube'un "gösterilmiş mesaj" belleği: son <see cref="Capacity"/> mesaj kimliği, en eskisi önce
/// unutulur.
///
/// <para><b>Neden ayrı bir nesne.</b> YouTube akışı her yeni bağlantıda yakın geçmişi yeniden
/// gönderir. Bellek okuyucunun içindeyken okuyucu her yeniden kurulduğunda (yenileme, yayın
/// oturumunu bitirip açma) sıfırlanıyor ve aynı mesajlar sohbete ikinci kez düşüyordu. Servis tek
/// bir örneği ömrü boyunca tutar ve kurduğu her okuyucuya onu verir.</para>
///
/// <para>İş parçacığı güvenlidir: eski okuyucu kapanırken yenisi başlamış olabilir.</para>
/// </summary>
public sealed class YouTubeSeenMessages
{
    private const int Capacity = 5000;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _ids = new(Capacity);
    private readonly Queue<string> _order = new(Capacity);

    /// <summary>Kimliği belleğe ekler.</summary>
    /// <returns>Kimlik ilk kez görülüyorsa true; bellekteyse false (mesaj yeniden yayımlanmamalı).</returns>
    public bool TryAdd(string id)
    {
        lock (_gate)
        {
            if (!_ids.Add(id)) return false;

            _order.Enqueue(id);
            while (_order.Count > Capacity)
                _ids.Remove(_order.Dequeue());
            return true;
        }
    }
}
