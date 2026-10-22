using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>Kalıcı imha kaydının (<see cref="DataDestructionRecord"/>) sabitleri ve yazıcısı.</summary>
public static class DataDestruction
{
    public static class Actions
    {
        public const string Deleted = "deleted";
        public const string Nulled = "nulled";
        /// <summary>Değer daha az tanımlayıcı bir biçimle değiştirildi (maske, beyaz liste, kimliğe çevirme).</summary>
        public const string Masked = "masked";
        /// <summary>Periyodik imha işi başarıyla bitti (<see cref="RecordRun"/>): silecek satır bulmasa da. Satır sayısı 0; silinen
        /// satırlar işin kendi kayıtlarında, toplamda iki kez sayılmasın.</summary>
        public const string RunCompleted = "run-completed";
    }

    public static class Triggers
    {
        public const string Retention = "retention";
        public const string ShopperPurge = "shopper-purge";
        public const string CustomerPurge = "customer-purge";
        public const string SubjectRequest = "subject-request";
        public const string ServiceEnd = "service-end";
        public const string AdminRemove = "admin-remove";
        /// <summary>Obifin kimliği değişti, eski hesabın gölge verisi silindi (<c>ObifinConnectionService.ResetShadowData</c>).</summary>
        public const string CredentialChange = "credential-change";
        /// <summary>Tek seferlik <c>bank-data-cleanup</c> komutu.</summary>
        public const string Cleanup = "cleanup";
        /// <summary>Yönetici onaysız hesabı reddetti ya da onayı geri aldı; hesabın verisi silindi (KVKK Ö-10;
        /// <c>BankConnectionTerminator.RejectAccountAsync</c>).</summary>
        public const string AccountRejected = "account-rejected";
        /// <summary>Veri işleyenin (Obifin) kendi kopyasını sildiğine dair yazılı teyidi kaydedildi (KVKK Ö-9;
        /// <c>BankConnectionTerminator.ConfirmObifinCopyDeletedAsync</c>).</summary>
        public const string ProcessorConfirmation = "processor-confirmation";
    }

    public const string SystemActor = "system";
    /// <summary>Konteynerde elle koşulan komut (<c>bank-data-cleanup</c>).</summary>
    public const string CliActor = "cli";
    /// <summary>Yönetici eylemi, yöneticinin kimliği servise ulaşmadığında (kim olduğu aynı işlemin denetim satırında).</summary>
    public const string AdminActor = "admin";

    /// <summary>Yönetici eyleminin yapanı: yöneticinin takma kimliği (<c>sub</c>), kullanıcı adı DEĞİL (KVKK Ö-14) — tablo
    /// budanmaz, tek kişilik işletmede ad bir gerçek kişiyi gösterir. Kimlik yoksa <see cref="AdminActor"/>.</summary>
    public static string ActorOf(ClaimsPrincipal user)
        => user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? AdminActor;

    /// <summary>Dış kopyanın (<see cref="DataDestructionDigestJob.ToCsv"/>) ayracı <c>;</c> ve satır sonu tabloya girmez: <c>;</c> →
    /// <c>,</c>, satır sonu → boşluk. E-postadaki değer tablodakiyle birebir aynı kalır; geri yüklemede ikisi karşılaştırılır.
    /// Ölçüt yazılırken zaten <c>;</c> kullanılmaz (<c>, </c> ya da <c> / </c>); bu, unutulan bir yazımın güvencesi.</summary>
    public static string CsvSafe(string? value) => (value ?? "").Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Kaydı izleyiciye EKLER, kaydetmez: silmeyle aynı SaveChanges'te yazılsın. Sıfır satırlık işlem yazılmaz. Serbest
    /// metinler (<paramref name="target"/>, <paramref name="criterion"/>, <paramref name="actor"/>) <see cref="CsvSafe"/>'ten
    /// geçer.</summary>
    public static void Record(LicenseDbContext db, Guid? licenseId, string target, string action, int rowCount,
        string criterion, string trigger, string actor, Guid? requestId = null, Guid? subjectId = null)
    {
        if (rowCount <= 0) return;
        db.DataDestructionRecords.Add(new DataDestructionRecord
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, LicenseId = licenseId, SubjectId = subjectId,
            Target = CsvSafe(target), Action = action, RowCount = rowCount, Criterion = CsvSafe(criterion), Trigger = trigger,
            RequestId = requestId, Actor = CsvSafe(actor),
        });
    }

    /// <summary>Periyodik imha işinin başarılı koşusunun kaydı (iç imha prosedürü §4.2-3: her çalışma kayıt bırakır, haftalık
    /// "son başarılı çalışma" kontrolü bu satırlardan ya da veritabanı dışındaki kopyasından yapılır). <paramref name="job"/>
    /// Hangfire iş kimliği; <paramref name="criterion"/> işin süreleri. İzleyiciye ekler, kaydetmez.</summary>
    public static void RecordRun(LicenseDbContext db, string job, string criterion)
        => db.DataDestructionRecords.Add(new DataDestructionRecord
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, Target = CsvSafe(job), Action = Actions.RunCompleted,
            RowCount = 0, Criterion = CsvSafe(criterion), Trigger = Triggers.Retention, Actor = SystemActor,
        });
}
