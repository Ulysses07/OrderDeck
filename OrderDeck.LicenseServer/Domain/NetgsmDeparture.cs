namespace OrderDeck.LicenseServer.Domain;

/// <summary>Ayrılan yayıncının saklama takvimi (§6 + 6563 m.13). Kişisel veri
/// İÇERMEZ — telefon/isim yok, yalnız marka ve tarihler.
/// LicenseId FK DEĞİL ve navigasyon eklemeyin (IysConsentEvent'teki kararın
/// aynısı): lisans KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.</summary>
public sealed class NetgsmDeparture
{
    public Guid Id { get; set; }

    /// <summary>FK'sız referans; Faz 3'te kampanya imhası için. Lisans
    /// silinmişse null kalabilir — o durumda yalnız olay imhası yapılır.</summary>
    public Guid? LicenseId { get; set; }

    public string BrandCode { get; set; } = "";

    /// <summary>Ayrılış anı = hesabın Disabled'a geçtiği an (DisabledAt).
    /// m.13 3 yıl BU tarihten sayılır.</summary>
    public DateTimeOffset DepartedAt { get; set; }

    /// <summary>30. gün temizliğinin gerçekleştiği an (IysConsent + hesap silindi).</summary>
    public DateTimeOffset ConsentsDeletedAt { get; set; }

    /// <summary>3 yıllık imha tamamlandığında damgalanır; null = imha bekliyor.</summary>
    public DateTimeOffset? PurgedAt { get; set; }
}
