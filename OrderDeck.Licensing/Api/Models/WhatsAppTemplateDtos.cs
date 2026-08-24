namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF → LicenseServer WhatsApp template push request (2026-05-15).
/// </summary>
public sealed record WhatsAppTemplatesRequest(
    string PaymentTemplate,
    string ShippingWonTemplate);

/// <summary>Server response — echo + server timestamp.</summary>
public sealed record WhatsAppTemplatesDto(
    string PaymentTemplate,
    string ShippingWonTemplate,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Meta'da ONAYLI bir şablon — ayar ekranındaki şablon seçicinin tek veri
/// kaynağı. Sunucudaki
/// <c>LicensesWhatsAppApprovedTemplatesController.TemplateDto</c> ile bire bir.
///
/// <para>Yukarıdaki <see cref="WhatsAppTemplatesRequest"/> ile karıştırılmamalı:
/// o, WPF'in <c>wa.me</c> bağlantısına yazdığı serbest metin kalıbı ve Meta'yla
/// hiç ilgisi yok. Bu ise Meta'nın onayladığı, 24 saatlik pencere kapalıyken
/// gönderilebilen tek mesaj türü.</para>
///
/// <para><see cref="BodyText"/> taşınıyor çünkü şablonu yalnız adından
/// seçtirmek, içeriğini görmeden faturalı mesaj göndertmek demek.
/// <see cref="ParameterCount"/> gövdedeki <c>{{1}}…{{n}}</c> sayısı: yayıncının
/// kurduğu alan eşlemesi bu uzunlukta olmak zorunda.
/// <see cref="UnsupportedReason"/> doluysa şablon listede görünür ama
/// seçilemez — gizlemek yayıncıya şablonun hiç onaylanmadığını düşündürürdü.</para>
/// </summary>
public sealed record ApprovedTemplateDto(
    string Name,
    string Language,
    string Category,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons,
    int ParameterCount,
    IReadOnlyList<string> ParameterExamples,
    string? UnsupportedReason);
