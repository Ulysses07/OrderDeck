namespace OrderDeck.LicenseServer.Domain;

public sealed class Customer
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// R11-S01: parola nesli. Kimlik bilgisini geçersiz kılan her olayda
    /// (parola değişimi, sıfırlama, KVKK purge) artar. Refresh token'lar
    /// üretildikleri andaki nesli taşır, yenilemede güncel nesille
    /// karşılaştırılır. İptal süpürmesi (<c>MarkAllRevokedAsync</c>) o anki
    /// AKTİF LİSTEYLE çalıştığı için kendisiyle yarışan bir login insert'ini
    /// kaçırabilir; nesil damgası o kaçağı sıralamadan bağımsız yakalar.
    ///
    /// <para>R12-S01: aynı zamanda bu satırın EŞZAMANLILIK JETONU
    /// (<c>LicenseDbContext.OnModelCreating</c>). Düz okuma-artırma-yazma
    /// altında iki parola değişimi aynı nesli okuyup ikisi de aynı sonraki
    /// değeri yazabiliyordu: iki "başarılı" geçersizleştirme ama tek nesil —
    /// yani ikinci değişim, birinci parolayla açılmış oturumu iptal ettiğini
    /// sanarken iptal etmiyordu. Jeton, kaybeden yazıyı sessizce kabul etmek
    /// yerine reddettirir; artırmayı atomikleştirip bayat parola gövdesini
    /// yine de yazmak farklı ve daha kötü bir ürün sonucu olurdu.</para>
    /// </summary>
    public int AuthVersion { get; set; }

    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }
    public bool Unsubscribed { get; set; }   // Phase 4e

    public ICollection<License> Licenses { get; } = new List<License>();
}
