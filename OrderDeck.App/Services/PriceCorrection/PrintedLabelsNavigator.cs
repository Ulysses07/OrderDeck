using System;

namespace OrderDeck.App.Services.PriceCorrection;

/// <summary>Rapordan "Basılan etiketler" isteği: yayın ve başlıkta gösterilecek ad/tarih.</summary>
public sealed record PrintedLabelsRequest(string SessionId, string Title, DateTime StreamDate);

/// <summary>
/// K6: yayın bittikten sonra aynı "Basılanlar" görünümü o yayının raporundan açılır. Rapor bir SAYFA,
/// sayfalar çekmece katmanını örtüyor — fiyat düzeltme ve iptal çekmeceleri sayfanın arkasında kalırdı.
/// Bu yüzden rapor görünümü kabuğa devreder: kabuk sayfaları kapatır ve sağ paneldeki "Basılanlar"
/// sekmesini o yayına açar (aynı görünüm, aynı çekmeceler). Tekil; rapor ile kabuk arasındaki tek bağ.
/// </summary>
public sealed class PrintedLabelsNavigator
{
    public event Action<PrintedLabelsRequest>? Requested;

    public void Request(PrintedLabelsRequest request) => Requested?.Invoke(request);
}
