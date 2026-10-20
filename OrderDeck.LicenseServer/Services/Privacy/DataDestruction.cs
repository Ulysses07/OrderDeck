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
    }

    public const string SystemActor = "system";
    /// <summary>Konteynerde elle koşulan komut (<c>bank-data-cleanup</c>).</summary>
    public const string CliActor = "cli";
    /// <summary>Yönetici eylemi, kullanıcı adı servise ulaşmadığında (adı aynı işlemin denetim satırında).</summary>
    public const string AdminActor = "admin";

    /// <summary>Kaydı izleyiciye EKLER, kaydetmez: silmeyle aynı SaveChanges'te yazılsın. Sıfır satırlık işlem yazılmaz.</summary>
    public static void Record(LicenseDbContext db, Guid? licenseId, string target, string action, int rowCount,
        string criterion, string trigger, string actor, Guid? requestId = null, Guid? subjectId = null)
    {
        if (rowCount <= 0) return;
        db.DataDestructionRecords.Add(new DataDestructionRecord
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, LicenseId = licenseId, SubjectId = subjectId, Target = target,
            Action = action, RowCount = rowCount, Criterion = criterion, Trigger = trigger, RequestId = requestId, Actor = actor,
        });
    }
}
