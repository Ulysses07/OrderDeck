namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`OrderDeck:Bank` bölümü. <see cref="HashKey"/> .env'den gelir (Bitwarden'da yedek);
/// IBAN/VKN hash'leri bu anahtarla HMAC'lenir — anahtar değişirse eski hash'ler eşleşmez.</summary>
public sealed class BankOptions
{
    public string HashKey { get; set; } = "";

    /// <summary>Eşleştirme dışı bırakılan işlem kodları (POS tahsilatı vb.). Varsayılan: CCP (QNB demo).
    /// <para>Yapılandırma bu listeye EKLER: .NET binder dizi varsayılanının üstüne yazmaz, sonuna ekler — varsayılan CCP
    /// yapılandırmayla çıkarılamaz (bilinçli). Bu yüzden appsettings.json'da liste YOK (orada CCP yazmak [CCP, CCP]
    /// üretirdi). Kod eklemek için ortam değişkeni, sıfırdan numaralı: <c>OrderDeck__Bank__ExcludedTransactionCodes__0=POS</c>
    /// → [CCP, POS] (prod'da docker-compose'un <c>environment</c> bölümüne de eklenmeli).</para></summary>
    public string[] ExcludedTransactionCodes { get; set; } = ["CCP"];

    public int RawJsonRetentionDays { get; set; } = 90;
    public int DescriptionRetentionDays { get; set; } = 180;
}
