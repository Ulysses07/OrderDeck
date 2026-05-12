# Kümülatif kargo eşik tetikleyicisi — Tasarım

**Tarih**: 2026-05-12
**Bağlam**: [project_shipping_threshold_pending memory](../../../../.claude/projects/C--Users-burak-source-repos-LiveDeck/memory/project_shipping_threshold_pending.md) — 2026-05-11 brainstorm'un teknik tasarım çıktısı.

## Amaç

Operatör (yayıncı) bir müşterinin ücretsiz kargo eşiğini geçtiğini fark etmeden manuel
kontrol etmeye çalışıyor. Bu spec, dekont onaylanır onaylanmaz sistemin müşterinin
açık kargo dosyasını otomatik kontrol edip operatörü yönlendirmesini tanımlar.

## İş kuralları

**Sabit ayarlar** (per-license, `AppSettings.Shipping`):
- `FreeShippingThreshold` (decimal, örn. 5000 TL)
- `ShippingFee` (decimal, örn. 150 TL)
- `IsEnabled` (bool — kargo özelliği aktif mi)

## Trigger akışı

```
Yeni Payment.Confirmed (dekont eşleşti / mobile onay)
  ↓
Müşterinin TÜM Label'ları ödenmiş mi?
  HAYIR → sessiz: kargolama tetiklenmez
  EVET → ↓
  Müşterinin açık Shipment'ı var mı?
    HAYIR → yeni Shipment oluştur (Status=Pending), o yayındaki Label'ları bağla
    EVET → açık Shipment'a yeni yayın Label'larını ekle
  ↓
  Shipment.CumulativeAmount >= FreeShippingThreshold ?
    EVET → operatöre modal:
       "Toplam alımınız X TL. Ücretsiz kargo kazandınız. Kargolayalım mı?"
       [ Evet, kargolansın ]   [ Beklemeye devam ]
    HAYIR → operatöre modal:
       "Müşteri Ayşe, toplam X TL, kargo eksik. Ne yapılsın?"
       [ Kargo beklesin ]   [ Alıcı ödemeli kargo ]
       [ Kargolansın (kargo ücretiyle) ]
```

**"Beklemeye devam" sonrası**: Her yeni dekont onayında threshold tekrar kontrol
edilir, modal yine açılır (kullanıcı onayı: her seferinde sor).

## Veri modeli

### Yeni entity: `Shipment`

```csharp
public sealed class Shipment
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public ShipmentStatus Status { get; set; }   // Pending | Held | RecipientPays | Shipped
    public DateTime CreatedAt { get; set; }
    public DateTime? HeldAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public decimal CumulativeAmount { get; set; } // cached sum, denormalize for perf
    public ICollection<Label> Labels { get; set; }
}

public enum ShipmentStatus
{
    Pending,        // yeni oluştu, henüz vendor karar vermedi
    Held,           // vendor "beklet" dedi, eşik bekliyor
    RecipientPays,  // vendor "alıcı ödemeli" dedi
    Shipped         // kargocuya verildi, kapalı
}
```

### Mevcut entity değişiklikleri

- `Label.ShipmentId` (Guid?, FK) — etiket hangi Shipment'a ait
- `Label.IsShippingFee` (bool) — kargo ücreti satırı mı (etiket render'da farklı gösterim)

### Cumulative hesabı

Müşteri başına en fazla bir "açık" (Pending/Held) Shipment olur. Yeni yayında alım yapılırsa:
- Açık Shipment varsa → Label'lar ona eklenir, `CumulativeAmount += yeni tutar`
- Yoksa → yeni Shipment (Status=Pending) oluşturulur

Shipped olduğunda → o Shipment kapanır, sonraki alım yeni Shipment açar.

## Modal flow detayı

### Senaryo 1 — Threshold AŞILDI

```
[Modal: "Ücretsiz kargo kazanıldı"]
─────────────────────────────────
Müşteri: Ayşe Yılmaz
Açık siparişler:
  • 01.05 yayını — 2000 TL
  • 05.05 yayını — 1500 TL
  • 10.05 yayını — 1800 TL
Toplam: 5300 TL ✓ (5000 TL eşiği geçti)

[ Evet, kargolansın ]   [ Beklemeye devam ]
```

**Evet** → `Shipment.Status = Shipped`, `ShippedAt = now`. Tüm bağlı Label'lar tek
print job'a düşer (mevcut `LabelPrinter` flow'una entegre). Müşteriye WhatsApp/SMS
"kazandınız" mesajı (sadece bu senaryoda).

**Beklemeye devam** → state korunur. Bir sonraki Payment.Confirmed'da yine sorulur.

### Senaryo 2 — Threshold AŞILMADI

```
[Modal: "Kargo eksik"]
─────────────────────
Müşteri: Mehmet Demir
Toplam: 1200 TL  (5000 TL eşiğine 3800 TL kaldı)
Kargo ücreti: 150 TL eksik

[ Kargo beklesin ]   [ Alıcı ödemeli ]   [ Kargolansın (kargo ücretiyle) ]
```

- **Kargo beklesin** → `Shipment.Status = Held`, `HeldAt = now`
- **Alıcı ödemeli** → `Shipment.Status = RecipientPays`. Etiket print'te kırmızı
  "ALICI ÖDEMELİ — 150 TL" yazısı (mevcut `RecipientPaysActive` ile aynı flow)
- **Kargolansın** → vendor müşteriye kargo farkını sözlü almış demektir; Status=Shipped

## Müşteri bilgilendirmesi

WhatsApp/SMS mesajı SADECE threshold aşıldığında ve vendor "Evet kargolansın" dediğinde
gönderilir. Spam riskini minimumda tutmak için.

Mevcut `WhatsAppMessageBuilder` template sistemine yeni placeholder/template:
```
{musteri_adi}, toplam {kumulatif_tutar} TL alımınız ile ücretsiz kargo hakkı
kazandınız! Siparişiniz yarın kargoya verilecek. {takip_no}
```

## UI değişiklikleri

### WPF App

1. **Settings dialog**: `Shipping` tab — FreeShippingThreshold + ShippingFee + IsEnabled toggle
2. **Yeni dialog**: `ShipmentDecisionDialog.xaml` — yukarıdaki 2 modal senaryoyu render eder
3. **Müşteri profil ekranı**: "Bekleyen kargo: X TL / Y etiket" badge
4. **Yeni view**: `PendingShipmentsView.xaml` — tüm Held + RecipientPays Shipment'ları liste

### Mobile Panel

1. **Yeni tab**: "Bekleyen Kargolar" — `GET /api/shipments?status=held`
2. **Yeni tab/filter**: "Alıcı Ödemeli" — `GET /api/shipments?status=recipientpays`
3. **Dekont kuyruğu**: eksik kargo işareti (Shipment Held/RecipientPays ise küçük rozet)

## Servisler

### `ShipmentService` (yeni — `OrderDeck.Core`)

```csharp
public interface IShipmentService
{
    Shipment GetOrCreateOpenShipment(Guid customerId);
    void AttachLabels(Guid shipmentId, IEnumerable<Label> labels);
    ShipmentDecisionContext EvaluateAfterPayment(Guid customerId);
    void ApplyDecision(Guid shipmentId, ShipmentDecision decision);
}

public sealed record ShipmentDecisionContext(
    Shipment Shipment,
    bool AllLabelsPaid,
    bool ThresholdReached,
    decimal AmountToThreshold);

public enum ShipmentDecision
{
    ShipNow, Hold, RecipientPays
}
```

### `PaymentService` entegrasyonu

`PaymentService.ConfirmPayment(...)` sonunda:
```csharp
var ctx = _shipmentService.EvaluateAfterPayment(customerId);
if (ctx.AllLabelsPaid)
{
    // UI thread'e dispatch — modal göster
    _shipmentDecisionPrompt.Show(ctx);
}
```

## Implementation sırası (5 PR)

| PR | Scope | Bağımlılık |
|----|-------|-------------|
| **A** | `AppSettings.Shipping` + Settings UI input | (yok) |
| **B** | `Label.IsShippingFee` + Shipment entity + migration + repository | A |
| **C** | `IShipmentService` + `PaymentService` hook + `ShipmentDecisionDialog` (WPF) | B |
| **D** | Mobile Panel: bekleyen kargo + alıcı ödemeli tab'ları | C |
| **E** | Threshold "kazandı" WhatsApp template + RecipientPays etiket render kırmızı yazı | D |

Toplam ~3-4 hafta efor, payment sync trilogy bittikten sonra başlanır.

## Trade-off'lar

- **Cached CumulativeAmount denormalize**: her Label eklendiğinde update edilir. Tutarsızlık riski (örn. Label iptal edilirse) için **invariant testler** + nightly reconciliation job düşünülmeli (Faz 2).
- **Multiple open Shipment senaryosu**: Spec'e göre müşteri başına en fazla 1 açık Shipment. Eğer race condition ile 2 oluşursa (örn. eşzamanlı 2 yayın), service layer'da `SELECT FOR UPDATE` veya optimistic concurrency token şart.
- **"Beklemeye devam"yu her seferinde sorma**: Kullanıcı kararı kabul edildi — vendor inatçı bir UX bekliyor, kararını her dekontta bilinçli vermek istiyor. Notification fatigue riski varsa Faz 2'de "X kez beklet'ten sonra sorma" hint eklenebilir.
- **Customer iadesi / red**: Faz 1 scope dışı. Eğer iadesi yapılan Label CumulativeAmount'a sayılmamalı — Faz 2'de Label.Status = Cancelled gibi bir flag eklenip cumulative hesabında dışlanır.
- **Migration**: mevcut Customer'ların açık Label'larını migration script'iyle Shipment'lara dönüştürmek gerek. Tek Shipment per customer per "açık etiket grubu" mantığıyla.

## Doğrulama kriterleri

1. **Birim test**: `ShipmentService.EvaluateAfterPayment` — 6+ scenario (threshold üstü/altı, kısmen ödenmiş, hiç ödenmemiş, RecipientPays, Held'den geçiş, Shipped sonrası yeni Shipment).
2. **Integration test**: `PaymentService.ConfirmPayment` sonrası `IShipmentDecisionPrompt` mock'a doğru `ShipmentDecisionContext`'in gittiğini doğrula.
3. **Manuel smoke**:
   - Müşteri 3 yayında alım → her birinde "beklet" → toplam 5300 → 3. dekontta "kazandı" modal'ı
   - Müşteri 1 yayında 6000 TL alım → tek dekont → "kazandı" modal'ı direkt
   - Müşteri 800 TL → "beklet" → ikinci yayın 500 TL → toplam 1300 → "kargo eksik" modal (eşik aşılmadı)
   - "Beklemeye devam" → bir sonraki dekontta yine modal
4. **Performance**: 1000 müşteri × 50 etiket dataset'inde `GET /api/shipments?status=held` < 100ms.
