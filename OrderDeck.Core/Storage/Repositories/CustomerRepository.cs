using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dapper;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class CustomerRepository
{
    private readonly IDbConnectionFactory _factory;
    public CustomerRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// KVKK boşaltma atamaları. Tek metinde tutuluyor çünkü iki yerden
    /// kullanılıyor (<see cref="ScrubPersonalData"/> ve
    /// <see cref="ScrubIfTombstonedSql"/>); ikisinin ayrışması, bir yoldan
    /// açılan satırın öbüründen temizlenmemesi demek olurdu.
    ///
    /// Neyin KALDIĞI ve gerekçesi <see cref="ScrubPersonalData"/>'da yazılı.
    /// </summary>
    private const string ScrubAssignments = @"
        DisplayName     = '[Silindi]',
        FullName        = NULL,
        Address         = NULL,
        City            = NULL,
        District        = NULL,
        Phone           = NULL,
        Email           = NULL,
        Tckn            = NULL,
        AvatarUrl       = NULL,
        WhatsAppConsent = 0,
        SmsConsent      = 0";

    /// <summary>Boş ya da yalnız boşluk → null; değilse kırpılmış değer. Formdan gelen
    /// boş alan bir birim DEĞİLDİR, yazılmaz (bağlayıcı kural 3).</summary>
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// R11-D01: "bu satırın kimliği silinmişse temizle." Satır AÇAN her yol,
    /// açtığı satır için bunu AYNI işlemde koşar.
    ///
    /// <para>Karar öncesinde "if purged" okuyup dallanmıyoruz: okuma ile yazma
    /// arasındaki pencerede inen bir tombstone kaçardı. Karar burada, uygulanan
    /// yazının <c>WHERE</c>'inde. <c>PurgedAt</c> tombstone'un tarihinden
    /// alınır — satır yeni açıldığı için silme anı "şimdi" değil, sunucunun
    /// bildirdiği andır.</para>
    ///
    /// <para><b>U16 — kimlik anahtarıyla da eşler.</b> <c>t.Username</c>
    /// kolonunun NOCASE harmanı yalnız ASCII katlar ("ŞEYMA" ≠ "şeyma"); kimlik
    /// anahtarı ASCII dışı harf farkını da yakalar. Önceki sürümün açtığı satırda
    /// <c>Customer.IdentityKey</c> NULL olabilir: o zaman yalnız ad eşleşmesi
    /// (bugünkü davranış). <c>MIN</c>: iki yazım da eşleşirse ilk silme tarihi
    /// (adli kayıt).</para>
    ///
    /// <para><c>internal</c>: senkron deposu sunucudan eklediği satırda da koşar.</para>
    /// </summary>
    internal const string ScrubIfTombstonedSql = @"
        UPDATE Customer
        SET " + ScrubAssignments + @",
            PurgedAt = COALESCE(PurgedAt, (
                SELECT MIN(t.PurgedAt) FROM CustomerPurgeTombstone t
                WHERE t.Platform = Customer.Platform
                  AND (t.Username = Customer.Username OR t.IdentityKey = Customer.IdentityKey)))
        WHERE Id = @id
          AND EXISTS (
                SELECT 1 FROM CustomerPurgeTombstone t
                WHERE t.Platform = Customer.Platform
                  AND (t.Username = Customer.Username OR t.IdentityKey = Customer.IdentityKey))";

    /// <summary>
    /// Kimlik başına tek silme kararı. <c>MIN</c>: tekrarlanan bildirim İLK
    /// silme tarihini korur (tarih adli kayıt). Tek metinde, çünkü
    /// <see cref="RecordPurge"/> kanonik kimlik ve (R12-D01) YouTube alias'ı
    /// için aynı ifadeyi koşar. Kimlik anahtarı (U16) da yazılır; satır zaten
    /// varsa dolu anahtar korunur.
    /// </summary>
    private const string TombstoneUpsertSql = @"
        INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt, IdentityKey)
        VALUES (@platform, @username, @purgedAtUnix, @key)
        ON CONFLICT(Platform, Username) DO UPDATE SET
            PurgedAt    = MIN(CustomerPurgeTombstone.PurgedAt, excluded.PurgedAt),
            IdentityKey = COALESCE(CustomerPurgeTombstone.IdentityKey, excluded.IdentityKey)";

    public void Insert(Customer c)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute(
            @"INSERT INTO Customer
              (Id, Platform, Username, IdentityKey, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               IsBlacklisted, BlacklistReason, Notes,
               TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone,
               RecipientPaysActive, GroupId, Email, Tckn, WhatsAppConsent, SmsConsent, FullName,
               City, District)
              VALUES
              (@Id, @Platform, @Username, @IdentityKey, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt, @Address, @Phone,
               @RecipientPaysActive, @GroupId, @Email, @Tckn, @WhatsAppConsent, @SmsConsent, @FullName,
               @City, @District)",
            new
            {
                c.Id, c.Platform, c.Username,
                // U6: kimlik anahtarını C# yazar (tetikleyici değil — bkz. göç 045). Birim
                // damgalarını INSERT tetikleyicisi basar: dolu birim = şimdi.
                IdentityKey = CustomerIdentity.KeyOrNull(c.Username),
                c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes,
                c.TotalLabelsPrinted, c.TotalAmount, c.BlacklistedAt, c.Address, c.Phone,
                RecipientPaysActive = c.RecipientPaysActive ? 1 : 0,
                c.GroupId, c.Email, c.Tckn,
                WhatsAppConsent = c.WhatsAppConsent ? 1 : 0,
                SmsConsent = c.SmsConsent ? 1 : 0,
                c.FullName, c.City, c.District
            }, tx);

        // R11-D01: chat akışı/ingest bu yoldan satır açar. Kimlik KVKK ile
        // silinmişse satır boş DOĞAR — silinen kişi yayına tek yorum yazdığı
        // anda takma adı ve avatarıyla geri gelmesin diye. Satırın kendisi
        // açılıyor çünkü etiket/sipariş ona bağlanacak; bariyer de o satır
        // (PurgedAt dolu → tüm güncelleme yolları kapalı).
        conn.Execute(ScrubIfTombstonedSql, new { id = c.Id }, tx);
        tx.Commit();
    }

    /// <summary>Kargo PR F: vendor "Alıcı Ödemeli" seçince true,
    /// sevkıyat tamamlanınca (gelecek future PR) false.</summary>
    public void SetRecipientPaysActive(string customerId, bool active)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Customer SET RecipientPaysActive=@active WHERE Id=@customerId",
            new { customerId, active = active ? 1 : 0 });
    }

    /// <summary>
    /// Önce birebir (Platform, Username) — tekil indeks, sıcak yol. Yoksa sunucunun
    /// kimlik anahtarıyla (U7): yeniden anahtarlama harf-farklı yerel kopyayı sildikten
    /// sonra sohbet aynı kişiyi birebir adla bulamayıp her gönderim aralığında yeni
    /// satır açardı (sunucuda yeni kopya → yerel taşıma → döngü) ve kara liste
    /// denetimi kişiyi kaçırırdı. İkinci sorgu da indeksli (IX_Customer_Identity).
    /// </summary>
    public Customer? FindByPlatformAndUsername(string platform, string username)
    {
        using var conn = _factory.Open();
        var row = FindRow(conn, null, platform, username);
        return row is null ? null : Map(row);
    }

    /// <summary>Birebir (Platform, Username), yoksa kimlik anahtarı (U7) — tek arama kuralı:
    /// <see cref="FindByPlatformAndUsername"/> ve eski form yolu (<see cref="UpsertFromIntakeForm"/>,
    /// yazma işleminin içinde) aynısını kullanır.</summary>
    private static Row? FindRow(
        System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, string platform, string username)
        => conn.QueryFirstOrDefault<Row>(
               "SELECT * FROM Customer WHERE Platform=@platform AND Username=@username",
               new { platform, username }, tx)
           ?? FindByIdentity(conn, tx, platform, username);

    /// <summary>Sunucunun kimlik anahtarıyla (U6) arama — U7. Boş kullanıcı adının anahtarı
    /// NULL: hiçbir satırla eşleşmez. Birden çok aday varsa silinmemiş ve en son görülen
    /// kazanır.</summary>
    private static Row? FindByIdentity(
        System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, string platform, string username)
        => conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Customer
              WHERE Platform = @platform COLLATE NOCASE AND IdentityKey = @key
              ORDER BY (PurgedAt IS NOT NULL), LastSeenAt DESC, Id
              LIMIT 1",
            new { platform, key = CustomerIdentity.KeyOrNull(username) }, tx);

    /// <summary>Returns the top-N shoppers from a session via a single
    /// JOIN — replaces the previous N+1 pattern in CustomerService where
    /// each row's Platform/Username was re-queried via FindByPlatformAndUsername.
    /// On a 1000-customer session that was ~1000 round-trips; this is one.</summary>
    public IReadOnlyList<Customer> GetTopShoppersForSession(string sessionId, int limit)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT c.*
              FROM Customer c
              JOIN Label l ON l.CustomerId = c.Id
              WHERE l.SessionId = @sessionId
                AND l.PrintedAt IS NOT NULL
                AND l.CancelledAt IS NULL
                AND l.IsTentativeBackup = 0
              GROUP BY c.Id
              ORDER BY SUM(l.Price) DESC
              LIMIT @limit",
            new { sessionId, limit });
        return rows.Select(Map).ToList();
    }

    public Customer? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    /// <param name="write">
    /// Doluysa yazma çağıranın işlemine katılır (bkz. <see cref="DbWrite"/>).
    /// Etiket durumu ile müşteri toplamı birlikte değişmek zorunda: biri yazılıp
    /// öbürü yazılmazsa ciro ile etiket listesi sessizce ayrışır. Boş
    /// bırakılırsa metot kendi bağlantısını açar — mevcut çağıranlar için
    /// davranış değişmiyor.
    /// </param>
    public void IncrementLabelStats(
        string id, int labelDelta, decimal amountDelta, long lastSeenAt, DbWrite? write = null)
    {
        _factory.Execute(write,
            @"UPDATE Customer
              SET TotalLabelsPrinted = TotalLabelsPrinted + @labelDelta,
                  TotalAmount        = TotalAmount + @amountDelta,
                  LastSeenAt         = @lastSeenAt
              WHERE Id = @id",
            new { id, labelDelta, amountDelta, lastSeenAt });
    }

    /// <summary>Sets or clears the blacklist flag, with optional reason and timestamp.</summary>
    public void UpdateBlacklist(string id, bool isBlacklisted, string? reason, long? blacklistedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Customer
              SET IsBlacklisted   = @flag,
                  BlacklistReason = @reason,
                  BlacklistedAt   = @blacklistedAt
              WHERE Id = @id",
            new
            {
                id,
                flag = isBlacklisted ? 1 : 0,
                reason,
                blacklistedAt
            });
    }

    /// <summary>True if any customer in the group is blacklisted.</summary>
    public bool IsGroupBlacklisted(string groupId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM Customer WHERE GroupId = @groupId AND IsBlacklisted = 1",
            new { groupId }) > 0;
    }

    /// <summary>Sets/clears the blacklist flag for EVERY customer in the group.
    /// Used so blacklisting one identity blacklists the whole linked person.</summary>
    public void SetGroupBlacklist(string groupId, bool isBlacklisted, string? reason, long? blacklistedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Customer
              SET IsBlacklisted = @flag, BlacklistReason = @reason, BlacklistedAt = @blacklistedAt
              WHERE GroupId = @groupId",
            new { groupId, flag = isBlacklisted ? 1 : 0, reason, blacklistedAt });
    }

    /// <summary>Toplam müşteri sayısı (tüm platformlar).</summary>
    public int CountAll()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer");
    }

    /// <summary>Kayıtlı müşteri sayısı = form doldurup telefon bırakanlar
    /// (chat-only müşterilerin telefonu yoktur).</summary>
    public int CountRegistered()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Customer WHERE Phone IS NOT NULL AND TRIM(Phone) <> ''");
    }

    /// <summary>Assigns a customer to a group (YouTube channelId adoption).</summary>
    public void SetGroupId(string customerId, string groupId)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Customer SET GroupId = @groupId WHERE Id = @id",
            new { groupId, id = customerId });
    }

    /// <summary>
    /// Manuel birleştirme: seçilen müşterileri tek bir gruba bağlar. Aralarında
    /// zaten gruplu biri varsa o grup id'si korunur (birden fazla grup varsa ilki
    /// baz alınır, hepsi ona toplanır); yoksa yeni grup id üretilir. Grubun
    /// üyelerinden herhangi biri kara listedeyse tüm birleşmiş grup kara listeye
    /// yayılır. Dönen değer nihai grup id'si.
    /// </summary>
    public string MergeIntoGroup(IReadOnlyList<string> customerIds)
    {
        var ids = customerIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
                  ?? new List<string>();
        if (ids.Count < 2)
            throw new ArgumentException("Birleştirmek için en az iki müşteri gerekli", nameof(customerIds));

        using var conn = _factory.Open();

        // Mevcut grup id'lerini topla; varsa ilkini koru, yoksa yeni üret.
        var existingGroups = conn.Query<string>(
            "SELECT DISTINCT GroupId FROM Customer WHERE Id IN @ids AND GroupId IS NOT NULL AND TRIM(GroupId) <> ''",
            new { ids }).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        var groupId = existingGroups.FirstOrDefault() ?? Guid.NewGuid().ToString("N");

        // Güncellenecek tüm id'leri bellekte topla: seçilenler + mevcut grupların
        // tüm üyeleri (böylece iki farklı grubu birleştirince geride üye kalmaz).
        var targetIds = new HashSet<string>(ids, StringComparer.Ordinal);
        if (existingGroups.Count > 0)
        {
            var groupMemberIds = conn.Query<string>(
                "SELECT Id FROM Customer WHERE GroupId IN @groups",
                new { groups = existingGroups });
            foreach (var id in groupMemberIds) targetIds.Add(id);
        }

        conn.Execute("UPDATE Customer SET GroupId = @groupId WHERE Id IN @targetIds",
            new { groupId, targetIds = targetIds.ToList() });

        // Elle birleştirme yerel eylemdir: yayılımı tetikleyici "şimdi" damgalar.
        PropagateGroupBlacklist(conn, null, groupId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), formAt: null);

        return groupId;
    }

    /// <summary>
    /// Tek seferlik geriye-dönük düzeltme: verilen form kimliklerine karşılık gelen
    /// müşteri satırlarını bulur ve <b>yalnızca boş</b> FullName'lere gerçek Ad
    /// Soyad'ı yazar. Eşleşen satır grupluysa tüm grup üyelerine yayılır (aynı kişi).
    /// LastSeenAt / DisplayName / Phone gibi alanlara <b>dokunmaz</b> — sadece
    /// FullName. Dönen: güncellenen satır sayısı.
    ///
    /// <para>Damga = formun gönderim anı (<paramref name="submittedAtMs"/>, kural 3) ve
    /// yalnız yerel ad damgası daha eskiyse ya da yoksa: damgalı boş ad bilinçli
    /// silmedir, daha eski form onu doldurmaz. Arama ve yazımlar tek yazma işleminde
    /// (bkz. <see cref="UpsertPersonFromIntake"/>).</para>
    /// </summary>
    public int BackfillFullNameForIdentities(
        IReadOnlyList<(string Platform, string Username)> identities, string fullName, long submittedAtMs)
    {
        var value = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim();
        if (value is null) return 0;
        var at = submittedAtMs;

        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var soloIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, u) in identities)
        {
            var handle = (u ?? "").Trim().TrimStart('@').Trim();
            if (handle.Length == 0) continue;
            var row = FindExistingForIntake(conn, tx, p, handle);
            if (row is null) continue;
            if (!string.IsNullOrWhiteSpace(row.GroupId)) groupIds.Add(row.GroupId!);
            else soloIds.Add(row.Id);
        }

        // R10-D02: "FullName boş" filtresi temizlenmiş satırları da yakalıyordu
        // (scrub FullName'i NULL'lar) — backfill KVKK silmesini geri dolduruyordu.
        // Tombstone'lu satırlar bariyerle dışarıda.
        int updated = 0;
        if (groupIds.Count > 0)
            updated += conn.Execute(
                @"UPDATE Customer SET FullName = @value, FullNameChangedAt = @at
                  WHERE GroupId IN @groups AND (FullName IS NULL OR TRIM(FullName) = '')
                    AND PurgedAt IS NULL
                    AND (FullNameChangedAt IS NULL OR FullNameChangedAt < @at)",
                new { value, groups = groupIds.ToList(), at }, tx);
        if (soloIds.Count > 0)
            updated += conn.Execute(
                @"UPDATE Customer SET FullName = @value, FullNameChangedAt = @at
                  WHERE Id IN @ids AND (FullName IS NULL OR TRIM(FullName) = '')
                    AND PurgedAt IS NULL
                    AND (FullNameChangedAt IS NULL OR FullNameChangedAt < @at)",
                new { value, ids = soloIds.ToList(), at }, tx);

        write.Commit();
        return updated;
    }

    /// <summary>Bir grubun tüm üye satırlarını döner (platform bazında). Detay
    /// penceresinde kişinin bağlı tüm platform kimliklerini göstermek için.</summary>
    public IReadOnlyList<Customer> GetGroupMembers(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return System.Array.Empty<Customer>();
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            "SELECT * FROM Customer WHERE GroupId = @groupId ORDER BY Platform",
            new { groupId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Verilen satırların ait olduğu grupların EKSİK üyelerini tamamlar; hiçbir
    /// satırı atmaz, yalnızca ekler.
    ///
    /// <para><b>R5-02 (2026-09-12) — neden gerekli.</b> <see cref="Search"/> ve
    /// <see cref="GetRecent"/> SATIR düzeyinde kesiyor, ekrandaki kart ise bir
    /// KİŞİ: <c>GroupId</c>'ye göre toplanmış satırların toplamı. Limit bir grubu
    /// ortasından böldüğünde sorun "eski kartların görünmemesi" değil —
    /// GÖRÜNEN kartın kendi toplamı eksiliyor (3 üyeli 300'lük kişi, 1 üye/100
    /// olarak çiziliyor). Ödeme komutu kart toplamını tükettiği için bu doğrudan
    /// yanlış tutarlı ödeme isteğine dönüşebiliyordu.</para>
    ///
    /// <para>Tamamlama <b>süzgeçlerden bağımsız</b>: platform/kayıtlı süzgeci
    /// hangi KİŞİLERİN listeleneceğini seçer, kartın İÇERİĞİNİ değil. Süzgeç
    /// tamamlamaya da uygulansaydı, telefonu yalnız bir platform satırında olan
    /// kişinin kartı telefonsuz kalır (birincil üye kaybolur) ve toplam yine
    /// eksik çıkardı.</para>
    /// </summary>
    public IReadOnlyList<Customer> CompleteGroups(IReadOnlyList<Customer> rows)
    {
        var groupIds = rows
            .Where(c => !string.IsNullOrWhiteSpace(c.GroupId))
            .Select(c => c.GroupId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groupIds.Count == 0) return rows;

        using var conn = _factory.Open();
        var members = conn.Query<Row>(
            "SELECT * FROM Customer WHERE GroupId IN @groupIds",
            new { groupIds }).ToList();

        var seen = rows.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var completed = rows.ToList();
        foreach (var m in members)
            if (seen.Add(m.Id)) completed.Add(Map(m));

        return completed;
    }

    /// <summary>Bir grubun tüm üyelerini gruptan ayırır (GroupId = NULL). Yanlış
    /// birleştirmeyi geri almak için. Kara liste durumuna dokunmaz.</summary>
    public void UnmergeGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        using var conn = _factory.Open();
        conn.Execute("UPDATE Customer SET GroupId = NULL WHERE GroupId = @groupId",
            new { groupId });
    }

    /// <summary>
    /// Finds a grouped YouTube customer whose Username matches the given handle
    /// (case-insensitive). Used to adopt a chat channelId row into the person's
    /// group: the intake form stores a youtube row keyed by @handle, while chat
    /// messages arrive keyed by channelId — this bridges them via the handle
    /// (chat DisplayName). Returns null if no grouped handle row exists.
    /// </summary>
    public Customer? FindGroupedYouTubeByHandle(string handle)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Customer
              WHERE Platform = 'youtube' AND GroupId IS NOT NULL
                AND LOWER(Username) = LOWER(@handle)
              LIMIT 1",
            new { handle });
        return row is null ? null : Map(row);
    }

    /// <summary>Returns all currently-blacklisted customers, newest first.</summary>
    public IReadOnlyList<Customer> GetBlacklisted()
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Customer
              WHERE IsBlacklisted = 1
              ORDER BY COALESCE(BlacklistedAt, 0) DESC").ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Updates only the Notes column. Whitespace input normalizes to NULL.</summary>
    public void UpdateNotes(string customerId, string? notes)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Customer SET Notes=@notes WHERE Id=@id",
            new { id = customerId, notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });
    }

    /// <summary>
    /// En son görülen <paramref name="limit"/> müşteri, LastSeenAt DESC sıralı.
    /// Arama kutusu boşken gösterilen varsayılan liste — operatörün henüz
    /// siparişi olmayan yeni kayıtları görebilmesi için (arama olmadan hiçbir
    /// yerde görünmezlerdi).
    ///
    /// <para><b>Neden sınırlı.</b> Bu metot eskiden <c>GetAll()</c>'dü ve TÜM
    /// tabloyu materialize ediyordu — <see cref="Search"/>'ün R3-04'te
    /// düzeltilen sorununun aynısı, sadece süzgeçsiz hâli. Sıralama zaten
    /// LastSeenAt DESC olduğu için sınır listenin AMACINI bozmuyor: aranan şey
    /// "en yeniler". Daha eskisine ulaşmak arama kutusunun işi; kesme UI'da
    /// açıkça yazılıyor, sessizce eksik liste göstermiyoruz.</para>
    ///
    /// <para>Süzgeçler (R3-03 ile aynı gerekçe) SQL'in içinde, limit'ten ÖNCE
    /// uygulanır — dışarıda süzülseydi süzgece uyan eski kayıt, ilk
    /// <paramref name="limit"/> genel satırın dışında kalınca kaybolurdu.</para>
    /// </summary>
    public IReadOnlyList<Customer> GetRecent(
        int limit, string? platform = null, bool registeredOnly = false)
    {
        var filters = new StringBuilder();
        if (!string.IsNullOrEmpty(platform)) filters.Append(" AND Platform = @platform");
        if (registeredOnly) filters.Append(" AND Phone IS NOT NULL AND TRIM(Phone) <> ''");

        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            $@"SELECT * FROM Customer
               WHERE 1 = 1{filters}
               ORDER BY LastSeenAt DESC
               LIMIT @limit",
            new { limit, platform }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Taze pencere: ilk geçişin baktığı en yeni satır sayısı. Ölçümde (500.000
    /// satır) yoğun terimler pencereden 0,3-0,9 ms'de dönüyor; pencere dolmazsa
    /// ikinci geçiş devreye giriyor, yani doğruluk pencereye BAĞLI DEĞİL —
    /// pencere yalnızca hızlı yol.
    /// </summary>
    private const int FreshWindowSize = 5000;

    /// <summary>
    /// Kullanıcı adı VEYA isim araması (Username + DisplayName + FullName),
    /// LastSeenAt DESC sıralı. Eşleştirme kuralı yine
    /// <see cref="CustomerSearch.Matches"/>; buradaki SQL onun
    /// <see cref="CustomerSearchPlan"/> üzerinden üretilmiş birebir karşılığıdır
    /// (katlanmış SearchKey/PhoneKey kolonlarında <c>INSTR</c>).
    ///
    /// <para><b>R3-04 — neden iki geçiş.</b> Eskiden tüm tablo belleğe alınıp
    /// LINQ'te süzülüyordu (50.000 satırda 1.451 ms). Ölçüm, tek bir stratejinin
    /// yetmediğini gösterdi: <c>LastSeenAt</c> indeksinden geriye yürümek YOĞUN
    /// terimlerde çok hızlı (ilk 50 eşleşmede durur, 0,2-0,6 ms) ama NADİR
    /// terimde tüm tabloyu tarar (135-222 ms); FTS5 ise tam tersi — nadirde
    /// 0,9-3,3 ms, yoğunda bütün eşleşmeleri toplayıp sıralamak zorunda olduğu
    /// için 168-230 ms. Bu yüzden önce taze pencere denenir; pencere
    /// <paramref name="limit"/> kadar satır döndürdüyse sonuç KANITLANMIŞ
    /// doğrudur (sıralama LastSeenAt DESC olduğu için pencere dışındaki hiçbir
    /// satır ilk <paramref name="limit"/>'e giremez) ve ikinci geçişe hiç
    /// gidilmez.</para>
    ///
    /// <para>R3-03: Ek süzgeçler (<paramref name="platform"/>,
    /// <paramref name="registeredOnly"/>) SQL'in İÇİNDE, limit'ten ÖNCE
    /// uygulanır. Dışarıda süzülseydi, süzgece uyan ama ilk
    /// <paramref name="limit"/> genel eşleşmenin dışında kalan kayıt yanlış
    /// "boş sonuç" olarak kaybolurdu.</para>
    /// </summary>
    public IReadOnlyList<Customer> Search(
        string query, int limit = 50, string? platform = null, bool registeredOnly = false)
    {
        var plan = CustomerSearchPlan.Build(query);
        if (plan.MatchesNothing)
            return System.Array.Empty<Customer>();

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = limit,
            ["window"] = FreshWindowSize
        };
        var where = plan.BuildWhereClause("c", parameters);

        var filters = new StringBuilder();
        if (!string.IsNullOrEmpty(platform))
        {
            parameters["platform"] = platform;
            filters.Append(" AND c.Platform = @platform");
        }
        if (registeredOnly)
            filters.Append(" AND c.Phone IS NOT NULL AND TRIM(c.Phone) <> ''");

        using var conn = _factory.Open();

        // 1. geçiş — yalnız en yeni FreshWindowSize satır.
        var window = conn.Query<Row>(
            $@"SELECT c.* FROM (
                   SELECT * FROM Customer ORDER BY LastSeenAt DESC LIMIT @window
               ) c
               WHERE {where}{filters}
               ORDER BY c.LastSeenAt DESC
               LIMIT @limit", parameters).ToList();

        if (window.Count >= limit)
            return window.Select(Map).ToList();

        // 2. geçiş — tüm tablo. Trigram indeksi yalnız her terim 3 harften uzunsa
        // kullanılabilir; kısa terimde MATCH hata vermeden BOŞ dönerdi (R3-03'ün
        // yanlış-boş sınıfı), o yüzden tam tarama.
        var sql = plan.CanUseTrigram
            ? $@"SELECT c.* FROM CustomerFts f
                 JOIN Customer c ON c.rowid = f.rowid
                 WHERE CustomerFts MATCH @match AND {where}{filters}
                 ORDER BY c.LastSeenAt DESC
                 LIMIT @limit"
            : $@"SELECT c.* FROM Customer c
                 WHERE {where}{filters}
                 ORDER BY c.LastSeenAt DESC
                 LIMIT @limit";

        if (plan.CanUseTrigram)
            parameters["match"] = plan.BuildMatchExpression();

        return conn.Query<Row>(sql, parameters).Select(Map).ToList();
    }

    private static Customer Map(Row r) => new(
        r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
        r.FirstSeenAt, r.LastSeenAt,
        r.IsBlacklisted == 1, r.BlacklistReason, r.Notes,
        r.TotalLabelsPrinted, r.TotalAmount, r.BlacklistedAt, r.Address, r.Phone,
        RecipientPaysActive: r.RecipientPaysActive == 1,
        GroupId: r.GroupId,
        Email: r.Email,
        Tckn: r.Tckn,
        WhatsAppConsent: r.WhatsAppConsent == 1,
        SmsConsent: r.SmsConsent == 1,
        FullName: r.FullName,
        City: r.City,
        District: r.District,
        SyncSeq: r.SyncSeq);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? AvatarUrl { get; init; }
        public long FirstSeenAt { get; init; }
        public long LastSeenAt { get; init; }
        public int IsBlacklisted { get; init; }
        public string? BlacklistReason { get; init; }
        public string? Notes { get; init; }
        public int TotalLabelsPrinted { get; init; }
        public decimal TotalAmount { get; init; }
        public long? BlacklistedAt { get; init; }
        public string? Address { get; init; }
        public string? Phone { get; init; }
        public int RecipientPaysActive { get; init; }
        public string? GroupId { get; init; }
        public string? Email { get; init; }
        public string? Tckn { get; init; }
        public int WhatsAppConsent { get; init; }
        public int SmsConsent { get; init; }
        public string? FullName { get; init; }
        public string? City { get; init; }
        public string? District { get; init; }
        public long SyncSeq { get; init; }
    }

    /// <summary>
    /// Upsert by (Platform, Username). Phase 4f intake form sync için.
    /// Mevcut müşteri varsa DisplayName, Address, Phone birim birim (form damgası
    /// yerel damgadan yeniyse ya da yerel birim damgasızsa; boş form alanı
    /// yazılmaz — kural 3) ve LastSeenAt güncellenir; yoksa yeni satır insert edilir.
    /// Dönen kayıt veritabanındaki hâldir. Arama <see cref="FindByPlatformAndUsername"/>
    /// ile aynı (birebir, yoksa kimlik anahtarı); arama ve yazım tek yazma işleminde
    /// (bkz. <see cref="UpsertPersonFromIntake"/>).
    /// </summary>
    public Customer UpsertFromIntakeForm(string username, string fullName, string address, string? phone, long nowUnix, long submittedAtMs)
    {
        const string platform = "form";
        var fullNameValue = Clean(fullName);
        var addressValue = Clean(address);
        var phoneValue = Clean(phone);
        // Damga = formun gönderim anı (kural 3).
        var at = submittedAtMs;

        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;

        var existing = FindRow(conn, tx, platform, username);

        string id;
        if (existing is not null)
        {
            // Birim birim: değer doluysa VE yerel damga daha eski (ya da yoksa). Eski
            // form "adres" alanı tek satırdır, il/ilçe bilmez → blok bütün yazılır
            // (City/District = NULL); "form" satırlarında zaten hep boş.
            //
            // N03-k: LastSeenAt = @nowUnix düz yazımı, satır delta imlecinin
            // üzerinde/ötesindeyse güncellemeyi imlecin ARKASINDA bırakıyordu.
            // MAX(LastSeenAt+1, @nowUnix) satır başına kesin artan (bkz.
            // UpdatePhone'daki N03 düzeltmesi).
            conn.Execute(@"
                UPDATE Customer SET
                    DisplayName          = CASE WHEN @fullName IS NOT NULL AND (DisplayNameChangedAt IS NULL OR DisplayNameChangedAt < @at) THEN @fullName ELSE DisplayName END,
                    DisplayNameChangedAt = CASE WHEN @fullName IS NOT NULL AND (DisplayNameChangedAt IS NULL OR DisplayNameChangedAt < @at) THEN @at ELSE DisplayNameChangedAt END,
                    Address          = CASE WHEN @address IS NOT NULL AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @address ELSE Address END,
                    City             = CASE WHEN @address IS NOT NULL AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN NULL ELSE City END,
                    District         = CASE WHEN @address IS NOT NULL AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN NULL ELSE District END,
                    AddressChangedAt = CASE WHEN @address IS NOT NULL AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @at ELSE AddressChangedAt END,
                    Phone          = CASE WHEN @phone IS NOT NULL AND (PhoneChangedAt IS NULL OR PhoneChangedAt < @at) THEN @phone ELSE Phone END,
                    PhoneChangedAt = CASE WHEN @phone IS NOT NULL AND (PhoneChangedAt IS NULL OR PhoneChangedAt < @at) THEN @at ELSE PhoneChangedAt END,
                    LastSeenAt = MAX(LastSeenAt + 1, @nowUnix) -- N03-k
                WHERE Id = @id
                  AND PurgedAt IS NULL -- R10-D02: KVKK tombstone'u geç gelen form cevabına yenilmez",
                new { id = existing.Id, fullName = fullNameValue, address = addressValue, phone = phoneValue, at, nowUnix },
                tx);
            id = existing.Id;
        }
        else
        {
            id = Guid.NewGuid().ToString("N");
            conn.Execute(@"
                INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                                      IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                                      BlacklistedAt, Address, Phone,
                                      DisplayNameChangedAt, AddressChangedAt, PhoneChangedAt)
                VALUES (@id, @platform, @username, @key, @fullName, NULL, @nowUnix, @nowUnix,
                        0, NULL, NULL, 0, 0, NULL, @address, @phone,
                        @displayAt, @addressAt, @phoneAt)",
                new
                {
                    id, platform, username, key = CustomerIdentity.KeyOrNull(username),
                    fullName = fullNameValue, nowUnix, address = addressValue, phone = phoneValue,
                    displayAt = fullNameValue is null ? (long?)null : at,
                    addressAt = addressValue is null ? (long?)null : at,
                    phoneAt = phoneValue is null ? (long?)null : at,
                }, tx);

            // R11-D01: yerelde satır YOKKEN inmiş bir KVKK tombstone'u varsa satır
            // boş doğar. Yukarıdaki UPDATE dalı zaten kapılıydı; açık olan tek
            // kapak buydu — silinen kişi, gecikmiş form cevabıyla sıfırdan
            // diriliyordu.
            conn.Execute(ScrubIfTombstonedSql, new { id }, tx);
        }

        // Yazılmak istenen değil, veritabanındaki hâl: damga kuralı birimleri
        // atlamış ya da silinmiş satıra hiç yazılmamış olabilir (R10-D02) —
        // iyimser kopya dönmek diriltilmiş veriyi çağırana (UI/sync) sızdırırdı.
        var stored = conn.QueryFirst<Row>("SELECT * FROM Customer WHERE Id = @id", new { id }, tx);
        write.Commit();
        return Map(stored);
    }

    /// <summary>
    /// Intake form çoklu-platform upsert. Kişinin bildirdiği her platform kimliği
    /// için bir Customer satırı oluşturur/günceller ve hepsini tek bir
    /// <c>GroupId</c> ile bağlar. Kimliklerden biri zaten bir gruba aitse o grup
    /// yeniden kullanılır (kimlikler tek kişide birleşir), yoksa yeni grup formun
    /// kimliğinden (<paramref name="formId"/>) türer.
    /// Handle normalize: trim + baştaki '@' atılır. IG/TikTok/FB'de Username = chat
    /// handle olduğundan chat satırıyla doğal birleşir; YouTube form satırı @handle
    /// ile durur (channelId adopsiyonu Faz 3'te). İletişim/izin bilgisi tüm satırlara
    /// yazılır; DisplayName boşsa Ad Soyad'a set edilir. Grup id'yi döner.
    ///
    /// <para>Bölüm C kural 3: her birim formun gönderim anıyla
    /// (<paramref name="submittedAtMs"/>) damgalanır ve yalnız yerel birim damgası daha
    /// eskiyse ya da yoksa yazılır; boş form alanı yazılmaz. Form tek yazma işleminde
    /// uygulanır.</para>
    /// </summary>
    /// <param name="formId">Sunucudaki form gönderiminin kimliği (zorunlu, boş olamaz).</param>
    public string UpsertPersonFromIntake(
        IReadOnlyList<(string Platform, string Username, string? PreferredDisplayName)> identities,
        string fullName, string address, string? phone,
        string? email, string? tckn, bool whatsAppConsent, bool smsConsent,
        long nowUnix, Guid formId, long submittedAtMs, string? city = null, string? district = null)
    {
        // Normalize + boşları ele. PreferredDisplayName: YouTube'da channelId
        // Username olduğunda operatöre @handle gösterilsin diye taşınır (UI asla
        // channelId göstermez).
        var norm = new List<(string Platform, string Username, string? Display)>();
        foreach (var (p, u, disp) in identities)
        {
            var handle = (u ?? "").Trim().TrimStart('@').Trim();
            if (handle.Length > 0) norm.Add((p, handle, string.IsNullOrWhiteSpace(disp) ? null : disp!.Trim()));
        }
        if (norm.Count == 0) throw new ArgumentException("En az bir kimlik gerekli", nameof(identities));

        // Gerçek Ad Soyad'ı ayrı kolona yaz (boşsa null). DisplayName fallback'ı
        // aşağıda ayrıca korunur (chat takma adı ezilmesin diye).
        // Boş form alanı YAZILMAZ (kural 3). Adres bloğu tek birim: parçalarından biri
        // doluysa blok formun hâliyle yazılır (boş parça = NULL), hiçbiri doluysa yazılmaz.
        // İl/ilçe 2026-08-03'ten sonraki formlardan gelir; eski gönderimlerde boş.
        var fullNameValue = Clean(fullName);
        var phoneValue = Clean(phone);
        var addressValue = Clean(address);
        var cityValue = Clean(city);
        var districtValue = Clean(district);
        var emailValue = Clean(email);
        var tcknValue = Clean(tckn);
        var hasAddress = addressValue is not null || cityValue is not null || districtValue is not null;
        // Damga = formun gönderim anı, işleme anı DEĞİL: her bilgisayar formları kendi
        // imleciyle oynatır; geç açılan bilgisayar eski formu "şimdi" damgasıyla yazsaydı
        // sonradan yapılmış elle düzeltmeleri her yerde ezerdi.
        var at = submittedAtMs;
        if (formId == Guid.Empty)
            throw new ArgumentException(
                "Formun kimliği gerekli: yeni grup ondan türer — boş kimlik ilgisiz kişileri tek grupta toplardı",
                nameof(formId));

        // Bütün form TEK yazma işleminde (BEGIN IMMEDIATE — okumadan önce yazma kilidi):
        // arama → grup çözümü → güncelleme/ekleme → telefonla gruplama → kara liste
        // yayılımı. Ayrı ifadelerde kalsaydı arada bir yeniden anahtarlama (kendi
        // işleminde kopyayı siler, yönlendirme yazar) Id ile yazımı 0 satıra düşürür,
        // form imleci ilerlediği için de form o bilgisayarda sessizce kaybolurdu; geç bir
        // hata da yarım uygulanmış form bırakırdı. Her okuma ve yazma işlemin bağlantısıyla
        // (U17).
        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;

        // Grup id çözümle: kimliklerden biri zaten gruplanmışsa onu kullan.
        string? groupId = null;
        foreach (var (p, u, _) in norm)
        {
            var existing = FindExistingForIntake(conn, tx, p, u);
            if (existing?.GroupId is { Length: > 0 } g) { groupId = g; break; }
        }
        // Telefon-bazlı: kimlikler eşleşmese bile aynı telefonlu mevcut bir grup
        // varsa onu kullan (aynı kişi başka platformdan tekrar kaydolduğunda).
        if (groupId is null && phoneValue is not null)
        {
            var byPhone = conn.QueryFirstOrDefault<string>(
                @"SELECT GroupId FROM Customer
                  WHERE Phone = @phoneValue AND GroupId IS NOT NULL AND TRIM(GroupId) <> ''
                  LIMIT 1",
                new { phoneValue }, tx);
            if (!string.IsNullOrWhiteSpace(byPhone)) groupId = byPhone;
        }
        // Yeni grup formun kimliğinden türer (bugünkü GroupId biçimi, "N"), rastgele DEĞİL:
        // grup birimi formun damgasını taşır; aynı formu birbirinin gönderimini görmeden
        // işleyen iki bilgisayar rastgele grupla eşit damgalı iki farklı değer yazardı —
        // sunucu da istemci de eşit damgayı yok saydığı için hiç yakınsamazlardı (kart
        // bölünür, toplam yanlış, grup kara listesi yanlış kümede).
        groupId ??= formId.ToString("N");

        foreach (var (p, u, disp) in norm)
        {
            // Gösterilecek ad: YouTube'da channelId Username olduğunda @handle (disp);
            // diğerlerinde Ad Soyad. UI asla channelId göstermesin diye önemli.
            //
            // Kabul edilen sınır: takma ad yalnız yerelde BOŞSA doldurulur ama formun
            // damgasını taşır. Eşzamanlı pencerede (bu bilgisayar satırı formdan açtı, başka
            // bilgisayarda aynı kimliğin sohbetten gelmiş dolu takma adı var ve sohbet anı
            // formdan eski) sunucu birim kuralıyla formun değerini seçer; öbür bilgisayardaki
            // takma ad Ad Soyad'la değişir. Kabul edildi.
            var displayForRow = disp ?? fullNameValue;
            var existing = FindExistingForIntake(conn, tx, p, u);
            if (existing is not null)
            {
                conn.Execute(IntakeUpdateSql, new
                {
                    id = existing.Id, at, now = nowUnix, groupId,
                    hasAddress = hasAddress ? 1 : 0, address = addressValue, city = cityValue, district = districtValue,
                    phone = phoneValue, email = emailValue, tckn = tcknValue,
                    wa = whatsAppConsent ? 1 : 0, sms = smsConsent ? 1 : 0,
                    displayForRow, fullName = fullNameValue,
                }, tx);
            }
            else
            {
                var newId = Guid.NewGuid().ToString("N");
                conn.Execute(IntakeInsertSql, new
                {
                    id = newId, p, u, key = CustomerIdentity.KeyOrNull(u), displayForRow, now = nowUnix,
                    address = addressValue, city = cityValue, district = districtValue,
                    phone = phoneValue, groupId, email = emailValue, tckn = tcknValue,
                    wa = whatsAppConsent ? 1 : 0, sms = smsConsent ? 1 : 0, fullName = fullNameValue,
                    // Yalnız DOLU birim damgalanır; izin her zaman formun cevabıdır.
                    displayAt = displayForRow is null ? (long?)null : at,
                    groupAt = at,
                    addressAt = hasAddress ? at : (long?)null,
                    phoneAt = phoneValue is null ? (long?)null : at,
                    emailAt = emailValue is null ? (long?)null : at,
                    tcknAt = tcknValue is null ? (long?)null : at,
                    consentAt = at,
                    fullNameAt = fullNameValue is null ? (long?)null : at,
                }, tx);

                // R11-D01: yukarıdaki UPDATE dalı "AND PurgedAt IS NULL" ile
                // kapılıydı ama yerelde satırı hiç olmayan silinmiş kimlik bu
                // dala düşüyor ve formun TÜM kişisel verisini (ad, adres,
                // telefon, e-posta, TCKN) sıfırdan yazıyordu. Karar artık
                // kimliğin kendisinde duruyor; satır boş doğuyor.
                conn.Execute(ScrubIfTombstonedSql, new { id = newId }, tx);
            }
        }

        // Telefon-bazlı otomatik birleştirme: aynı telefona sahip mevcut TÜM
        // müşterileri (ve ait oldukları grupların kalan üyelerini) bu gruba çek.
        // Böylece aynı kişi farklı platformdan ayrı ayrı kaydolmuşsa tek kart olur.
        // BAŞKA satırların GroupId'si de formdan türeyen yazımdır → aynı damga, aynı
        // "yalnız daha yeniyse" kuralı (sonradan elle ayrılan satır geri bağlanmaz).
        if (phoneValue is not null)
        {
            var otherGroups = conn.Query<string>(
                @"SELECT DISTINCT GroupId FROM Customer
                  WHERE Phone = @phoneValue AND GroupId IS NOT NULL
                    AND TRIM(GroupId) <> '' AND GroupId <> @groupId",
                new { phoneValue, groupId }, tx)
                .Where(g => !string.IsNullOrWhiteSpace(g)).ToList();

            conn.Execute(
                @"UPDATE Customer SET GroupId = @groupId, GroupIdChangedAt = @at
                  WHERE Phone = @phoneValue AND (GroupId IS NULL OR GroupId <> @groupId)
                    AND (GroupIdChangedAt IS NULL OR GroupIdChangedAt < @at)",
                new { groupId, phoneValue, at }, tx);

            if (otherGroups.Count > 0)
                conn.Execute(
                    @"UPDATE Customer SET GroupId = @groupId, GroupIdChangedAt = @at
                      WHERE GroupId IN @otherGroups
                        AND (GroupIdChangedAt IS NULL OR GroupIdChangedAt < @at)",
                    new { groupId, otherGroups, at }, tx);
        }

        // Kara liste yayılımı HER formda (telefonla gruplamadan sonra): telefonsuz form
        // da kimlikleri gruba koyar (mevcut gruba katılım ya da yeni grup). Kara liste
        // satır bayrağından okunduğu için (sohbet, etiket kuyruğu, çekiliş) yayılım
        // yalnız telefon dalında koşunca telefonsuz formla gruba giren yeni kimlik
        // işaretsiz kalıyor, kişi o platformdan alışveriş yapabiliyordu.
        // Yayılım da formdan türer: formun damgası, aynı "yalnız daha yeniyse" kuralı.
        // Kaynak üyenin kara liste tarihi yoksa yayılan tarih de formdan (gönderim
        // anı, sn): işleme anı aynı formu işleyen bilgisayarlarda farklı olurdu.
        PropagateGroupBlacklist(conn, tx, groupId, fallbackAt: at / 1000, formAt: at);

        write.Commit();
        return groupId;
    }

    /// <summary>Form → mevcut satır. Her birim: form değeri doluysa VE yerel damga daha
    /// eski ya da yoksa yaz + damga = @at (kural 3). Eşit damga yankıdır. SET ifadeleri
    /// ESKİ satır değerlerine göre hesaplanır (SQL), yani değer ve damga CASE'leri aynı
    /// koşulu görür. Damga değiştiği için damga tetikleyicisi çalışmaz.
    ///
    /// <para>SyncSeq tetikleyicisi değere değil SET listesine bakar (<c>UPDATE OF</c>): bu
    /// ifade bütün birim kolonlarını SET'te taşıdığı için (silinmemiş) satırı HER koşuşta —
    /// hiçbir birim yazılmasa da, eşit damgalı yankıda bile — gönderime yeniden koyar.
    /// Zararsız (sunucu aynı damgaları yok sayar), yalnız fazladan bir gönderim.</para></summary>
    private const string IntakeUpdateSql = @"
        UPDATE Customer SET
            GroupId          = CASE WHEN GroupIdChangedAt IS NULL OR GroupIdChangedAt < @at THEN @groupId ELSE GroupId END,
            GroupIdChangedAt = CASE WHEN GroupIdChangedAt IS NULL OR GroupIdChangedAt < @at THEN @at ELSE GroupIdChangedAt END,
            Address  = CASE WHEN @hasAddress = 1 AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @address  ELSE Address  END,
            City     = CASE WHEN @hasAddress = 1 AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @city     ELSE City     END,
            District = CASE WHEN @hasAddress = 1 AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @district ELSE District END,
            AddressChangedAt = CASE WHEN @hasAddress = 1 AND (AddressChangedAt IS NULL OR AddressChangedAt < @at) THEN @at ELSE AddressChangedAt END,
            Phone          = CASE WHEN @phone IS NOT NULL AND (PhoneChangedAt IS NULL OR PhoneChangedAt < @at) THEN @phone ELSE Phone END,
            PhoneChangedAt = CASE WHEN @phone IS NOT NULL AND (PhoneChangedAt IS NULL OR PhoneChangedAt < @at) THEN @at ELSE PhoneChangedAt END,
            Email          = CASE WHEN @email IS NOT NULL AND (EmailChangedAt IS NULL OR EmailChangedAt < @at) THEN @email ELSE Email END,
            EmailChangedAt = CASE WHEN @email IS NOT NULL AND (EmailChangedAt IS NULL OR EmailChangedAt < @at) THEN @at ELSE EmailChangedAt END,
            Tckn          = CASE WHEN @tckn IS NOT NULL AND (TcknChangedAt IS NULL OR TcknChangedAt < @at) THEN @tckn ELSE Tckn END,
            TcknChangedAt = CASE WHEN @tckn IS NOT NULL AND (TcknChangedAt IS NULL OR TcknChangedAt < @at) THEN @at ELSE TcknChangedAt END,
            WhatsAppConsent          = CASE WHEN WhatsAppConsentChangedAt IS NULL OR WhatsAppConsentChangedAt < @at THEN @wa ELSE WhatsAppConsent END,
            WhatsAppConsentChangedAt = CASE WHEN WhatsAppConsentChangedAt IS NULL OR WhatsAppConsentChangedAt < @at THEN @at ELSE WhatsAppConsentChangedAt END,
            SmsConsent          = CASE WHEN SmsConsentChangedAt IS NULL OR SmsConsentChangedAt < @at THEN @sms ELSE SmsConsent END,
            SmsConsentChangedAt = CASE WHEN SmsConsentChangedAt IS NULL OR SmsConsentChangedAt < @at THEN @at ELSE SmsConsentChangedAt END,
            DisplayName          = CASE WHEN NULLIF(TRIM(DisplayName), '') IS NULL AND @displayForRow IS NOT NULL
                                         AND (DisplayNameChangedAt IS NULL OR DisplayNameChangedAt < @at)
                                        THEN @displayForRow ELSE DisplayName END,
            DisplayNameChangedAt = CASE WHEN NULLIF(TRIM(DisplayName), '') IS NULL AND @displayForRow IS NOT NULL
                                         AND (DisplayNameChangedAt IS NULL OR DisplayNameChangedAt < @at)
                                        THEN @at ELSE DisplayNameChangedAt END,
            FullName          = CASE WHEN @fullName IS NOT NULL AND (FullNameChangedAt IS NULL OR FullNameChangedAt < @at) THEN @fullName ELSE FullName END,
            FullNameChangedAt = CASE WHEN @fullName IS NOT NULL AND (FullNameChangedAt IS NULL OR FullNameChangedAt < @at) THEN @at ELSE FullNameChangedAt END,
            LastSeenAt = MAX(LastSeenAt + 1, @now) -- N03-k: delta imleci için kesin artan
        WHERE Id = @id
          AND PurgedAt IS NULL -- R10-D02: KVKK tombstone'u geç gelen form cevabına yenilmez";

    /// <summary>Form → yeni satır. Dolu birimler açık damga taşır; GroupId ve izinler
    /// (WhatsApp, SMS) HER ZAMAN damgalanır — grup formdan türer, izin formun cevabıdır
    /// ("hayır" da). Boş birimin damgası null: INSERT tetikleyicisi de damgalamaz, çünkü
    /// birim boştur.</summary>
    private const string IntakeInsertSql = @"
        INSERT INTO Customer
          (Id, Platform, Username, IdentityKey, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
           IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
           BlacklistedAt, Address, Phone, RecipientPaysActive,
           GroupId, Email, Tckn, WhatsAppConsent, SmsConsent, FullName, City, District,
           DisplayNameChangedAt, GroupIdChangedAt, AddressChangedAt, PhoneChangedAt, EmailChangedAt,
           TcknChangedAt, WhatsAppConsentChangedAt, SmsConsentChangedAt, FullNameChangedAt)
        VALUES
          (@id, @p, @u, @key, @displayForRow, NULL, @now, @now,
           0, NULL, NULL, 0, 0,
           NULL, @address, @phone, 0,
           @groupId, @email, @tckn, @wa, @sms, @fullName, @city, @district,
           @displayAt, @groupAt, @addressAt, @phoneAt, @emailAt,
           @tcknAt, @consentAt, @consentAt, @fullNameAt)";

    /// <summary>Grupta kara listede en az bir üye varsa, o üyenin sebep/tarihiyle
    /// tüm grubu kara listeye alır (birleştirme kara liste kaçışını kapatsın diye).
    /// Verilen açık bağlantıyı (ve varsa işlemi) kullanır.</summary>
    /// <param name="fallbackAt">Kaynak üyenin kara liste tarihi yoksa yazılacak tarih (sn).</param>
    /// <param name="formAt">Doluysa yayılım formdan türeyen yazımdır: kara liste damgası =
    /// formun gönderim anı ve yalnız yerel damga daha eskiyse ya da yoksa yazılır — eski
    /// formun geç oynatılması, başka bilgisayarda sonradan kara listeden çıkarılmış satırı
    /// yeniden kara listeye almaz. Boşsa (elle birleştirme) yerel eylemdir: tetikleyici
    /// "şimdi" damgalar.</param>
    private static void PropagateGroupBlacklist(
        System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, string groupId, long fallbackAt,
        long? formAt)
    {
        // Eşit tarihte Id bozar: tarama sırası (ekleme sırası) bilgisayardan bilgisayara
        // değişir; aynı veriye sahip iki bilgisayar farklı kaynak seçip eşit damgayla farklı
        // sebep yayardı.
        var b = conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Customer
              WHERE GroupId = @groupId AND IsBlacklisted = 1
              ORDER BY COALESCE(BlacklistedAt, 0) DESC, Id LIMIT 1",
            new { groupId }, tx);
        if (b is null) return;

        var reason = b.BlacklistReason;
        var at = b.BlacklistedAt ?? fallbackAt;
        if (formAt is null)
            conn.Execute(
                @"UPDATE Customer SET IsBlacklisted = 1, BlacklistReason = @reason, BlacklistedAt = @at
                  WHERE GroupId = @groupId AND IsBlacklisted = 0",
                new { groupId, reason, at }, tx);
        else
            conn.Execute(
                @"UPDATE Customer SET IsBlacklisted = 1, BlacklistReason = @reason, BlacklistedAt = @at,
                                      BlacklistChangedAt = @formAt
                  WHERE GroupId = @groupId AND IsBlacklisted = 0
                    AND (BlacklistChangedAt IS NULL OR BlacklistChangedAt < @formAt)",
                new { groupId, reason, at, formAt }, tx);
    }

    /// <summary>
    /// Intake form kimliğini mevcut bir Customer satırıyla eşleştirir; böylece
    /// alışveriş geçmişi olan (numarasız otomatik kaydedilmiş) müşteri, formu
    /// doldurduğunda AYRI satır açmaz, mevcut satırı güncelleriz.
    /// <list type="number">
    ///   <item>Önce <c>(Platform, Username)</c> birebir — büyük/küçük harf
    ///   duyarsız (COLLATE NOCASE) → IG/TikTok/FB'de handle chat'le birebir aynı.</item>
    ///   <item>Kimlik anahtarı (U7): NOCASE yalnız ASCII katlar; "ŞEYMA" ile "şeyma"yı
    ///   anahtar eşler.</item>
    ///   <item>YouTube özel: chat satırının Username'i channelId (UCxxx), form ise
    ///   @handle verir → doğrudan tutmaz. Bu satırların <c>DisplayName</c>'i @handle
    ///   tuttuğu için handle'ı DisplayName ile eşleştirip channelId satırını buluruz
    ///   (Username=channelId korunur, gelecekteki chat de eşleşmeye devam eder).</item>
    /// </list>
    /// Eşleşme yoksa null döner → çağıran yeni satır açar (regresyon yok). Çağıranın
    /// yazma işleminin içinde koşar (<paramref name="tx"/>).
    /// </summary>
    private static Row? FindExistingForIntake(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, string platform, string handle)
    {
        // 1) (Platform, Username) birebir — harf duyarsız.
        var exact = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Platform=@platform AND Username=@handle COLLATE NOCASE",
            new { platform, handle }, tx);
        if (exact is not null) return exact;

        // 2) Kimlik anahtarı: NOCASE yalnız ASCII katlar ("ŞEYMA" ≠ "şeyma").
        var byKey = FindByIdentity(conn, tx, platform, handle);
        if (byKey is not null) return byKey;

        // 3) YouTube: chat satırı channelId ile; @handle DisplayName'de saklı.
        if (string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return conn.QueryFirstOrDefault<Row>(
                @"SELECT * FROM Customer
                  WHERE Platform='youtube'
                    AND LTRIM(DisplayName, '@') = @handle COLLATE NOCASE
                  LIMIT 1",
                new { handle }, tx);
        }

        return null;
    }

    /// <summary>
    /// KVKK silme talebi: yayıncının kendi bilgisayarındaki kişisel alanları
    /// kalıcı olarak boşaltır. Sunucudaki <c>ShopperPurgeService</c>'in üçüncü
    /// katmanı — tetikleyici <c>CustomerChangesPullService</c>'in akışından gelen
    /// <c>PurgedAt</c> işareti.
    ///
    /// <para><b>Satır silinmiyor, boşaltılıyor.</b> Sunucudaki desenin aynısı:
    /// <c>Label</c> ve <c>Order</c> satırları <c>Customer.Id</c>'ye bağlı; satır
    /// gidince mali geçmiş sahipsiz kalır ve ciro raporları sessizce bozulur.</para>
    ///
    /// <para><b>Kalanlar ve gerekçesi:</b> <c>Platform</c>/<c>Username</c> kimlik
    /// anahtarı — silinirse aynı kişi yayında tek bir yorum yazdığı anda chat
    /// akışı satırı bulamaz ve TEMİZ bir kopyasını yeniden açar; kalınca mevcut
    /// boş satırla eşleşir, yalnız <c>LastSeenAt</c> ilerler.
    /// <c>TotalAmount</c>/<c>TotalLabelsPrinted</c> mali kayıt.
    /// <c>IsBlacklisted</c> sahtekârlık koruması (sunucuda ödeme hash'lerinin
    /// kalmasıyla aynı gerekçe) — temizlenseydi silme talebi kara listeden
    /// çıkmanın yolu olurdu. <c>Notes</c> yayıncının kendi yazdığı işletme
    /// notu; serbest metin olduğu için kişisel veri içerebilir ama silinmesi
    /// operatörün kendi kaydını yok etmek olur — bilerek dokunulmuyor.</para>
    ///
    /// <para><c>LastSeenAt</c>'e DOKUNULMUYOR: iş zamanıdır ("en son görülme"),
    /// silme bir görülme değildir. Gönderime düşme ona bağlı değil (imleç
    /// <c>SyncSeq</c> — N03-g); yerelde silinmiş satır zaten gönderilmez (C6).</para>
    ///
    /// <para><b>R10-D02 (2026-09-15):</b> boşaltma tek başına kalıcı bariyer
    /// değildi — silmeden önce çekilmiş ama sonra uygulanan bir form cevabı
    /// (<see cref="UpsertPersonFromIntake"/>) ya da
    /// <see cref="BackfillFullNameForIdentities"/> alanları geri dolduruyordu
    /// ve ingest imleci tombstone'un ötesinde olduğu için scrub bir daha
    /// koşmuyordu. Artık karar <c>PurgedAt</c> damgasıyla satırda yaşar ve
    /// intake/backfill yazıları <c>AND PurgedAt IS NULL</c> ile kapılıdır.
    /// <c>COALESCE</c>: tekrarlanan scrub (ör. eski yedek geri yüklenip
    /// tombstone yeniden okunduğunda) İLK silme tarihini korur.</para>
    /// </summary>
    /// <returns>Güncellenen satır sayısı; bilinmeyen id'de 0.</returns>
    public int ScrubPersonalData(string customerId)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE Customer SET " + ScrubAssignments + @",
                  PurgedAt        = COALESCE(PurgedAt, @now)
              WHERE Id = @id",
            new { id = customerId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>
    /// R11-D01: KVKK silme kararını kimlik düzeyinde kaydeder ve varsa yerel
    /// satırı boşaltır. <see cref="ScrubPersonalData"/>'nın yerini alır —
    /// çünkü <b>silme kararının yerel satırın varlığına bağlı olmaması</b>
    /// gerekiyor.
    ///
    /// <para>Eski davranış: yerelde eşleşen satır yoksa hiçbir şey yazılmıyor,
    /// ingest imleci ilerliyordu. Karar hiçbir yerde durmadığı için sonradan
    /// inen bir intake form cevabı (ya da kişinin yayına yazdığı tek bir
    /// yorum) aynı kimliği sıfırdan, tam kişisel veriyle açıyordu; tombstone
    /// bir daha inmediği için de ihlal kalıcılaşıyordu.</para>
    ///
    /// <para>Tombstone satırı hem varken hem yokken yazılır: satır varsa bile
    /// yedekten geri yükleme onu geri götürebilir, karar ise kimlik
    /// tablosunda kalır. <c>MIN</c>: tekrarlanan bildirim İLK silme tarihini
    /// korur (<see cref="ScrubPersonalData"/>'daki <c>COALESCE</c> ile aynı
    /// gerekçe — tarih adli kayıt).</para>
    ///
    /// <para>Boşaltma <c>Id</c> ile değil <c>(Platform, Username)</c> ile
    /// yapılıyor: aynı kimlikten birden fazla satır kalmışsa (eski
    /// birleştirmelerden) hepsi kapsanır. Harf duyarsız: NOCASE (ASCII) +
    /// kimlik anahtarı (U16 — ASCII dışı harf farkı); mezar taşı da anahtarı
    /// taşır.</para>
    ///
    /// <para>Bölüm C kural 2 + 8: akıştan inen silme kilit altında
    /// (<see cref="SyncApplyScope"/>) yazılır — düzenleme değildir.</para>
    /// </summary>
    /// <returns>Boşaltılan yerel satır sayısı; satır yoksa 0 — karar yine de yazılmıştır.</returns>
    public int RecordPurge(string platform, string username, long purgedAtUnix)
        => RecordPurge(platform, username, purgedAtUnix, out _);

    /// <inheritdoc cref="RecordPurge(string, string, long)"/>
    /// <param name="changed">Karar bu çağrıyla YENİ bir şey yaptı: yeni mezar taşı (kimlik ya da
    /// YouTube alias'ı) yazıldı ya da henüz silinmemiş (<c>PurgedAt</c> boş) bir satır boşaltıldı.
    /// Zaten uygulanmış silmenin tekrarı (akış CursorReset'le yeniden oynatıldı) false — akış
    /// servisinin "KVKK silme" günlüğü yinelenmez (C7 incelemesi M-4).</param>
    public int RecordPurge(string platform, string username, long purgedAtUnix, out bool changed)
    {
        // Akıştan inen KVKK kararı bir DÜZENLEME değildir (kural 2): kilit altında
        // yazılır — boşaltılan birimler "şimdi" damgalanmaz, SyncSeq ilerlemez (sunucu
        // satırı zaten silinmiş; geri göndermek boşuna tur).
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        var newTombstones = UpsertTombstone(conn, tx, platform, username, purgedAtUnix) ? 1 : 0;

        // R12-D01: YouTube'da kararın kapsamı, uygulamanın eşleştirmede
        // GÜVENDİĞİ bağın tamamı olmalı. Boşaltma DisplayName'i de siliyor;
        // onunla birlikte <see cref="FindExistingForIntake"/>'in handle
        // köprüsü de kopuyor. Silmeden ÖNCE kabul edilmiş ama SONRA uygulanan
        // handle-only bir form artık kanal satırını bulamayıp handle adına
        // YENİ satır açıyordu ve tombstone yalnız channelId'yi tanıdığı için
        // o satır temizlenmiyordu. Alias, köprü kopmadan ÖNCE okunur ve aynı
        // kararın ikinci kimliği olarak yazılır.
        //
        // Kapsam bilerek dar: yalnız handle-only girdi. Kanonik kanal kimliği
        // çözülebiliyorsa satır kendi Username'iyle açılır ve bu tombstone'a
        // takılmaz — yani handle'ı ileride alan BAŞKA biri kaydolabilir.
        // '[Silindi]' ve alias == Username elenir: tekrarlanan silme, kararı
        // boşaltma etiketine ya da kendi kimliğine genişletmesin.
        if (string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            var aliases = conn.Query<string>(
                @"SELECT DISTINCT LTRIM(TRIM(DisplayName), '@') AS Alias
                  FROM Customer
                  WHERE Platform = @platform AND Username = @username COLLATE NOCASE
                    AND DisplayName IS NOT NULL
                    AND TRIM(DisplayName) <> '[Silindi]'
                    AND LENGTH(LTRIM(TRIM(DisplayName), '@')) > 0
                    AND LTRIM(TRIM(DisplayName), '@') <> @username COLLATE NOCASE",
                new { platform, username }, tx);

            foreach (var alias in aliases)
                newTombstones += UpsertTombstone(conn, tx, platform, alias, purgedAtUnix) ? 1 : 0;
        }

        // Harf duyarsız: NOCASE (ASCII) + kimlik anahtarı (ASCII dışı harf farkı).
        var newlyScrubbed = conn.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM Customer
              WHERE Platform = @platform
                AND (Username = @username COLLATE NOCASE OR IdentityKey = @key)
                AND PurgedAt IS NULL",
            new { platform, username, key = CustomerIdentity.KeyOrNull(username) }, tx);
        var scrubbed = conn.Execute(
            "UPDATE Customer SET " + ScrubAssignments + @",
                  PurgedAt        = COALESCE(PurgedAt, @purgedAtUnix)
              WHERE Platform = @platform
                AND (Username = @username COLLATE NOCASE OR IdentityKey = @key)",
            new { platform, username, purgedAtUnix, key = CustomerIdentity.KeyOrNull(username) }, tx);

        scope.Commit();
        changed = newTombstones > 0 || newlyScrubbed > 0;
        return scrubbed;
    }

    /// <summary>Mezar taşını yazar (ilk tarih korunur); kimlik için ilk karar mıydı.</summary>
    private static bool UpsertTombstone(System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        string platform, string username, long purgedAtUnix)
    {
        // Birincil anahtarla aynı karşılaştırma: Platform birebir, Username NOCASE (kolon harmanı).
        var existed = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM CustomerPurgeTombstone WHERE Platform = @platform AND Username = @username",
            new { platform, username }, tx) > 0;
        conn.Execute(TombstoneUpsertSql,
            new { platform, username, purgedAtUnix, key = CustomerIdentity.KeyOrNull(username) }, tx);
        return !existed;
    }

    /// <summary>Phase 4g: WhatsApp E.164 telefonu güncelle. Geçersiz id no-op.
    ///
    /// <para>N03 (2026-09-10 denetimi): <c>LastSeenAt</c> de ilerletilir — iş zamanı
    /// ("en son görülme"). Sunucuya gidiş artık ona bağlı DEĞİL (N03-g): gönderim imleci
    /// <c>SyncSeq</c>'tir; telefon değişince göç 036/045 tetikleyicileri SyncSeq'i ilerletir ve
    /// <c>PhoneChangedAt</c>'i damgalar (C6, <c>CustomerSyncRepository.GetForPush</c>).</para>
    ///
    /// <para><b>R12-D02 (2026-09-16 denetimi):</b> <c>AND PurgedAt IS NULL</c>
    /// — elle telefon girişi de silme kapısına tabi. Telefon çekmecesi
    /// açıldıktan SONRA inen bir tombstone'un ardından basılan Kaydet,
    /// boşaltılmış satıra numarayı geri yazıyordu; sonraki boş ingest de onu
    /// temizlemiyordu (kararın kendisi <c>PurgedAt</c> ile satırda duruyor,
    /// tombstone bir daha inmiyor). Kapıyı burada tutmak, "if purged" diye
    /// önden okuyup dallanmaktan üstün: okuma ile yazma arasındaki pencerede
    /// inen bir karar kaçardı — <see cref="ScrubIfTombstonedSql"/> ile aynı
    /// gerekçe.</para></summary>
    /// <returns>Güncellenen satır sayısı; bilinmeyen ya da silinmiş id'de 0.
    /// Çağıran 0'ı "kaydedildi" diye göstermemeli.</returns>
    public int UpdatePhone(string customerId, string e164Phone)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE Customer SET Phone=@phone, LastSeenAt=MAX(LastSeenAt+1, @now)
              WHERE Id=@id AND PurgedAt IS NULL",
            new
            {
                phone = e164Phone,
                id = customerId,
                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }
}
