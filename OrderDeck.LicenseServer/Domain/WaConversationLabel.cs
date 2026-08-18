namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Sohbete yapıştırılmış etiket. Bir sohbet BİRDEN ÇOK etiket taşıyabilir.
///
/// <para>Etiketler yalnız ELLE kaldırılır — sunucu hiçbirini otomatik
/// düşürmez. Ödeme onayı "Dekont geldi" etiketini silmez; yayıncı işi
/// bitirdiğinde kendisi kaldırır. Bunun sonucu "iş var" etiketlerinin
/// birikebilmesidir, o yüzden panelde kaldırma tek tık olmalı.</para>
/// </summary>
public sealed class WaConversationLabel
{
    public Guid Id { get; set; }

    /// <summary>
    /// Denormalize — <c>WaMessage.LicenseId</c> ile aynı gerekçe artı bir tane
    /// daha: silme yolu. Sohbet de etiket de License'tan cascade siliniyor;
    /// ara tablo ikisinden birine cascade bağlansaydı License silinirken SQL
    /// Server'a iki cascade yolu çıkardı. Cascade YALNIZ buradan (License) —
    /// diğer iki FK <c>NoAction</c>, temizlik açıkça yapılıyor.
    /// </summary>
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid ConversationId { get; set; }
    public WaConversation Conversation { get; set; } = null!;

    public Guid WaLabelId { get; set; }
    public WaLabel WaLabel { get; set; } = null!;

    /// <summary>"auto" (kural yapıştırdı) | "manual" (yayıncı yapıştırdı).</summary>
    public string Source { get; set; } = "auto";

    public DateTimeOffset CreatedAt { get; set; }
}
