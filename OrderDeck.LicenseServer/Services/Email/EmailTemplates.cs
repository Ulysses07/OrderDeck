using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace OrderDeck.LicenseServer.Services.Email;

public static class EmailTemplates
{
    /// <summary>HTML gövdeyi kurar; interpolasyondaki HER değer otomatik
    /// HTML-encode edilir (bkz. <see cref="HtmlBody"/>).
    ///
    /// <para><b>Neden çağrı başına encode değil:</b> bu dosyada ~40 interpolasyon
    /// noktası var ve yeni şablon eklemek rutin bir iş. Tek tek sarmalamak kuralı
    /// yazarın hatırlamasına bağlar — bir kez unutulduğunda sessizce geri gelir.
    /// İşleyici tersine çevirir: encode etmemek için özel çaba gerekir.</para></summary>
    private static string Html(HtmlBody body) => body.ToStringAndClear();

    /// <summary>Deliklerdeki değeri encode eden, sabit metni olduğu gibi geçiren
    /// interpolasyon işleyicisi.
    ///
    /// <para><b>Neden gerekli:</b> müşteri adı kendi belirlediği bir alan ve
    /// <c>POST /api/v1/auth/register</c> herkese açık. Doğrulama e-postası tanımı
    /// gereği DOĞRULANMAMIŞ bir adrese gidiyor, yani saldırgan kurbanın adresiyle
    /// kayıt olup <c>Name</c> alanına HTML koyabiliyor: OrderDeck'in kendi
    /// alan adından, geçerli SPF/DKIM ile, saldırganın yazdığı bir bağlantı
    /// taşıyan gerçek bir e-posta. Sunucu tarafı XSS değil, ama e-posta içerik
    /// bütünlüğü kalmıyor.</para></summary>
    [InterpolatedStringHandler]
    public struct HtmlBody
    {
        /// <summary>Varsayılan encoder ASCII dışındaki HER karakteri sayısal
        /// varlığa çeviriyor: "Rıdvan Özcan" → "R&amp;#305;dvan &amp;#214;zcan".
        /// Doğru HTML ama gövde Türkçe bir metinde okunmaz hâle geliyor ve
        /// gereksiz şişiyor. Tüm Unicode aralığına izin vermek işaretleme
        /// açısından güvenliği değiştirmiyor — <c>&lt; &gt; &amp; " '</c> yine
        /// kaçırılıyor; e-posta zaten UTF-8 gönderiliyor.</summary>
        private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

        private readonly StringBuilder _sb;

        public HtmlBody(int literalLength, int formattedCount)
            => _sb = new StringBuilder(literalLength + formattedCount * 16);

        public void AppendLiteral(string value) => _sb.Append(value);

        /// <summary>URL'ler dahil AYRIM YAPMADAN encode ediliyor. Çift tırnaklı
        /// bir attribute içinde <c>&amp;</c> → <c>&amp;amp;</c> zaten doğru HTML;
        /// ayrıca "bu değer güvenli" muafiyeti açmamak, işleyicinin tüm anlamı.</summary>
        public void AppendFormatted<T>(T value)
            => _sb.Append(Encoder.Encode(value?.ToString() ?? string.Empty));

        internal string ToStringAndClear() => _sb.ToString();
    }

    public static (string Subject, string Html, string Plain) ConfirmEmail(string customerName, string confirmUrl)
    {
        var subject = "OrderDeck — Email adresinizi doğrulayın";
        var plain = $@"Merhaba {customerName},

OrderDeck hesabını doğrulamak için aşağıdaki bağlantıya tıkla:
{confirmUrl}

Bu link 24 saat geçerli.

Sen yapmadıysan bu mesajı görmezden gel.
— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck hesabını doğrulamak için <a href=""{confirmUrl}"">tıkla</a>.</p>
<p>Bu link 24 saat geçerli.</p>
<p style=""color:#888"">Sen yapmadıysan bu mesajı görmezden gel.</p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, html, plain);
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Renewal reminders
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) Renewal14d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        // Renewals are reminders, not deliveries — never put the full key in transit.
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız 14 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınız {dateStr} tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.

Lisans anahtarı: {maskedKey}
Bitiş: {dateStr}

Lisansınızı portaldan yönetin: {portalUrl}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınız <strong>{dateStr}</strong> tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{maskedKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p><a href=""{portalUrl}"">Lisansınızı portaldan yönetin</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal7d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız 7 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınızın bitmesine 7 gün kaldı. Hizmet kesintisi yaşamamak için en kısa sürede yenileyin.

Lisans anahtarı: {maskedKey}
Bitiş: {dateStr}

Yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınızın bitmesine <strong>7 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{maskedKey}</code></p>
<p><a href=""{portalUrl}"">Hemen yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal3d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız 3 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın bitmesine 3 gün kaldı! Hemen yenileyin.

Lisans: {maskedKey}
Bitiş: {dateStr}

{portalUrl}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın bitmesine <strong style=""color:#d97706"">3 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{maskedKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#d97706;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Hemen yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal0d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız bugün sona eriyor!";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınız bugün sona eriyor. Hizmet kesintisi yaşamamak için hemen yenileyin.

Lisans: {maskedKey}
Bitiş: {dateStr}

Şimdi yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p style=""color:#dc2626;font-size:18px""><strong>Lisansınız bugün sona eriyor!</strong></p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{maskedKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#dc2626;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Şimdi yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) ExpiredAfter1d(
        string customerName, string licenseKey, string portalUrl, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınızın süresi doldu";
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınızın süresi dün doldu. Lisansı yenileyerek hizmete kaldığınız yerden devam edebilirsiniz.

Lisans anahtarı: {maskedKey}

Yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınızın süresi dün doldu.</p>
<p>Lisans anahtarı: <code>{maskedKey}</code></p>
<p><a href=""{portalUrl}"">Lisansınızı yenileyin</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Password reset (transactional, no unsubscribe)
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) PasswordReset(string customerName, string resetUrl)
    {
        var subject = "OrderDeck — Şifre sıfırlama bağlantınız";
        var plain = $@"Merhaba {customerName},

OrderDeck hesabınız için şifre sıfırlama talebi aldık. Yeni şifrenizi belirlemek için aşağıdaki bağlantıya tıklayın:

{resetUrl}

Bu bağlantı 1 saat geçerlidir. Talep size ait değilse bu mesajı görmezden gelin.

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck hesabınız için şifre sıfırlama talebi aldık.</p>
<p><a href=""{resetUrl}"">Yeni şifrenizi belirleyin</a></p>
<p style=""color:#888"">Bu bağlantı 1 saat geçerlidir. Talep size ait değilse bu mesajı görmezden gelin.</p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, html, plain);
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Admin actions (license issued / revoked / extended)
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) LicenseIssued(
        string customerName, string licenseKey, string skuCode, DateTimeOffset expiresAt, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Yeni lisansınız hazır";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Yeni OrderDeck lisansınız oluşturuldu.

Lisans anahtarı: {licenseKey}
Plan: {skuCode}
Bitiş tarihi: {dateStr}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Yeni OrderDeck lisansınız oluşturuldu.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{licenseKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Plan</td><td style=""padding:4px 12px"">{skuCode}</td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş tarihi</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseRevoked(
        string customerName, string licenseKey, string reason, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız iptal edildi";
        var plain = $@"Merhaba {customerName},

Lisansınız iptal edildi.

Lisans anahtarı: {maskedKey}
Sebep: {reason}

Sorularınız için lütfen bizimle iletişime geçin.

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınız iptal edildi.</p>
<p>Lisans: <code>{maskedKey}</code></p>
<p>Sebep: {reason}</p>
<p style=""color:#888"">Sorularınız için lütfen bizimle iletişime geçin.</p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseExtended(
        string customerName, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays, string? unsubscribeUrl)
    {
        var maskedKey = LicenseKeyMasker.Mask(licenseKey);
        var subject = "OrderDeck — Lisansınız uzatıldı";
        var dateStr = newExpiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın süresi {additionalDays} gün uzatıldı.

Lisans anahtarı: {maskedKey}
Yeni bitiş tarihi: {dateStr}

— OrderDeck Ekibi";
        var html = Html($@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın süresi <strong>{additionalDays} gün</strong> uzatıldı.</p>
<p>Lisans: <code>{maskedKey}</code></p>
<p>Yeni bitiş tarihi: <strong>{dateStr}</strong></p>
<p>— OrderDeck Ekibi</p>
</body></html>");
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string AppendUnsubscribeFooter(string html, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return html;
        var footer = Html($@"<hr><p style=""color:#888;font-size:12px;margin-top:24px"">Bu e-postayı OrderDeck hesabınızla ilgili olduğu için aldınız. <a href=""{unsubscribeUrl}"">E-posta bildirimlerini durdur</a></p>");
        return html.Replace("</body>", footer + "</body>");
    }

    private static string AppendUnsubscribeFooterPlain(string plain, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return plain;
        return plain + $"\n\n---\nE-posta bildirimlerini durdurmak için: {unsubscribeUrl}";
    }
}
