using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Kümülatif kargo eşik tetikleyici servisi (PR-C).
/// Spec: docs/superpowers/specs/2026-05-12-cumulative-shipping-trigger-design.md
///
/// Shipment yaşam döngüsünü yönetir, Label'ları aktif Shipment'a attach eder,
/// payment onaylanınca threshold check ile vendor'a sunulacak modal context'i
/// hesaplar. Pure business logic — UI dialog gösterimi caller sorumluluğunda
/// (PR-C/2'de WPF integration).
///
/// "AllLabelsPaid" hesabı caller tarafından sağlanır (Payment-Customer
/// linkage runtime'da PaymentMatcher üzerinden yapılıyor, service buna
/// bağımlı olmasın diye dışsallaştırıldı).
/// </summary>
public sealed class ShipmentService
{
    private readonly ShipmentRepository _shipments;
    private readonly LabelRepository _labels;
    private readonly Func<AppSettings> _settings;
    private readonly Func<long> _nowUnix;

    public ShipmentService(
        ShipmentRepository shipments,
        LabelRepository labels,
        Func<AppSettings> settings,
        Func<long>? nowUnix = null)
    {
        _shipments = shipments;
        _labels = labels;
        _settings = settings;
        _nowUnix = nowUnix ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Müşterinin açık (Pending veya Held) Shipment'ını döndürür; yoksa yeni
    /// Pending Shipment oluşturup persist eder. Müşteri başına en fazla 1
    /// açık Shipment invariant'ı bu method tarafından korunur — yerel taşıma
    /// (U12) iki dosyayı aynı kişide bırakabilir; o zaman en yenisi seçilir
    /// (<see cref="ShipmentRepository.GetOpenByCustomer"/>), eşik ve karar ise
    /// hepsini tek havuz sayar (<see cref="EvaluateAfterPayment"/>,
    /// <see cref="ApplyDecision"/>).
    /// </summary>
    public Shipment GetOrCreateOpenShipment(string customerId)
    {
        var existing = _shipments.GetOpenByCustomer(customerId);
        if (existing is not null) return existing;

        var fresh = new Shipment(
            Id: Guid.NewGuid().ToString("N"),
            CustomerId: customerId,
            Status: ShipmentStatus.Pending,
            CreatedAt: _nowUnix(),
            HeldAt: null,
            ShippedAt: null,
            CumulativeAmount: 0m);
        _shipments.Insert(fresh);
        return fresh;
    }

    /// <summary>
    /// Verilen Label id'lerini Shipment'a bağlar ve CumulativeAmount'u
    /// günceller. Label.IsTentativeBackup veya Label.IsShippingFee olanlar
    /// dahil — caller filtreleme sorumluluğundadır (kümülatif kapsam = açık
    /// Label'ların hepsi, ürün/kargo ayrımı yapılmaz; spec brainstorm kararı).
    /// </summary>
    public Shipment AttachLabels(string shipmentId, IEnumerable<string> labelIds)
    {
        var ids = labelIds.ToList();
        var shipment = _shipments.GetById(shipmentId)
            ?? throw new InvalidOperationException($"Shipment {shipmentId} not found.");

        if (shipment.Status is ShipmentStatus.Shipped)
            throw new InvalidOperationException(
                $"Cannot attach labels to a Shipped Shipment ({shipmentId}).");

        decimal added = 0m;
        foreach (var labelId in ids)
        {
            var label = _labels.GetById(labelId)
                ?? throw new InvalidOperationException($"Label {labelId} not found.");
            if (label.ShipmentId == shipmentId) continue; // idempotent
            if (label.ShipmentId is not null)
                throw new InvalidOperationException(
                    $"Label {labelId} already attached to Shipment {label.ShipmentId}.");

            _shipments.AttachLabel(shipmentId, labelId);
            added += label.Price;
        }

        if (added == 0m) return shipment;

        var updated = shipment with { CumulativeAmount = shipment.CumulativeAmount + added };
        _shipments.Update(updated);
        return updated;
    }

    /// <summary>
    /// Payment.Confirmed sonrası vendor'a hangi modal gösterilmeli karar
    /// verir. Caller önce müşterinin TÜM Label'larının ödenmiş olduğunu
    /// (<paramref name="allLabelsPaid"/>) tespit etmeli — eğer false ise
    /// modal gösterilmez (sessiz, kargolama tetiklenmez).
    ///
    /// allLabelsPaid=true ise: açık Shipment'a yeni Label'lar zaten attach
    /// edilmiş olmalı (caller AttachLabels çağırdı). Threshold check
    /// yapılır → ThresholdReached field'ı UI hangi modal varyantını
    /// göstermeli belirler.
    ///
    /// <para>U12: yerel taşıma kişide birden çok açık dosya bırakabilir. Eşik kişinin
    /// BÜTÜN açık dosyalarının toplamından hesaplanır ve
    /// <see cref="ShipmentDecisionContext.PooledAmount"/>'ta taşınır (çekmece bu tutarı
    /// gösterir); dönen <c>Shipment</c> kararın verileceği en yeni dosyadır, satırın aynası.
    /// Tek dosyada havuz dosyanın kendi tutarıdır — davranış değişmez.</para>
    /// </summary>
    public ShipmentDecisionContext EvaluateAfterPayment(string customerId, bool allLabelsPaid)
    {
        if (!allLabelsPaid)
            return ShipmentDecisionContext.Silent(customerId);

        var open = _shipments.GetAllOpenByCustomer(customerId);
        if (open.Count == 0)
            return ShipmentDecisionContext.Silent(customerId);
        var shipment = open[0];
        var pooled = open.Sum(s => s.CumulativeAmount);

        var shipping = _settings().Shipping;
        if (!shipping.IsEnabled)
            return new ShipmentDecisionContext(
                Shipment: shipment,
                AllLabelsPaid: true,
                ThresholdReached: false,
                AmountToThreshold: 0m,
                ShouldPrompt: false,
                PooledAmount: pooled);

        var threshold = shipping.FreeShippingThreshold!.Value;
        var reached = pooled >= threshold;
        var remaining = reached ? 0m : threshold - pooled;

        return new ShipmentDecisionContext(
            Shipment: shipment,
            AllLabelsPaid: true,
            ThresholdReached: reached,
            AmountToThreshold: remaining,
            ShouldPrompt: true,
            PooledAmount: pooled);
    }

    /// <summary>
    /// Vendor'un modal'da verdiği kararı Shipment state machine'ine uygular.
    /// Geçişler spec'te tanımlı; geçersiz transition InvalidOperationException
    /// fırlatır.
    ///
    /// <para>U12: hedef dosya karardan ÖNCE açıksa (Pending/Held) karar kişinin yerel
    /// taşımadan kalan öbür açık dosyalarına da aynen uygulanır (tek havuz —
    /// <see cref="EvaluateAfterPayment"/>): fazla dosya gönder / alıcı öder kararında kapanır
    /// ve durum kendiliğinden düzelir; bekletmede hepsi bekler, sonraki etiketler en yeni
    /// dosyaya gider, eşik yine toplamdan. Aksi hâlde en yeni dosya kapandıktan sonra eski
    /// dosya eski toplamıyla sonraki etiketleri alır ve eşik kararı (ve "kazandın" mesajı)
    /// yanlış olurdu. Zaten kapalı bir dosyaya (alıcı öder) verilen karar havuza dokunmaz.
    /// Hepsi TEK işlemde: yarım uygulanmış karar havuzu bölerdi. Tek dosyada davranış
    /// değişmez.</para>
    /// </summary>
    /// <returns>Kararın uygulandığı dosya — satırın aynası.</returns>
    public Shipment ApplyDecision(string shipmentId, ShipmentDecision decision)
        => ApplyDecision(shipmentId, decision, out _);

    /// <inheritdoc cref="ApplyDecision(string, ShipmentDecision)"/>
    /// <param name="pooledAmount">Kararın kapsadığı dosyaların toplam tutarı ("kazandın"
    /// mesajı bunu söyler); tek dosyada dosyanın kendi tutarı.</param>
    public Shipment ApplyDecision(string shipmentId, ShipmentDecision decision, out decimal pooledAmount)
    {
        using var write = _shipments.BeginWrite();
        var shipment = _shipments.GetById(shipmentId, write)
            ?? throw new InvalidOperationException($"Shipment {shipmentId} not found.");

        if (shipment.Status is ShipmentStatus.Shipped)
            throw new InvalidOperationException(
                $"Shipment {shipmentId} is already Shipped (terminal).");

        var now = _nowUnix();
        var updated = Decide(shipment, decision, now);
        _shipments.Update(updated, write);
        pooledAmount = updated.CumulativeAmount;

        if (shipment.Status is ShipmentStatus.Pending or ShipmentStatus.Held)
        {
            var others = _shipments.GetAllOpenByCustomer(shipment.CustomerId, write)
                .Where(s => !string.Equals(s.Id, shipment.Id, StringComparison.Ordinal));
            foreach (var other in others)
            {
                _shipments.Update(Decide(other, decision, now), write);
                pooledAmount += other.CumulativeAmount;
            }
        }

        // Satırın kendisi (Update Revision'ı artırır, SyncedAt'i düşürür) — bellekteki kopya değil.
        var stored = _shipments.GetById(shipmentId, write)!;
        write.Commit();
        return stored;
    }

    private static Shipment Decide(Shipment shipment, ShipmentDecision decision, long now)
    {
        Shipment updated = decision switch
        {
            ShipmentDecision.ShipNow => shipment with
            {
                Status = ShipmentStatus.Shipped,
                ShippedAt = now
            },
            ShipmentDecision.Hold => shipment with
            {
                Status = ShipmentStatus.Held,
                // HeldAt sadece ilk Held'e geçişte set edilir; idempotent kalsın.
                HeldAt = shipment.HeldAt ?? now
            },
            ShipmentDecision.RecipientPays => shipment with
            {
                Status = ShipmentStatus.RecipientPays
            },
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null)
        };
        return updated;
    }
}

/// <summary>
/// Vendor'a sunulacak modal'ın input'u. ShouldPrompt=false ise modal
/// açılmaz (sessiz akış: müşterinin henüz ödemediği Label'ları var, veya
/// kargo özelliği kapalı). ShouldPrompt=true ise UI ThresholdReached'a
/// göre hangi modal varyantını göstereceğine karar verir.
/// </summary>
/// <param name="Shipment">Kararın verileceği dosya (kişinin en yeni açık dosyası) — satırın aynası.</param>
/// <param name="PooledAmount">U12: eşiğin hesaplandığı tutar — kişinin bütün açık dosyalarının
/// toplamı (yerel taşımadan kalan fazla dosya dahil); tek dosyada dosyanın kendi tutarı.
/// Çekmece bunu gösterir. Sessiz bağlamda 0.</param>
public sealed record ShipmentDecisionContext(
    Shipment? Shipment,
    bool AllLabelsPaid,
    bool ThresholdReached,
    decimal AmountToThreshold,
    bool ShouldPrompt,
    decimal PooledAmount = 0m)
{
    public static ShipmentDecisionContext Silent(string _customerId) =>
        new(Shipment: null, AllLabelsPaid: false, ThresholdReached: false,
            AmountToThreshold: 0m, ShouldPrompt: false);
}

/// <summary>
/// Modal'da vendor'un seçtiği karar. UI sadece ilgili butonları gösterir
/// (ThresholdReached=true → ShipNow + Hold; false → Hold + RecipientPays + ShipNow).
/// </summary>
public enum ShipmentDecision
{
    /// <summary>"Evet, kargolansın" — Shipment kapanır, Label'lar kargoya gider.</summary>
    ShipNow,
    /// <summary>"Beklemeye al" — kümülatif eşik aşılması beklenir.</summary>
    Hold,
    /// <summary>"Alıcı ödemeli" — sticky; etiketler kırmızı yazılı basılır.</summary>
    RecipientPays
}
