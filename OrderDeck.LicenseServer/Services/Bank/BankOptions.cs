namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`OrderDeck:Bank` bölümü. <see cref="HashKey"/> .env'den gelir (Bitwarden'da yedek);
/// IBAN/VKN hash'leri bu anahtarla HMAC'lenir — anahtar değişirse eski hash'ler eşleşmez.</summary>
public sealed class BankOptions
{
    public string HashKey { get; set; } = "";

    /// <summary>Eşleştirme dışı bırakılan işlem kodları (POS tahsilatı vb.). Başlangıç: CCP (QNB demo).
    /// <para>Yapılandırma bu listeye EKLER: .NET binder dizi varsayılanının üstüne yazmaz, sonuna
    /// ekler — varsayılan CCP yapılandırmayla çıkarılamaz. Bilinçli; varsayılan değiştirilmiyor.</para></summary>
    public string[] ExcludedTransactionCodes { get; set; } = ["CCP"];

    public int RawJsonRetentionDays { get; set; } = 90;
    public int DescriptionRetentionDays { get; set; } = 180;
}
