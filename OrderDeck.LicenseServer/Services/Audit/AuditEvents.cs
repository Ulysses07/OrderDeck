namespace OrderDeck.LicenseServer.Services.Audit;

public static class AuditEvents
{
    public const string AdminLogin = "admin.login";
    public const string AdminLogout = "admin.logout";
    public const string CustomerConfirmEmail = "customer.confirm-email";
    public const string LicenseIssue = "license.issue";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseExtend = "license.extend";
    public const string LicenseSlotsChange = "license.slots-change";
    public const string ActivationForceDeactivate = "activation.force-deactivate";
    public const string RefreshTokenIssued = "RefreshTokenIssued";
    public const string RefreshTokenRotated = "RefreshTokenRotated";
    public const string RefreshTokenRevoked = "RefreshTokenRevoked";

    // KVKK / GDPR compliance (Phase 5d)
    public const string CustomerDataExported = "customer.data-exported";
    public const string CustomerPurged = "customer.purged";

    // Shopper (müşteri app) silme talebi — elle işlenen KVKK akışı.
    public const string ShopperPurged = "shopper.purged";

    // Multi-operator (PR-5 Faz 1+2) — owner Customer'ın staff hesap CRUD'u.
    public const string OperatorInvited = "operator.invited";
    public const string OperatorDeleted = "operator.deleted";

    // Netgsm kurulum kapatma anahtarı (§2.5). Kapatma bir yayıncının tüm
    // SMS'ini keser; kimin ne zaman kestiği iz bırakmadan olmamalı.
    public const string NetgsmAccountDisable = "netgsm.account.disable";
    public const string NetgsmAccountEnable = "netgsm.account.enable";

    // §6 — ayrılışta İYS onay listesi dışa aktarımı. Dönüş yolunun taşıyıcısı:
    // İYS bir markanın onay listesini vermez, /iys/search numara listesi ister.
    public const string NetgsmAccountExport = "netgsm.account.export";

    // Obifin banka hareketi çekimi (admin/obifin). Ayrıntıya şifre, API anahtarı ve banka web servis kimliği GİRMEZ.
    public const string ObifinConnectionSave = "obifin.connection.save";
    public const string ObifinConnectionVerify = "obifin.connection.verify";
    public const string ObifinBankAdd = "obifin.bank.add";
    public const string ObifinPollNow = "obifin.poll.now";
    // KVKK Ö-10: yönetici banka hesabını lisansa bağladı (hesap numarası + banka kodu; IBAN GİRMEZ).
    public const string ObifinAccountApprove = "obifin.account.approve";
    // KVKK K-6: yönetici çekimi durdurdu / bağlantıyı yeniden etkinleştirdi (ayrıntı yok).
    public const string ObifinConnectionStop = "obifin.connection.stop";
    public const string ObifinConnectionResume = "obifin.connection.resume";
    /// <summary>Bağlantı kaldırıldı ve banka verisi silindi (KVKK K-6, Ö-9): imha olayı (<see cref="DestructionEvents"/>).
    /// Ayrıntı: sayılar, Obifin tarafının sonucu ve teyit kaydının kimliği.</summary>
    public const string ObifinConnectionRemove = "obifin.connection.remove";
    /// <summary>Hesap reddedildi ya da onayı geri alındı, verisi silindi (KVKK Ö-10): imha olayı. Ayrıntı: hesap numarası ve
    /// sayılar.</summary>
    public const string ObifinAccountReject = "obifin.account.reject";
    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit kaydedildi (KVKK Ö-9): imha olayı. Ayrıntı: teyit kaydının
    /// kimliği.</summary>
    public const string ObifinCopyDeletionConfirm = "obifin.copy.confirm";
    // Gölge banka eşleştirmesi (admin/banka-eslestirme). Ayrıntıya yalnız müşteri kayıt numarası (WpfCustomerId) girer:
    // kullanıcı adı, açıklama, tutar, IBAN GİRMEZ (KVKK Ö-14; Görev 16'dan önceki satırları bank-data-cleanup çevirir).
    public const string BankMatchManual = "bank.match.manual";
    public const string BankMatchUnmatch = "bank.match.unmatch";
    /// <summary>Banka yönetim sayfasının görüntülenmesi (KVKK Ö-4; Kurul 2018/10 3-c): açıklama, kanıt, maske gösteren sayfanın
    /// her açılışı. Ayrıntı yalnız sayfa adı ve sayfa/talep numarası. Banka Eşleştirme ve Obifin sayfalarında hedef banka
    /// modülünün öbür satırları gibi <see cref="AuditTargets.ObifinConnection"/> + lisans KİMLİĞİ (Obifin'de düzenleme formu açık
    /// lisans, yoksa boş); <see cref="AuditTargets.License"/> lisans ANAHTARI taşır ve lisans sayfasının son 20 kaydında
    /// listelenir, görüntülemeler orayı doldurmasın.</summary>
    public const string BankPageView = "bank.page.view";

    /// <summary>İmha olayları (Silme Yönetmeliği md. 7/3: en az üç yıl). <see cref="AuditRetentionJobs"/> bunları
    /// <see cref="AuditRetentionOptions.RetentionMonths"/>'ta değil <see cref="AuditRetentionOptions.DestructionRetentionMonths"/>'ta
    /// budar. Muafiyet kişisel veriyi daha uzun tutmamalı: YALNIZ ayrıntısı kişisel veri İÇERMEYEN (sayılar, kimlikler) olaylar
    /// girer, satırın kendi sütunlarındaki kişisel veri — silmeyi yapan yöneticinin kullanıcı adı ve IP'si — ise
    /// <see cref="AuditRetentionOptions.RetentionMonths"/>'ta maskelenir (ad yöneticinin kimliğine çevrilir, IP boşaltılır;
    /// <see cref="AuditRetentionJobs.MaskedTarget"/>). Satır 36 aya kadar yalnız takma kimlikler ve sayılarla kalır (KVKK Ö-14).
    /// <see cref="CustomerPurged"/> BİLEREK yok — ayrıntısında silinen yayıncının e-postası var, 24 ayda gider; yayıncı
    /// silmesinin 3 yıllık kaydı kişisel veri içermeyen <c>DataDestructionRecords</c>. Yeni bir silme olayı eklenirse ayrıntısı
    /// kişisel veri içermiyorsa buraya da eklenir.</summary>
    public static readonly IReadOnlySet<string> DestructionEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        ShopperPurged,
        ObifinConnectionRemove,
        ObifinAccountReject,
        ObifinCopyDeletionConfirm,
    };
}

public static class AuditTargets
{
    public const string Admin = "admin";
    public const string Customer = "customer";
    public const string License = "license";
    public const string Activation = "activation";
    public const string RefreshToken = "RefreshToken";
    public const string Operator = "operator";
    public const string Shopper = "shopper";
    public const string NetgsmAccount = "netgsm-account";
    public const string ObifinConnection = "obifin-connection";
    public const string BankTransaction = "bank-transaction";
}
