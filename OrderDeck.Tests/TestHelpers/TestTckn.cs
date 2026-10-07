namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Testler için rastgele ama kontrol basamaklı (geçerli) TCKN üretir. Repo
/// public; sabit kimlik-şekilli dizeler test koduna yazılmaz, TCKN her
/// çağrıda üretilir.
///
/// Sunucu test projesindeki <c>TestTckn</c> ile aynı; kontrol basamağı formülü
/// sunucudaki <c>TcknValidator</c>'dan kopyalandı.
/// </summary>
public static class TestTckn
{
    /// <summary>11 haneli, kontrol basamakları tutan rastgele bir TCKN üretir.
    /// İlk hane 1-9 arası (TCKN 0 ile başlayamaz).</summary>
    public static string NewValid()
    {
        var rnd = Random.Shared;
        var d = new int[11];
        d[0] = rnd.Next(1, 10);
        for (var i = 1; i < 9; i++) d[i] = rnd.Next(0, 10);

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        d[9] = ((odd * 7 - even) % 10 + 10) % 10;
        d[10] = d.Take(10).Sum() % 10;

        return string.Concat(d);
    }
}
