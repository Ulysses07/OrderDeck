using System;

namespace LiveDeck.Core.Customers;

/// <summary>Phase 4g: WhatsApp ödeme isteme mesajı için template input.</summary>
public sealed record PaymentContext(
    string DisplayName,
    decimal TotalAmount,
    DateTime StreamDate,
    string? Iban,
    string? AccountHolder,
    string? Papara);
