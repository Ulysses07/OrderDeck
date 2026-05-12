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
    /// açık Shipment invariant'ı bu method tarafından korunur.
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
    /// </summary>
    public ShipmentDecisionContext EvaluateAfterPayment(string customerId, bool allLabelsPaid)
    {
        if (!allLabelsPaid)
            return ShipmentDecisionContext.Silent(customerId);

        var shipment = _shipments.GetOpenByCustomer(customerId);
        if (shipment is null)
            return ShipmentDecisionContext.Silent(customerId);

        var shipping = _settings().Shipping;
        if (!shipping.IsEnabled)
            return new ShipmentDecisionContext(
                Shipment: shipment,
                AllLabelsPaid: true,
                ThresholdReached: false,
                AmountToThreshold: 0m,
                ShouldPrompt: false);

        var threshold = shipping.FreeShippingThreshold!.Value;
        var reached = shipment.CumulativeAmount >= threshold;
        var remaining = reached ? 0m : threshold - shipment.CumulativeAmount;

        return new ShipmentDecisionContext(
            Shipment: shipment,
            AllLabelsPaid: true,
            ThresholdReached: reached,
            AmountToThreshold: remaining,
            ShouldPrompt: true);
    }

    /// <summary>
    /// Vendor'un modal'da verdiği kararı Shipment state machine'ine uygular.
    /// Geçişler spec'te tanımlı; geçersiz transition InvalidOperationException
    /// fırlatır.
    /// </summary>
    public Shipment ApplyDecision(string shipmentId, ShipmentDecision decision)
    {
        var shipment = _shipments.GetById(shipmentId)
            ?? throw new InvalidOperationException($"Shipment {shipmentId} not found.");

        if (shipment.Status is ShipmentStatus.Shipped)
            throw new InvalidOperationException(
                $"Shipment {shipmentId} is already Shipped (terminal).");

        var now = _nowUnix();
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

        _shipments.Update(updated);
        return updated;
    }
}

/// <summary>
/// Vendor'a sunulacak modal'ın input'u. ShouldPrompt=false ise modal
/// açılmaz (sessiz akış: müşterinin henüz ödemediği Label'ları var, veya
/// kargo özelliği kapalı). ShouldPrompt=true ise UI ThresholdReached'a
/// göre hangi modal varyantını göstereceğine karar verir.
/// </summary>
public sealed record ShipmentDecisionContext(
    Shipment? Shipment,
    bool AllLabelsPaid,
    bool ThresholdReached,
    decimal AmountToThreshold,
    bool ShouldPrompt)
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
