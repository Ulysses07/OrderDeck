using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class CustomerRepository
{
    private readonly IDbConnectionFactory _factory;
    public CustomerRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Customer c)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Customer
              (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               IsBlacklisted, BlacklistReason, Notes,
               TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone,
               RecipientPaysActive, GroupId, Email, Tckn, WhatsAppConsent, SmsConsent)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt, @Address, @Phone,
               @RecipientPaysActive, @GroupId, @Email, @Tckn, @WhatsAppConsent, @SmsConsent)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes,
                c.TotalLabelsPrinted, c.TotalAmount, c.BlacklistedAt, c.Address, c.Phone,
                RecipientPaysActive = c.RecipientPaysActive ? 1 : 0,
                c.GroupId, c.Email, c.Tckn,
                WhatsAppConsent = c.WhatsAppConsent ? 1 : 0,
                SmsConsent = c.SmsConsent ? 1 : 0
            });
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

    public Customer? FindByPlatformAndUsername(string platform, string username)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Platform=@platform AND Username=@username",
            new { platform, username });
        return row is null ? null : Map(row);
    }

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

    public void IncrementLabelStats(string id, int labelDelta, decimal amountDelta, long lastSeenAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
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

    /// <summary>Assigns a customer to a group (YouTube channelId adoption).</summary>
    public void SetGroupId(string customerId, string groupId)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Customer SET GroupId = @groupId WHERE Id = @id",
            new { groupId, id = customerId });
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

    /// <summary>All customers, ordered by LastSeenAt DESC. Used by the customer
    /// dialog to show a default list when the search box is empty — otherwise the
    /// operator has no way to discover newly-registered shoppers (which don't yet
    /// have orders). WPF ListBox virtualizes by default, so a few thousand rows
    /// remain responsive.</summary>
    public IReadOnlyList<Customer> GetAll()
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Customer
              ORDER BY LastSeenAt DESC").ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Case-insensitive substring search on Username, ordered by LastSeenAt DESC.</summary>
    public IReadOnlyList<Customer> Search(string usernameContains, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(usernameContains))
            return System.Array.Empty<Customer>();

        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Customer
              WHERE LOWER(Username) LIKE LOWER(@q)
              ORDER BY LastSeenAt DESC
              LIMIT @limit",
            new { q = "%" + usernameContains + "%", limit }).ToList();
        return rows.Select(Map).ToList();
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
        SmsConsent: r.SmsConsent == 1);

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
    }

    /// <summary>
    /// Upsert by (Platform, Username). Phase 4f intake form sync için.
    /// Mevcut müşteri varsa DisplayName, Address, LastSeenAt güncellenir;
    /// yoksa yeni satır insert edilir.
    /// </summary>
    public Customer UpsertFromIntakeForm(string username, string fullName, string address, string? phone, long nowUnix)
    {
        const string platform = "form";
        using var conn = _factory.Open();

        var existing = conn.QueryFirstOrDefault<Row>(@"
            SELECT Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                   IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                   BlacklistedAt, Address, Phone
            FROM Customer
            WHERE Platform = @platform AND Username = @username",
            new { platform, username });

        if (existing is not null)
        {
            conn.Execute(@"
                UPDATE Customer
                SET DisplayName = @fullName,
                    Address = @address,
                    Phone = @phone,
                    LastSeenAt = @nowUnix
                WHERE Id = @id",
                new { fullName, address, phone, nowUnix, id = existing.Id });
            var updated = Map(existing);
            return updated with { DisplayName = fullName, Address = address, Phone = phone, LastSeenAt = nowUnix };
        }

        var id = Guid.NewGuid().ToString("N");
        conn.Execute(@"
            INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                                  IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                                  BlacklistedAt, Address, Phone)
            VALUES (@id, @platform, @username, @fullName, NULL, @nowUnix, @nowUnix,
                    0, NULL, NULL, 0, 0, NULL, @address, @phone)",
            new { id, platform, username, fullName, nowUnix, address, phone });

        return new Customer(id, platform, username, fullName, null, nowUnix, nowUnix,
            false, null, null, 0, 0m, null, address, phone);
    }

    /// <summary>
    /// Intake form çoklu-platform upsert. Kişinin bildirdiği her platform kimliği
    /// için bir Customer satırı oluşturur/günceller ve hepsini tek bir
    /// <c>GroupId</c> ile bağlar. Kimliklerden biri zaten bir gruba aitse o grup
    /// yeniden kullanılır (kimlikler tek kişide birleşir), yoksa yeni grup üretilir.
    /// Handle normalize: trim + baştaki '@' atılır. IG/TikTok/FB'de Username = chat
    /// handle olduğundan chat satırıyla doğal birleşir; YouTube form satırı @handle
    /// ile durur (channelId adopsiyonu Faz 3'te). İletişim/izin bilgisi tüm satırlara
    /// yazılır; DisplayName boşsa Ad Soyad'a set edilir. Grup id'yi döner.
    /// </summary>
    public string UpsertPersonFromIntake(
        IReadOnlyList<(string Platform, string Username, string? PreferredDisplayName)> identities,
        string fullName, string address, string? phone,
        string? email, string? tckn, bool whatsAppConsent, bool smsConsent,
        long nowUnix)
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

        using var conn = _factory.Open();

        // Grup id çözümle: kimliklerden biri zaten gruplanmışsa onu kullan.
        string? groupId = null;
        foreach (var (p, u, _) in norm)
        {
            var existing = FindExistingForIntake(conn, p, u);
            if (existing?.GroupId is { Length: > 0 } g) { groupId = g; break; }
        }
        groupId ??= Guid.NewGuid().ToString("N");

        foreach (var (p, u, disp) in norm)
        {
            // Gösterilecek ad: YouTube'da channelId Username olduğunda @handle (disp);
            // diğerlerinde Ad Soyad. UI asla channelId göstermesin diye önemli.
            var displayForRow = disp ?? fullName;
            var existing = FindExistingForIntake(conn, p, u);
            if (existing is not null)
            {
                conn.Execute(
                    @"UPDATE Customer SET
                        GroupId = @groupId,
                        Address = @address, Phone = @phone, Email = @email, Tckn = @tckn,
                        WhatsAppConsent = @wa, SmsConsent = @sms, LastSeenAt = @now,
                        DisplayName = COALESCE(NULLIF(DisplayName, ''), @displayForRow)
                      WHERE Id = @id",
                    new
                    {
                        groupId, address, phone, email, tckn,
                        wa = whatsAppConsent ? 1 : 0, sms = smsConsent ? 1 : 0,
                        now = nowUnix, displayForRow, id = existing.Id
                    });
            }
            else
            {
                conn.Execute(
                    @"INSERT INTO Customer
                      (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                       IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                       BlacklistedAt, Address, Phone, RecipientPaysActive,
                       GroupId, Email, Tckn, WhatsAppConsent, SmsConsent)
                      VALUES
                      (@id, @p, @u, @displayForRow, NULL, @now, @now,
                       0, NULL, NULL, 0, 0,
                       NULL, @address, @phone, 0,
                       @groupId, @email, @tckn, @wa, @sms)",
                    new
                    {
                        id = Guid.NewGuid().ToString("N"), p, u, displayForRow, now = nowUnix,
                        address, phone, groupId, email, tckn,
                        wa = whatsAppConsent ? 1 : 0, sms = smsConsent ? 1 : 0
                    });
            }
        }

        return groupId;
    }

    /// <summary>
    /// Intake form kimliğini mevcut bir Customer satırıyla eşleştirir; böylece
    /// alışveriş geçmişi olan (numarasız otomatik kaydedilmiş) müşteri, formu
    /// doldurduğunda AYRI satır açmaz, mevcut satırı güncelleriz.
    /// <list type="number">
    ///   <item>Önce <c>(Platform, Username)</c> birebir — büyük/küçük harf
    ///   duyarsız (COLLATE NOCASE) → IG/TikTok/FB'de handle chat'le birebir aynı.</item>
    ///   <item>YouTube özel: chat satırının Username'i channelId (UCxxx), form ise
    ///   @handle verir → doğrudan tutmaz. Bu satırların <c>DisplayName</c>'i @handle
    ///   tuttuğu için handle'ı DisplayName ile eşleştirip channelId satırını buluruz
    ///   (Username=channelId korunur, gelecekteki chat de eşleşmeye devam eder).</item>
    /// </list>
    /// Eşleşme yoksa null döner → çağıran yeni satır açar (regresyon yok).
    /// </summary>
    private static Row? FindExistingForIntake(System.Data.IDbConnection conn, string platform, string handle)
    {
        // 1) (Platform, Username) birebir — harf duyarsız.
        var exact = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Platform=@platform AND Username=@handle COLLATE NOCASE",
            new { platform, handle });
        if (exact is not null) return exact;

        // 2) YouTube: chat satırı channelId ile; @handle DisplayName'de saklı.
        if (string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return conn.QueryFirstOrDefault<Row>(
                @"SELECT * FROM Customer
                  WHERE Platform='youtube'
                    AND LTRIM(DisplayName, '@') = @handle COLLATE NOCASE
                  LIMIT 1",
                new { handle });
        }

        return null;
    }

    /// <summary>Phase 4g: WhatsApp E.164 telefonu güncelle. Geçersiz id no-op.</summary>
    public void UpdatePhone(string customerId, string e164Phone)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Customer SET Phone=@phone WHERE Id=@id",
            new { phone = e164Phone, id = customerId });
    }

    /// <summary>
    /// Faz 0c-2: WpfCustomerProjection sync için delta query. LastSeenAt > since
    /// olan customer kayıtlarını döner. <paramref name="max"/> ile batch size sınırı.
    /// Sonuçlar LastSeenAt ASC sıralı (watermark ilerlemesi deterministic olsun).
    /// </summary>
    public IReadOnlyList<Customer> GetUpdatedSince(long sinceUnixSeconds, int max)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                     IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                     BlacklistedAt, Address, Phone, RecipientPaysActive
              FROM Customer
              WHERE LastSeenAt > @since
              ORDER BY LastSeenAt ASC
              LIMIT @max",
            new { since = sinceUnixSeconds, max })
            .ToList();
        return rows.Select(Map).ToList();
    }
}
