namespace OrderDeck.LicenseServer.Domain;

/// <summary>Ayrılan yayıncının saklama takvimi (§6 + 6563 m.13). Kişisel veri
/// İÇERMEZ — telefon/isim yok, yalnız marka ve tarihler.
/// LicenseId FK DEĞİL ve navigasyon eklemeyin (IysConsentEvent'teki kararın
/// aynısı): lisans KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.</summary>
public sealed class NetgsmDeparture
{
    public Guid Id { get; set; }

    /// <summary>FK'sız referans; Faz 3'te kampanya imhası için. Lisans
    /// silinmişse null kalabilir — o durumda yalnız olay imhası yapılır.
    /// Markasız dönemde (<see cref="BrandCode"/> null) DOLUDUR: imhanın tek
    /// anahtarı budur.</summary>
    public Guid? LicenseId { get; set; }

    /// <summary>Ayrılan dönemin İYS marka kodu. <b>null = markasız dönem</b>
    /// (sahip kararı 10, 2026-10-23): doğrulanmış markası olmayan yayıncının
    /// ispat olayları <c>IysConsentEvent.BrandCode</c> null yazılır ve lisansa
    /// bağlıdır; imhaları <see cref="LicenseId"/> ile yapılır.
    ///
    /// <para><b>Tuzak:</b> olay sorgusunda <c>e.BrandCode == dep.BrandCode</c>
    /// YAZMAYIN. Değer null olduğunda EF bunu <c>IS NULL</c>'a çevirir ve
    /// <b>bütün lisansların</b> markasız olaylarını seçer. İki dal ayrı:
    /// marka varsa marka, yoksa lisans + <c>BrandCode IS NULL</c>.</para></summary>
    public string? BrandCode { get; set; }

    /// <summary>Ayrılış anı = hesabın Disabled'a geçtiği an (DisabledAt);
    /// yetim markada ve markasız dönemde gerçek an bilinmediği için TESPİT
    /// anı (markasız dönemde: lisansın silindiğinin fark edildiği koşu).
    /// m.13 3 yıl BU tarihten sayılır.</summary>
    public DateTimeOffset DepartedAt { get; set; }

    /// <summary>30. gün temizliğinin gerçekleştiği an (IysConsent + hesap silindi).
    /// Markasız dönemde silinecek durum satırı yoktur (toplayıcı açmaz); takvimin
    /// açıldığı an yazılır.</summary>
    public DateTimeOffset ConsentsDeletedAt { get; set; }

    /// <summary>3 yıllık imha tamamlandığında damgalanır; null = imha bekliyor.</summary>
    public DateTimeOffset? PurgedAt { get; set; }
}
