namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WPF Customer kayıtlarının sunucudaki TAM kopyası: çok bilgisayarlı
/// senkronun temel taşı — aynı lisans birden fazla PC'de çalıştığında
/// müşteri verisinin tek doğru kaynağı burasıdır (hafif/özet bir kopya
/// değil). Order/Payment satırlarındaki CustomerId string'i bu tablodaki
/// Id'yi (GUID hex) işaret eder. WPF tarafı periyodik sync ile günceller
/// (POST /api/v1/licenses/{id}/wpf-customers/sync).
/// </summary>
public sealed class WpfCustomerProjection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string Platform { get; set; } = "";

    private string _username = "";

    public string Username
    {
        get => _username;
        set
        {
            _username = value;
            IdentityKey = IdentityKeyOf(value);
        }
    }

    /// <summary>
    /// Kimlik anahtarı: kırpılmış + küçük harf kullanıcı adı (bkz.
    /// <see cref="IdentityKeyOf"/>). Kişi başına tek asıl kayıt (MergedIntoId
    /// null) bu anahtarla aranır. Ayrı kolon, çünkü InMemory test sağlayıcısı
    /// hesaplanmış kolonu değerlendirmiyor ve tekil indeks (Bölüm B) bir
    /// kolon istiyor. Yazma her zaman Username setter'ından.
    /// </summary>
    /// <remarks>
    /// <see cref="OrderDeck.LicenseServer.Services.Bank.BankTextNormalizer.UsernameKey"/>
    /// İLE KARIŞTIRILMASIN — adları benzer ama amaçları ve davranışları
    /// farklı: o, banka açıklaması içinde ALT DİZE arayan BULANIK bir anahtar
    /// (ayraçları tamamen siler, aksanları ASCII'ye indirir — "ayşe gül 34"
    /// → "aysegul34"); bu alan ise TAM KİMLİK anahtarı, ayraç/noktalama
    /// korunur. İkisi birbirinin yerine kullanılsa derleniyor ama davranışı
    /// SESSİZCE değiştiriyor: bu alan yerine BankTextNormalizer'ınki
    /// kullanılırsa farklı kişiler aynı bulanık anahtara düşüp YANLIŞ
    /// BİRLEŞTİRİLİR (false merge) — ikisi kasıtlı olarak ayrı tutulmalı.
    /// </remarks>
    public string IdentityKey { get; private set; } = "";

    /// <summary>
    /// Kimlik anahtarını türetir: kırpılmış + küçük harf (invariant kültür —
    /// tr-TR değil, çünkü sunucu ortamları arasında kültüre bağlı sıralama/
    /// küçültme davranışı deterministik olmayabilir).
    ///
    /// <para><b>Türkçe 'İ' düzeltmesi:</b> .NET 10'da invariant
    /// <c>ToLowerInvariant()</c>, U+0130 (büyük noktalı İ) harfini
    /// DEĞİŞTİRMEZ — oysa SQL Server'ın <c>LOWER('İ')</c>'si HER collation'da
    /// 'i' döner. Bu uyuşmazlık düzeltilmeden var olan satırlar (göçteki SQL
    /// backfill ile anahtarlanmış) ile yeni yazılan satırlar (bu metotla
    /// anahtarlanmış) aynı kullanıcı için FARKLI anahtara düşer — kalıcı,
    /// sessiz kopyalar. <c>Replace('İ','i')</c> iki tarafı en azından İ
    /// harfinde eşitler (Ç/Ğ/I/Ö/Ş/Ü zaten invariant altında SQL ile aynı
    /// sonucu veriyor).</para>
    ///
    /// <para>Çözülmeyen kalanlar: kenarlardaki U+0020 DIŞI boşluklar (NBSP,
    /// TAB, …) — SQL'in <c>RTRIM/LTRIM</c>'i yalnız U+0020'yi kırpar — ve
    /// Türkçe/Latin-1 dışındaki bazı harfler (Latin Ext-B/D, Yunanca/Kiril
    /// ekleri, Gürcüce, Cherokee, letterlike semboller, ek düzlem harfleri)
    /// hâlâ iki taraf arasında ayrışabilir. Burada BİLEREK çözülmüyor —
    /// <c>IdentityKeyRepairJob</c> (Hangfire: <c>identity-key-repair</c>)
    /// bu satırları .NET'te yeniden hesaplayıp düzeltir.</para>
    /// </summary>
    public static string IdentityKeyOf(string username)
        => username.Trim().ToLowerInvariant().Replace('İ', 'i');

    // ── Senkronlanan alanlar. Her BİRİMİN kendi damgası var (istemcide
    //    düzenleme anı); kurallar CustomerFieldMerge'de. Birim = tek alan, ya
    //    da ayrılırsa anlamsızlaşan blok (adres, kara liste).

    public string? FullName { get; set; }
    public DateTimeOffset? FullNameChangedAt { get; set; }
    /// <summary>Platform takma adı (sohbet). FullName'den ayrı taşınır.</summary>
    public string? DisplayName { get; set; }
    public DateTimeOffset? DisplayNameChangedAt { get; set; }
    /// <summary>WPF'in aynı kişinin farklı platform satırlarını bağladığı anahtar.</summary>
    public string? GroupId { get; set; }
    public DateTimeOffset? GroupIdChangedAt { get; set; }

    /// <summary>Adres bloğu: Address + City + District tek birim, tek damga
    /// (<see cref="AddressChangedAt"/>) — başka adresin il/ilçesiyle karışmış
    /// blok etikete yanlış adres basar.</summary>
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public DateTimeOffset? AddressChangedAt { get; set; }
    /// <summary>Kendi damgası var: dekont girilince OTOMATİK açılıyor
    /// (DekontEkleViewModel). Adresle aynı damgayı paylaşsaydı, güncel adresi
    /// almamış bir bilgisayar bu otomatik yazımla adresi boşla ezerdi.</summary>
    public bool RecipientPaysActive { get; set; }
    public DateTimeOffset? RecipientPaysChangedAt { get; set; }

    public string? Phone { get; set; }
    public DateTimeOffset? PhoneChangedAt { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset? EmailChangedAt { get; set; }
    /// <summary>TCKN — ŞİFRELİ (TcknProtector; Shopper.TcProtected ve
    /// IntakeFormSubmission.TcknProtected ile aynı amaç). Asla kırpılmaz.</summary>
    public string? TcknProtected { get; set; }
    public DateTimeOffset? TcknChangedAt { get; set; }
    /// <summary>İzin bayrakları WPF senkronu ve gösterim içindir; gönderimin
    /// dayanağı DEĞİLDİR (SMS kampanyası <c>Shopper.SmsConsent</c>'e bakar,
    /// İYS aynası yetkilidir). Her biri ayrı damgalı: birinin değişmesi
    /// ötekinin eski değerini geri getirmesin.</summary>
    public bool WhatsAppConsent { get; set; }
    public DateTimeOffset? WhatsAppConsentChangedAt { get; set; }
    public bool SmsConsent { get; set; }
    public DateTimeOffset? SmsConsentChangedAt { get; set; }

    /// <summary>Kara liste bloğu: üç alan tek birim (<see cref="BlacklistChangedAt"/>).</summary>
    public bool IsBlacklisted { get; set; }
    public string? BlacklistReason { get; set; }
    public DateTimeOffset? BlacklistedAt { get; set; }
    public DateTimeOffset? BlacklistChangedAt { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset? NotesChangedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Doluysa bu satır bir KOPYADIR ve asıl kayıt bu Id'dir. Kopya silinmez:
    /// eski Id ile geç gelen veri (sipariş, kargo, push) buradan asıl kayda
    /// yönlendirilir. Kişisel alanları <see cref="ScrubPersonal"/> ile
    /// boşaltılmıştır; iş notu ve kara liste kalır (bkz. o metodun dokümanı).
    /// </summary>
    public Guid? MergedIntoId { get; set; }

    /// <summary>SQL Server rowversion — her yazımda sunucu artırır. Değişiklik
    /// akışının imleci; istemci saati hiç kullanılmaz. EF InMemory
    /// sağlayıcısında DEĞER ÜRETMEZ, her zaman 0 kalır — rowversion da,
    /// olası bir alt-sınır sorgusu için düşünülen MIN_ACTIVE_ROWVERSION da
    /// SQL Server'a özgüdür; bu alanı InMemory testleri DOĞRULAYAMAZ (bkz.
    /// <c>CustomerProjectionFullSyncMigrationTests</c> — Testcontainers ile
    /// gerçek SQL Server kullanıyor).</summary>
    public long ChangeSeq { get; set; }

    /// <summary>
    /// KVKK silme talebi kapsamında bu satırın kişisel alanları temizlendiyse
    /// dolu. Senkron kapısı olarak kullanılır: WPF hâlâ kendi yerel kopyasını
    /// taşıdığı için, işaret olmasa bir sonraki push kişisel veriyi buraya
    /// geri yazar (sync ucu CustomerFieldMerge kurallarıyla yazar: boşaltılmış
    /// alanı eski sürümün damgasız gönderimi doldurur, yeni sürümün daha yeni
    /// damgalı değeri ezer) ve silme sessizce geri alınırdı. Kapı hem sync
    /// ucunda hem CustomerFieldMerge'de.
    /// </summary>
    public DateTimeOffset? PurgedAt { get; set; }

    /// <summary>
    /// Shopper uygulamasının açtığı GEÇİCİ kayıt: kullanıcı adının hiç adayı
    /// yokken açılır, ad/telefon/adres kişinin KENDİ beyanıdır ve bağlantı
    /// kanıtsız bağlanmıştır. Asıl kayıtken yayıncıdan gelen her yazımda bağlı
    /// bağlantılar satırın telefonuna karşı yeniden kanıt ister; bayrak yalnız
    /// yayıncıdan damgalı telefonla kalkar (eski ingest'in yankısı benimseme
    /// değildir). Yayıncı bu kişiyi kendi Id'siyle gönderdiğinde (devralma)
    /// yayıncının satırı asıl kayıt olur, bu satır onun kopyasına döner ve
    /// bayrak KÖKEN olarak kalır: geçici kökenli kopyanın gönderimi (eski
    /// ingest beyanı geri yankılar) asıl kayda yazılmaz. Birleştirme işinde
    /// geçici kopya alan kaynağı ve silme yayıcısı olmaz. Bkz.
    /// LicensesWpfCustomersSyncController, CustomerIdentityMergeJob,
    /// WpfCustomerLinkMatcher.
    /// </summary>
    public bool CreatedByShopper { get; set; }

    /// <summary>
    /// Kişisel alanların TEK boşaltma listesi — WPF tarafındaki
    /// <c>OrderDeck.Core.Storage.Repositories.CustomerRepository</c>'nin
    /// <c>ScrubAssignments</c>'ı ile AYNI politikayı uygular (KVKK silme
    /// talebinde neyin kişisel sayılıp neyin kalacağı kararı orada belgeli,
    /// bkz. <c>ScrubPersonalData</c> doc'u).
    ///
    /// <para><b>Kalanlar ve gerekçesi</b> (BİLEREK dokunulmaz): kimlik
    /// (<see cref="Platform"/>/<see cref="Username"/>/<see cref="IdentityKey"/>);
    /// <see cref="Notes"/> — yayıncının kendi yazdığı işletme notu, silinmesi
    /// operatörün kendi kaydını yok etmek olurdu; <see cref="IsBlacklisted"/>/
    /// <see cref="BlacklistReason"/>/<see cref="BlacklistedAt"/> — sahtekârlık
    /// koruması, temizlenseydi silme talebi kara listeden çıkmanın yolu
    /// olurdu. <see cref="PurgedAt"/>/<see cref="UpdatedAt"/> de burada
    /// DEĞİŞMEZ — damgalama <see cref="MarkPurged"/>'in işi; bu ayrım
    /// birleştirme sırasında kişisel alanı silip tombstone'a henüz
    /// dokunmayan bir çağırana da izin verir.
    /// </para>
    /// </summary>
    public void ScrubPersonal()
    {
        FullName = null; DisplayName = null;
        Phone = null; Email = null; TcknProtected = null;
        Address = null; City = null; District = null;
        WhatsAppConsent = false; SmsConsent = false;
    }

    /// <summary>
    /// KVKK silme talebi: kişisel alanları boşaltır VE tombstone damgasını
    /// vurur. <c>PurgedAt ??=</c>: tarih adli kayıt, tekrarlanan silme talebi
    /// İLK silme tarihini korur — ikinci çağrı <see cref="UpdatedAt"/>'ı
    /// ilerletir ama <see cref="PurgedAt"/>'ı DEĞİŞTİRMEZ.
    /// </summary>
    public void MarkPurged(DateTimeOffset now)
    {
        ScrubPersonal();
        PurgedAt ??= now;
        UpdatedAt = now;
    }
}
