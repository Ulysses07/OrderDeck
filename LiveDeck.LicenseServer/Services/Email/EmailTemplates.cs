namespace LiveDeck.LicenseServer.Services.Email;

public static class EmailTemplates
{
    public static (string Subject, string Html, string Plain) ConfirmEmail(string customerName, string confirmUrl)
    {
        var subject = "LiveDeck — Email adresinizi doğrulayın";
        var plain = $@"Merhaba {customerName},

LiveDeck hesabını doğrulamak için aşağıdaki bağlantıya tıkla:
{confirmUrl}

Bu link 24 saat geçerli.

Sen yapmadıysan bu mesajı görmezden gel.
— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck hesabını doğrulamak için <a href=""{confirmUrl}"">tıkla</a>.</p>
<p>Bu link 24 saat geçerli.</p>
<p style=""color:#888"">Sen yapmadıysan bu mesajı görmezden gel.</p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, html, plain);
    }
}
