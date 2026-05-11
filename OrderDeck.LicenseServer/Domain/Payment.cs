namespace OrderDeck.LicenseServer.Domain;

public enum PaymentStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// Customer'ın yayıncıya yaptığı banka transferinin OrderDeck kaydı. PDF
/// dekont parse sonrası (PR'da yok — sonraki faz) veya WPF App manuel
/// girişi (yine sonraki faz) ile oluşur. Mobile Panel app burada bekleyenleri
/// listeler, onayla/reddet aksiyonları işler.
///
/// LicenseId üzerinden tenant izolasyonu: yayıncı sadece kendi lisansının
/// payments'ını görür.
/// </summary>
public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Dekontu yatıran kişinin adı (PDF parse sonucu).
    /// Customer kayıt ismi ile eşleşmeyebilir (eş/aile hesabından ödeme).</summary>
    public string PayerName { get; set; } = "";

    public decimal Amount { get; set; }
    public DateTimeOffset PaidAt { get; set; }

    /// <summary>Bankanın dekont referans numarası. Duplicate dedektörü için
    /// unique anahtar (license bazında).</summary>
    public string ReferansNo { get; set; } = "";

    /// <summary>PDF dosyasının SHA-256 hash'i. Aynı dosya tekrar yüklenirse
    /// reddetmek için. PDF kendisi saklanmıyor (privacy + storage).</summary>
    public string? PdfHash { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByCustomerId { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public Guid? RejectedByCustomerId { get; set; }
    public string? RejectReason { get; set; }
}
