namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`OrderDeck:Bank` bölümü. <see cref="HashKey"/> .env'den gelir (Bitwarden'da yedek);
/// IBAN özetleri bu anahtarla HMAC'lenir — anahtar değişirse eski özetler eşleşmez. VKN/TCKN özetlenmez (KVKK Ö-2).</summary>
public sealed class BankOptions
{
    public string HashKey { get; set; } = "";

    /// <summary>Eşleştirme dışı bırakılan işlem kodları (POS tahsilatı vb.). Varsayılan: CCP (QNB demo).
    /// <para>Yapılandırma bu listeye EKLER: .NET binder dizi varsayılanının üstüne yazmaz, sonuna ekler — varsayılan CCP
    /// yapılandırmayla çıkarılamaz (bilinçli). Bu yüzden appsettings.json'da liste YOK (orada CCP yazmak [CCP, CCP]
    /// üretirdi). Kod eklemek için ortam değişkeni, sıfırdan numaralı: <c>OrderDeck__Bank__ExcludedTransactionCodes__0=POS</c>
    /// → [CCP, POS] (prod'da docker-compose'un <c>environment</c> bölümüne de eklenmeli).</para></summary>
    public string[] ExcludedTransactionCodes { get; set; } = ["CCP"];

    // Saklama tablosu (KVKK K-6, Ö-14, not karar 5; süreler öneri, avukat onaylamalı) — BankDataRetentionJob. Süreler
    // hareketin İŞLEM tarihinden işler. Değiştirirsen gizlilik politikasının Bölüm 5'ini (iki kopya) de değiştir.

    /// <summary>Ham kayıt (beyaz listeli, <see cref="BankRawJsonRedactor"/>): 30 gün.</summary>
    public int RawJsonRetentionDays { get; set; } = 30;

    /// <summary>Bir müşteriyle eşleşmeyen havalenin açıklaması ve gönderen adı (eşleştirmenin gerekçe metniyle): 30 gün.</summary>
    public int UnmatchedDescriptionRetentionDays { get; set; } = 30;

    /// <summary>Hareketin tamamı, eşleştirme satırı ve gap (gap oluşturmadan): 180 gün.</summary>
    public int TransactionRetentionDays { get; set; } = 180;

    /// <summary>IBAN–müşteri bağı, son doğrulanmış havaleden (<c>CustomerIbanMemory.LastConfirmedAt</c>): 12 ay.</summary>
    public int IbanMemoryRetentionMonths { get; set; } = 12;

    /// <summary>Geç muhasebeleşen hareket çekildikten en az bu kadar gün kalır (eşleştirme ve dekont bağdaştırması yetişsin):
    /// her süre fiilen <c>max(işlem tarihi + süre, çekim + bu)</c>.</summary>
    public int MinDaysAfterFetch { get; set; } = 7;
}
