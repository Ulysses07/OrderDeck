namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Testler için rastgele TR cep numarası üretir. Repo public: uydurma gibi
/// görünen sabit bir numara da gerçek bir aboneye ait olabilir, bu yüzden test
/// kodunda telefon literal'i tutulmaz; numara her çağrıda üretilir
/// (<see cref="TestTckn"/> ile aynı gerekçe).
/// </summary>
public static class TestPhone
{
    /// <summary>Öneksiz 10 haneli ulusal numara ("55" ile başlar). Biçim
    /// testleri bunu parçalayıp istedikleri yazılışa sokar.</summary>
    public static string NewNational()
        => "55" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>Kanonik biçim: E.164 (+90…).</summary>
    public static string NewE164() => "+90" + NewNational();

    /// <summary>Verilen uzunlukta rastgele rakam dizisi — sabit hat, eksik ya
    /// da fazla haneli gibi numara şekilli geçersiz girdiler için.</summary>
    public static string Digits(int count)
        => string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10)));
}
