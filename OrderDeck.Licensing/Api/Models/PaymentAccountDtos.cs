namespace OrderDeck.Licensing.Api.Models;

/// <summary>Request body for POST /api/v1/licenses/{licenseId}/payment-account (Faz 0c-1).</summary>
public sealed record SetPaymentAccountRequest(string? Iban, string? AccountHolder);
