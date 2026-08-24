using System.ComponentModel.DataAnnotations;

namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşteri başına yedek kotasının eşzamanlılık hakemi. Tek satır, tek sayı.
///
/// <para><b>Neden gerekli:</b> kota kontrolü "mevcut baytları TOPLA, yenisini
/// ekle, sığıyorsa yaz" biçiminde. Toplam ile yazım arasındaki pencerede aynı
/// müşterinin ikinci yüklemesi <b>aynı bayat toplamı</b> okur; ikisi de
/// kontrolü geçer, ikisi de yazar ve kota sessizce aşılır. Blob'lar diskte
/// durduğu için aşım kendiliğinden kapanmaz — tek çare elle silmektir.</para>
///
/// <para><b>Neden ayrı tablo (kolon değil):</b> hakem, <c>CustomerBackups</c>
/// üzerindeki toplamın kendisi olamaz — yarışın sebebi zaten <i>henüz var
/// olmayan</i> satırlar. Sürüm damgasını <c>Customer</c> satırına koymak ise
/// profil güncellemesi gibi ilgisiz akışları da eşzamanlılık kontrolüne sokup
/// yapay 409'lar üretirdi.</para>
///
/// <para><b>Neden bayt toplamı burada TUTULMUYOR:</b> denormalize bir toplam,
/// silme yollarının hepsinde (elle silme, saklama budaması, KVKK temizliği,
/// yetim toplayıcı) azaltılmak zorunda kalırdı; biri unutulunca sayı kayar ve
/// müşteri var olmayan doluluk yüzünden kilitlenir. Gerçeğin kaynağı
/// <c>SUM(SizeBytes)</c> olarak kalıyor; bu satır yalnızca <b>kilit</b>.</para>
///
/// <para><b>RowVersion:</b> aynı müşteri için iki eşzamanlı yükleme sayacı aynı
/// sürümde okur; <c>SaveChanges</c>'te yalnız biri geçer, kaybeden 409 alıp
/// yeniden dener ve bu kez GÜNCEL toplamı görür. Emsal:
/// <see cref="BarcodeCounter"/>.</para>
/// </summary>
public class BackupQuotaCounter
{
    /// <summary>Birincil anahtar; müşteri başına tek satır.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>
    /// Her başarılı yüklemede artan sayaç. Tek işlevi <c>UPDATE</c> ifadesine
    /// yazılacak bir kolon vermek: yalnızca anahtar ve damgadan oluşan bir
    /// satırda EF güncelleme üretemez, dolayısıyla damga da hiç kontrol
    /// edilmezdi.
    /// </summary>
    public long Ticket { get; set; }

    /// <summary>Eşzamanlılık damgası; SQL Server <c>rowversion</c>.</summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public Customer? Customer { get; set; }
}
