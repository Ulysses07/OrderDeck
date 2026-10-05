namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Testler için rastgele ama kontrol basamaklı (geçerli) TCKN üretir. Repo
/// kuralı sabit kimlik-şekilli dizeleri test koduna yazmayı yasaklıyor
/// (CLAUDE.md — "Never write a literal credential string in tests"); bu
/// sınıf TCKN'yi her çağrıda üretir.
///
/// Algoritma <see cref="OrderDeck.LicenseServer.Services.IntakeForm.TcknValidator"/>
/// ile birebir aynı (kontrol basamağı formülü oradan kopyalandı).
/// </summary>
public static class TestTckn
{
    /// <summary>11 haneli, kontrol basamakları tutan rastgele bir TCKN üretir.
    /// İlk hane 1-9 arası (TCKN 0 ile başlayamaz).</summary>
    public static string NewValid()
    {
        var rnd = Random.Shared;
        var firstNine = new int[9];
        firstNine[0] = rnd.Next(1, 10);
        for (var i = 1; i < 9; i++) firstNine[i] = rnd.Next(0, 10);
        return FromFirstNine(firstNine);
    }

    /// <summary>Verilen ilk 9 haneden iki kontrol basamağını hesaplayıp 11
    /// haneli TCKN döner. Belirli bir basamak deseni ZORLAMAK isteyen testler
    /// için — rastgele değil, çağıranın verdiği dizi (örn. negatif-mod dalını
    /// kasıtlı tetikleyen bir desen).</summary>
    public static string FromFirstNine(int[] firstNine)
    {
        if (firstNine.Length != 9)
            throw new ArgumentException("İlk 9 hane tam olarak 9 eleman olmalı.", nameof(firstNine));
        if (firstNine[0] is < 1 or > 9)
            throw new ArgumentException("İlk hane 1-9 arası olmalı (TCKN 0 ile başlayamaz).", nameof(firstNine));

        var d = new int[11];
        Array.Copy(firstNine, d, 9);

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        d[9] = ((odd * 7 - even) % 10 + 10) % 10;
        d[10] = d.Take(10).Sum() % 10;

        return string.Concat(d);
    }
}
