namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans geneli kargo ayarı — yayın ve etiket senkronu spec'i 2026-10-13 §3.1, §4.8, §6.2 (sahip kararı Y4: tek,
/// lisans geneli ayar). Bugün her bilgisayar kendi yerel ayarını kullanıyor (<c>AppSettings.Shipping</c>); mod On'da
/// bütün bilgisayarlar bunu okur, <c>disable</c> turunda yerel ayara kopyalanır (§8.5).
///
/// <para><b>Yazıcılar.</b> Mod On'da WPF ayar ekranı (<c>PUT shipping-settings</c>, <see cref="UpdatedBy"/> =
/// çağıran ReplicaId); her kipte Burak'ın <c>sales-sync shipping set</c> CLI'ı (<see cref="UpdatedBy"/> =
/// <see cref="UpdatedByCli"/>). Mod kapısının 4. koşulu satırın varlığını ve <see cref="UpdatedBy"/>'ın dolu
/// olmasını ister (§4.10-4). Satır yoksa ayar "henüz yazılmadı" demektir ve sürümü 0 sayılır.</para>
/// </summary>
public sealed class LicenseShippingSettings
{
    /// <summary>CLI'ın <see cref="UpdatedBy"/> değeri (§4.8).</summary>
    public const string UpdatedByCli = "cli";

    /// <summary>Satır yokken sayılan sürüm: ilk yazım <c>expectedVersion = 0</c> ile gelir.</summary>
    public const int MissingVersion = 0;

    /// <summary><c>decimal(18,2)</c>'nin en büyük değeri; üstü kolona sığmaz.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Üzerinde kargo ücretsiz olan en düşük tutar (TL). <see cref="ShippingFee"/> ile birlikte boş = kargo
    /// kapalı.</summary>
    public decimal? FreeShippingThreshold { get; set; }

    /// <summary>Eşiğin altında istenen sabit kargo ücreti (TL).</summary>
    public decimal? ShippingFee { get; set; }

    /// <summary>Eşzamanlılık jetonu: her yazım bir artırır. Ödeme isteği hesapladığı tutarın ayar sürümünü taşır
    /// (S3, <c>settings-stale</c>, §6.2).</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary><see cref="UpdatedByCli"/> ya da yazan kopyanın ReplicaId'si (metin).</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Geçerli ayar: ya ikisi de boş (kargo kapalı) ya ikisi de &gt; 0. Kural WPF'in
    /// <c>ShippingSettings.IsEnabled</c>'ıyla aynı (<c>OrderDeck.Core/Settings/AppSettings.cs</c>): yalnız biri dolu,
    /// sıfır ya da negatif bir ayar orada "kapalı" sayılır; burada sessizce kapalıya dönmesin diye reddedilir. Ek
    /// olarak tutar kuruş hassasiyetinde (en çok 2 ondalık) ve <c>decimal(18,2)</c>'ye sığmalı: aksi hâlde kolon
    /// değeri yuvarlar ya da yazım düşer ve istemcinin gönderdiği tutarla saklanan ayrışır.
    /// </summary>
    public static bool IsValid(decimal? threshold, decimal? fee) =>
        (threshold, fee) switch
        {
            (null, null) => true,
            ({ } t, { } f) => IsAmount(t) && IsAmount(f),
            _ => false,
        };

    private static bool IsAmount(decimal value) =>
        value > 0 && value <= MaxAmount && decimal.Round(value, 2) == value;
}
