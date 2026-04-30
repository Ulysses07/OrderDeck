namespace OrderDeck.LicenseServer.Services.Email;

public static class EmailTemplates
{
    public static (string Subject, string Html, string Plain) ConfirmEmail(string customerName, string confirmUrl)
    {
        var subject = "OrderDeck — Email adresinizi doğrulayın";
        var plain = $@"Merhaba {customerName},

OrderDeck hesabını doğrulamak için aşağıdaki bağlantıya tıkla:
{confirmUrl}

Bu link 24 saat geçerli.

Sen yapmadıysan bu mesajı görmezden gel.
— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck hesabını doğrulamak için <a href=""{confirmUrl}"">tıkla</a>.</p>
<p>Bu link 24 saat geçerli.</p>
<p style=""color:#888"">Sen yapmadıysan bu mesajı görmezden gel.</p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, html, plain);
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Renewal reminders
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) Renewal14d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız 14 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınız {dateStr} tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.

Lisans anahtarı: {licenseKey}
Bitiş: {dateStr}

Lisansınızı portaldan yönetin: {portalUrl}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınız <strong>{dateStr}</strong> tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{licenseKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p><a href=""{portalUrl}"">Lisansınızı portaldan yönetin</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal7d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız 7 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınızın bitmesine 7 gün kaldı. Hizmet kesintisi yaşamamak için en kısa sürede yenileyin.

Lisans anahtarı: {licenseKey}
Bitiş: {dateStr}

Yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınızın bitmesine <strong>7 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"">Hemen yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal3d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız 3 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın bitmesine 3 gün kaldı! Hemen yenileyin.

Lisans: {licenseKey}
Bitiş: {dateStr}

{portalUrl}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın bitmesine <strong style=""color:#d97706"">3 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#d97706;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Hemen yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal0d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız bugün sona eriyor!";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınız bugün sona eriyor. Hizmet kesintisi yaşamamak için hemen yenileyin.

Lisans: {licenseKey}
Bitiş: {dateStr}

Şimdi yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p style=""color:#dc2626;font-size:18px""><strong>Lisansınız bugün sona eriyor!</strong></p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#dc2626;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Şimdi yenile</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) ExpiredAfter1d(
        string customerName, string licenseKey, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınızın süresi doldu";
        var plain = $@"Merhaba {customerName},

OrderDeck lisansınızın süresi dün doldu. Lisansı yenileyerek hizmete kaldığınız yerden devam edebilirsiniz.

Lisans anahtarı: {licenseKey}

Yenile: {portalUrl}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck lisansınızın süresi dün doldu.</p>
<p>Lisans anahtarı: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"">Lisansınızı yenileyin</a></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
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
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>OrderDeck hesabınız için şifre sıfırlama talebi aldık.</p>
<p><a href=""{resetUrl}"">Yeni şifrenizi belirleyin</a></p>
<p style=""color:#888"">Bu bağlantı 1 saat geçerlidir. Talep size ait değilse bu mesajı görmezden gelin.</p>
<p>— OrderDeck Ekibi</p>
</body></html>";
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
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Yeni OrderDeck lisansınız oluşturuldu.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{licenseKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Plan</td><td style=""padding:4px 12px"">{skuCode}</td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş tarihi</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseRevoked(
        string customerName, string licenseKey, string reason, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız iptal edildi";
        var plain = $@"Merhaba {customerName},

Lisansınız iptal edildi.

Lisans anahtarı: {licenseKey}
Sebep: {reason}

Sorularınız için lütfen bizimle iletişime geçin.

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınız iptal edildi.</p>
<p>Lisans: <code>{licenseKey}</code></p>
<p>Sebep: {reason}</p>
<p style=""color:#888"">Sorularınız için lütfen bizimle iletişime geçin.</p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseExtended(
        string customerName, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays, string? unsubscribeUrl)
    {
        var subject = "OrderDeck — Lisansınız uzatıldı";
        var dateStr = newExpiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın süresi {additionalDays} gün uzatıldı.

Lisans anahtarı: {licenseKey}
Yeni bitiş tarihi: {dateStr}

— OrderDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın süresi <strong>{additionalDays} gün</strong> uzatıldı.</p>
<p>Lisans: <code>{licenseKey}</code></p>
<p>Yeni bitiş tarihi: <strong>{dateStr}</strong></p>
<p>— OrderDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string AppendUnsubscribeFooter(string html, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return html;
        var footer = $@"<hr><p style=""color:#888;font-size:12px;margin-top:24px"">Bu e-postayı OrderDeck hesabınızla ilgili olduğu için aldınız. <a href=""{unsubscribeUrl}"">E-posta bildirimlerini durdur</a></p>";
        return html.Replace("</body>", footer + "</body>");
    }

    private static string AppendUnsubscribeFooterPlain(string plain, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return plain;
        return plain + $"\n\n---\nE-posta bildirimlerini durdurmak için: {unsubscribeUrl}";
    }
}
