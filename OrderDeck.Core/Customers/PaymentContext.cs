using System;

namespace OrderDeck.Core.Customers;

/// <summary>Phase 4g: WhatsApp ödeme isteme mesajı için template input.
/// Kargo entegrasyon (2026-05-12): ProductTotal / ShippingFee / ShippingNote
/// eklendi — template yeni placeholder'larla kargo durumunu yansıtabilir.</summary>
public sealed record PaymentContext(
    string DisplayName,
    decimal TotalAmount,
    DateTime StreamDate,
    string? Iban,
    string? AccountHolder,
    string? Papara,
    // Kargo entegrasyon (sona, geriye uyumlu — eski caller'lar default değer alır)
    decimal ProductTotal = 0m,
    decimal? ShippingFee = null,
    string ShippingNote = "");
