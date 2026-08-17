using System.ComponentModel.DataAnnotations;

namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans başına barkod sıra numarası üreteci. Tek satır, tek sayı.
///
/// <para><b>Neden ayrı tablo:</b> barkod yükü türetilmiş DEĞİL — eksen
/// değerlerinden ya da Id'den hesaplanmıyor. Türetseydik eksen değeri
/// düzeltilince (yazım hatası) basılı etiket geçersiz olurdu. Atanan bir
/// sayı, kaynağını bir yerde saklamayı zorunlu kılar; burası orası.</para>
///
/// <para><b>Neden lisans başına:</b> numaralar kısa (10 hane) ve operatörün
/// gözüyle okunabilir olsun diye küçük başlıyor. Global tek sayaç, kiracıların
/// numaralarını birbirine karıştırıp gereksizce büyütürdü. Benzersizlik zaten
/// <c>(LicenseId, Barcode)</c> indeksinde.</para>
///
/// <para><b>RowVersion:</b> aynı lisans için iki eşzamanlı ayırma, sayacı aynı
/// değerden okuyup aynı numarayı verebilirdi. Damga bunu
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>'a
/// çevirir; çağıran 409 döner. Benzersiz indeks son savunma hattı, ilk değil.
/// Emsal: <c>20260501075917_AddConcurrencyTokens</c> (License/Activation).</para>
/// </summary>
public class BarcodeCounter
{
    /// <summary>Birincil anahtar; lisans başına tek satır.</summary>
    public Guid LicenseId { get; set; }

    /// <summary>Bir sonraki VERİLECEK numara. İlk satır 1'den başlar.</summary>
    public long Next { get; set; }

    /// <summary>Eşzamanlılık damgası; SQL Server <c>rowversion</c>.</summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
