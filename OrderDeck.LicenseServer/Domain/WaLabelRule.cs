using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// "Şu olay olduğunda şu etiketi yapıştır" kuralı. Olay sabit
/// (<see cref="WaLabelEvent"/>), etiket dinamik.
///
/// <para>(LicenseId, EventKey) benzersiz: bir olay en fazla bir etikete
/// bağlanır. Çoklu eşleme istenirse yayıncı olayı değil etiketi çoğaltır —
/// aksi hâlde tek bir ödeme onayı sohbete üç etiket birden yapıştırırdı.</para>
/// </summary>
public sealed class WaLabelRule
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public WaLabelEvent EventKey { get; set; }

    public Guid WaLabelId { get; set; }
    public WaLabel WaLabel { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
