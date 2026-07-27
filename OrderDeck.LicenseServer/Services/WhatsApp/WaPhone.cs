using System.Linq;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp numarası kanonikleştirme. Meta'nın <c>wa_id</c> formatı: yalnız
/// rakamlar, ülke kodu dahil, '+' yok (ör. "905321234567").
///
/// <para>Tek yerde tutulur çünkü <c>WaConversation</c>'daki (LicenseId, CustomerPhone)
/// unique index'i bu forma dayanır — "+90 532..." ve "90532..." aynı sohbet olmalı.</para>
/// </summary>
public static class WaPhone
{
    /// <summary>Rakam dışındaki her şeyi atar. Boş/whitespace girdi için "" döner.</summary>
    public static string Canonical(string? phone) =>
        string.IsNullOrWhiteSpace(phone)
            ? string.Empty
            : new string(phone.Where(char.IsDigit).ToArray());
}
