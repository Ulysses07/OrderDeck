namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Testler için rastgele ama kontrol haneleri geçerli TR IBAN üretir. Repo
/// public; sabit bir IBAN gerçek bir hesaba ait olabilir, bu yüzden dekont
/// fikstürleri banka kodunu koruyup hesap kısmını her çağrıda üretir.
/// </summary>
public static class TestIban
{
    /// <summary>"TR" + 2 kontrol hanesi + 5 haneli banka kodu + "0" + 16
    /// rastgele hane = 26 karakter, boşluksuz.</summary>
    public static string NewTr(string bankCode)
    {
        if (bankCode.Length != 5 || !bankCode.All(char.IsAsciiDigit))
            throw new ArgumentException("Banka kodu 5 haneli olmalı.", nameof(bankCode));

        var bban = bankCode + "0"
            + string.Concat(Enumerable.Range(0, 16).Select(_ => Random.Shared.Next(10)));

        // ISO 13616 mod-97: ülke kodu + "00" sona alınır, harfler sayıya
        // çevrilir (T=29, R=27); kontrol = 98 - (sayı mod 97).
        var mod = 0;
        foreach (var c in bban + "292700")
            mod = (mod * 10 + (c - '0')) % 97;

        return $"TR{98 - mod:D2}{bban}";
    }

    /// <summary>Dörtlü gruplu yazılış: "TR12 3456 …".</summary>
    public static string Grouped(string iban)
        => string.Join(' ', iban.Chunk(4).Select(g => new string(g)));
}
