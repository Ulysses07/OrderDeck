namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının veri sorumlusu bilgileri (KVKK m. 10/1-a; ön inceleme K-3): yayıncının veri sorumlusu olduğu yerlerde (kayıt
/// formu, Shopper) ilgili kişiye gösterilir. Yayıncı hesabı (<see cref="Customer"/>) başına bir satır. Şahıs yayıncıda kişisel
/// veridir (ad, adres): yayıncının KVKK silmesinde silinir.
/// </summary>
public sealed class DataControllerProfile
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    /// <summary>Ad soyad ya da ticaret unvanı.</summary>
    public string LegalName { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    /// <summary>KVKK başvurularının gönderileceği e-posta.</summary>
    public string KvkkEmail { get; set; } = "";
    /// <summary>Kayıtlı elektronik posta adresi, varsa.</summary>
    public string? KepAddress { get; set; }
    /// <summary>Gösterilen içeriğin sürümü: her içerik değişikliğinde artar, içeriği <see cref="DataControllerProfileVersion"/>'da
    /// (KVKK m. 10/1-a ispatı; <see cref="IntakeFormSubmission.DataControllerVersion"/> buna işaret eder). Eşzamanlılık jetonu:
    /// aynı sürümü okuyan iki yazımdan ikincisi çakışır, yeniden okuyup dener (<c>DataControllerProfiles.UpsertAsync</c>).</summary>
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary><c>panel</c> ya da <c>admin:&lt;yönetici kimliği&gt;</c> (<c>DataDestruction.ActorOf</c>; kullanıcı adı değil).</summary>
    public string UpdatedBy { get; set; } = "";
}
