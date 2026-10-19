namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşteri bakiye ledger satırı (immutable, append-only). CustomerBalance.Balance
/// her zaman SUM(Amount) ile tutar. Audit hem yayıncı hem shopper'a açık.
/// </summary>
public sealed class CustomerBalanceTransaction
{
    /// <summary>R4-03: <see cref="SaleScope"/> sınırı. En uzun gerçek kapsam
    /// <c>"legacy:"</c> + 32 hane = 39; 128 rahat bir tavan. Sunucu bunu
    /// aşan kapsamı 400 ile reddeder — sessizce kırpmak, iki farklı satışı
    /// aynı kimliğe indirger ve yanlış benimsemeye yol açardı.</summary>
    public const int SaleScopeMaxLength = 128;

    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid WpfCustomerId { get; set; }
    public WpfCustomerProjection WpfCustomer { get; set; } = null!;

    /// <summary>+ bakiye eklendi, − kullanıldı / geri alındı.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Transaction türü. Display tarafında karşılığı:
    ///   - "refund-full":     "Hatalı ürün iadesi" (yayıncı kaynaklı, tam tutar)
    ///   - "refund-net":      "Müşteri iadesi (kargo düşülmüş)"
    ///   - "purchase-deduction": "Ödeme isteği — bakiye kullanıldı"
    ///   - "manual-adjustment": "Yayıncı manuel ayar" (+ veya −)
    ///   - "reversal":        "Önceki transaction geri alındı" (yanlış kayıt iptal)
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Refund'larda orijinal sipariş tutarı (audit). Diğerlerinde null.</summary>
    public decimal? OriginalAmount { get; set; }

    /// <summary>refund-net'te kargo olarak düşülen tutar (audit). Diğerlerinde null.</summary>
    public decimal? ShippingDeducted { get; set; }

    /// <summary>Free text — yayıncı notu, opsiyonel.</summary>
    public string? Reason { get; set; }

    /// <summary>Reversal ise hangi transaction'ı iptal ediyor.</summary>
    public Guid? ReversesTransactionId { get; set; }

    /// <summary>
    /// R4-03: satışın <b>kalıcı kimliği</b> — istemcinin kapsam anahtarı
    /// (<c>"session:{id}"</c> | <c>"cumulative"</c> | <c>"legacy:{key}"</c>).
    ///
    /// <para>Neden var: bu satırdan önce satışın kimliği YALNIZ yerelde
    /// yaşıyordu (istemcinin ürettiği rastgele idempotency anahtarı, yerel
    /// SQLite satırında). Eski bir yedek geri yüklenince o anahtar yok
    /// oluyor, uzak defter hatırlamaya devam ediyor ve aynı satış ikinci kez
    /// düşülüyordu. Kimlik, kaybolabilen tarafta değil kaybolmayan tarafta
    /// duruyor artık.</para>
    ///
    /// <para>Nullable: eski istemciler bu alanı hiç göndermez ve
    /// göndermedikleri sürece davranışları değişmez. Geçmiş satırlar da null
    /// kalır — göç geriye dönük doldurmaz, çünkü hangi satırın hangi satışa
    /// ait olduğu bilgisi sunucuda hiç yoktu (R4-03'ün kendisi bu).</para>
    ///
    /// <para>Yalnız <c>purchase-deduction</c> satırlarında anlamlı; iade ve
    /// reversal satırları kapsam taşımaz.</para>
    /// </summary>
    public string? SaleScope { get; set; }

    /// <summary>
    /// Ortak bakiye (spec 2026-10-11 §2, §3.2): çok bacaklı bir çekimin bacaklarını çapaya bağlar. Değer çapanın
    /// <see cref="Id"/>'si; çapada kendi <see cref="Id"/>'si. <c>null</c> = eski, tek bacaklı satır — kendi başına
    /// bir grup. Çapa ⇔ <c>DrawGroupId == null || DrawGroupId == Id</c> (<c>PersonBalance.IsAnchor</c>).
    ///
    /// <para>Çekimleri (havuz kipindeki düşüm, elle eksi, çekim türü iptal) S2 yazar; S1 bu biçimi yalnız OKUR ve
    /// çok bacaklı bir çekimin grup geri almasını yazar (geri alma satırları geri alma çapasına bağlanır). Havuz kipi
    /// hiç açılmamış bir lisansta dolu satır yoktur. Bacaklarda <see cref="SaleScope"/>, <see cref="OriginalAmount"/>
    /// ve <see cref="Reason"/> boştur; açıklama çapada durur.</para>
    /// </summary>
    public Guid? DrawGroupId { get; set; }

    /// <summary>Bu transaction'ı oluşturan yayıncı (Customer) — audit.</summary>
    public Guid CreatedByCustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
