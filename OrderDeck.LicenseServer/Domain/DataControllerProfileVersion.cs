namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Veri sorumlusu bilgisinin bir sürümü (KVKK m. 10/1-a; ispat yükü veri sorumlusunda — ön inceleme K-3, plan karar 24): kayıt
/// formunda gösterilen bloğun içeriği sonradan ispatlanabilsin (<see cref="IntakeFormSubmission.DataControllerVersion"/>). Yalnız
/// eklenir. Şahıs yayıncıda kişisel veridir: yayıncının KVKK silmesinde profille birlikte silinir.
/// </summary>
public sealed class DataControllerProfileVersion
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public int Version { get; set; }
    public string LegalName { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    public string KvkkEmail { get; set; } = "";
    public string? KepAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary><c>panel</c> ya da <c>admin:&lt;yönetici kimliği&gt;</c> (kullanıcı adı değil).</summary>
    public string CreatedBy { get; set; } = "";
}
