namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WPF lokal "Label" entity'sinin server-side replikası. Mobile Panel
/// Siparişler ekranında yayın bazlı sipariş listesini bu tablodan okur.
///
/// "Label" yerine "Order" adı seçildi — server semantically siparişle
/// uğraşıyor, label sadece WPF'te fiziksel etiket karşılığı. LicenseId
/// üzerinden tenant izolasyonu; SessionId opsiyonel olabilir (eski/orphan
/// label'lar için).
///
/// WPF authoritative.
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid? SessionId { get; set; }
    public StreamSession? Session { get; set; }

    /// <summary>WPF lokal CustomerId (hex GUID string). Server tarafı Customer
    /// entity sync edilmiyor — sadece display name + username yeterli.</summary>
    public string CustomerId { get; set; } = "";

    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }

    public string MessageText { get; set; } = "";
    public string? Code { get; set; }
    public decimal Price { get; set; }

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>True ise label normal ürün satışı değil, kargo ücreti satırıdır.</summary>
    public bool IsShippingFee { get; set; }

    /// <summary>True ise backup-promoted ("Y" damga); audit için.</summary>
    public bool IsBackupPromoted { get; set; }

    /// <summary>True ise tentative backup (henüz onaylanmamış yedek); cumulative
    /// hesaplarında dışarda bırakılır.</summary>
    public bool IsTentativeBackup { get; set; }

    /// <summary>
    /// Bağlandığı katalog ürünü. <b>FK DEĞİL</b> — bilerek.
    ///
    /// Gerekçe: sert bir FK, sunucuda silinmiş bir ürüne referans veren tek bir
    /// siparişte tüm senkron paketini 500'e düşürür ve WPF'in outbox'ı sonsuza
    /// dek aynı paketi yeniden dener — yani tek bir kayıt bütün senkronu kilitler.
    /// <c>CustomerId</c> de aynı sebeple FK'sız. Defter tarafındaki
    /// <c>StockMovement</c> ise gerçek FK alıyor; bilinmeyen id'yi
    /// <c>StockLedgerWriter</c> hareketi atlayarak eliyor.
    /// </summary>
    public Guid? ProductId { get; set; }

    /// <summary>
    /// Bağlandığı varyant. Null ise düşüm ürün seviyesinde yapılır — hangi
    /// varyant olduğu bilinmiyor demektir, hata değil.
    /// </summary>
    public Guid? ProductVariantId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Eşzamanlılık jetonu. Her senkron güncellemesinde bir artar; EF üretilen
    /// UPDATE'e <c>WHERE SyncVersion = okunan</c> ekler, yani aynı satıra
    /// paralel giren ikinci istek yazamaz.
    ///
    /// Korunan şey aslında sipariş alanları değil, ondan TÜRETİLEN stok
    /// defteri. <c>StockLedgerWriter</c> "bugüne kadarki hareketler" toplamını
    /// bir kez okuyup farkı yazıyor; iki istek aynı toplamı okursa aynı farkı
    /// iki kez yazar (satılmış siparişin iptali +1 yerine +2 telafi üretir) ve
    /// stok sessizce şişer. Defter yalnız bu uçtan, hep siparişin kendi satırı
    /// da güncellenerek yazıldığı için siparişi kilitlemek deftere de yetiyor:
    /// hareket yazan her yol önce bu sütunu geçmek zorunda.
    ///
    /// Zaman damgası (<see cref="UpdatedAt"/>) jeton olarak seçilmedi: iki
    /// istek aynı saat tikine düşerse jeton sessizce işlevsizleşir. Monoton
    /// sayaçta böyle bir delik yok.
    ///
    /// Neden <c>rowversion</c> değil: o sütun tipi SQL Server'a özgü,
    /// planlanan Postgres göçünde karşılığı yok. Sayacı kendimiz artırınca
    /// davranış sağlayıcıdan bağımsız kalıyor. (Akış imleci
    /// <see cref="ChangeSeq"/> rowversion'dır ve bu bağımlılığı bilerek alır,
    /// müşteri projeksiyonu gibi; Postgres göçü ikisini birlikte çözer. Jeton
    /// bu sütun olarak kalır; satış senkronunda telde <c>Version</c> adıyla
    /// açılır — spec 2026-10-13 §3.1.)
    /// </summary>
    public int SyncVersion { get; set; }

    // ── Yayın ve etiket senkronu (spec 2026-10-13 §2, §3.1). Hepsi boş
    //    olabilir: S1 öncesi satırlarda ve S1 öncesi imajın yazdığı satırlarda
    //    NULL kalırlar; göç geri doldurmaz (onarım işi, §4.9).

    /// <summary>Yedeğin ebeveyni (yerel <c>Label.ParentLabelId</c>). FK değil —
    /// <see cref="ProductId"/> ile aynı gerekçe.</summary>
    public Guid? ParentOrderId { get; set; }

    /// <summary>Bağlı kargo dosyası. FK değil — <see cref="ProductId"/> ile aynı
    /// gerekçe; bağı bilinmeyen sipariş için bkz. <see cref="OriginReplicaId"/>.</summary>
    public Guid? ShipmentId { get; set; }

    /// <summary>Siparişi gönderen kopya. Boşsa kargo bağı bilinmiyor demektir
    /// (Ws geri doldurması hiç gelmedi) ve sipariş <c>attach-shipment</c>
    /// adayı olmaz (M22).</summary>
    public Guid? OriginReplicaId { get; set; }

    /// <summary>Basış grubu ve kâğıttaki sıra (yerel 048).</summary>
    public string? PrintBatchId { get; set; }
    public int? PrintSeq { get; set; }

    /// <summary>"Fiyatı düzelt" damgası (yerel 048).</summary>
    public DateTimeOffset? PriceCorrectedAt { get; set; }

    /// <summary>Ödenmiş etiketin ödendiği fiyat; NULL = ödenmişken hiç
    /// düzeltilmedi (yerel 048).</summary>
    public decimal? PaidPrice { get; set; }

    /// <summary>Oturumun devir anının kopyası: devirde ya da eklemede dolar;
    /// akış süzgeci (DM3).</summary>
    public DateTimeOffset? SessionReleasedAt { get; set; }

    /// <summary>Birim damgaları (sunucu saati, yalnız komut basar, hiçbir yol
    /// silmez). Damgalı birimi bayat push değiştiremez (K2, M3).</summary>
    public DateTimeOffset? PriceSetAt { get; set; }
    public DateTimeOffset? CancelSetAt { get; set; }
    public DateTimeOffset? BackupSetAt { get; set; }
    public DateTimeOffset? ShipmentSetAt { get; set; }

    /// <summary>KVKK metin boşaltmasının anı (§9).</summary>
    public DateTimeOffset? TextScrubbedAt { get; set; }

    /// <summary>SQL Server rowversion — değişiklik akışının imleci (§4.4).
    /// Eşzamanlılık jetonu DEĞİL (jeton <see cref="SyncVersion"/>). InMemory
    /// sağlayıcısında hep 0 kalır; bkz. <see cref="WpfCustomerProjection.ChangeSeq"/>.</summary>
    public long ChangeSeq { get; set; }
}
