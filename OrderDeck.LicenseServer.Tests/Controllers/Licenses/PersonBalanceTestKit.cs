using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye (spec 2026-10-11) testlerinin ortak kurulumu — InMemory ve gerçek SQL Server. Lisans ve kimliği
/// doğrulanmış istemci <see cref="SalesCommandTestKit"/>'ten. Üyeler, bakiyeler ve S2 biçimindeki çekim grupları
/// doğrudan veritabanına yazılır (D1 korunur: her bakiye bir defter satırından). Kişisel alan yok: kullanıcı adları
/// ve grup numaraları üretilir.
/// </summary>
public sealed class PersonBalanceTestKit
{
    public const string Session42 = "session:42";
    public const string KindDeduction = "purchase-deduction";
    public const string KindManual = "manual-adjustment";
    public const string KindReversal = "reversal";

    private readonly Dictionary<Guid, (string Platform, string Username)> _identities = new();

    private PersonBalanceTestKit(SalesCommandTestKit sales) => Sales = sales;

    public SalesCommandTestKit Sales { get; }
    public Guid LicenseId => Sales.LicenseId;
    public Guid CustomerId => Sales.CustomerId;
    public HttpClient Client => Sales.Client;
    private string WpfUrl => $"/api/v1/licenses/{LicenseId}/customer-balance";

    public static async Task<PersonBalanceTestKit> CreateAsync(ApiFactory factory, SalesSyncMode mode = SalesSyncMode.Off) =>
        new(await SalesCommandTestKit.CreateAsync(factory, mode));

    public static string NewGroup() => $"g{Guid.NewGuid():N}";

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Kişinin bir üyesi (asıl kayıt). <paramref name="balance"/> ≠ 0 ise bir defter satırı (artıda iade, eksi
    /// "eski" bir elle ayar) ve aynı tutarlı bakiye satırı; 0 ise <paramref name="balanceRow"/> true iken 0'lık satır.
    /// </summary>
    public async Task<Guid> MemberAsync(
        string? groupId, decimal balance = 0m, bool purged = false, bool balanceRow = true,
        string platform = "youtube", string? username = null)
    {
        var id = Guid.NewGuid();
        var name = username ?? $"p{Guid.NewGuid():N}";
        _identities[id] = (platform, name);
        var now = DateTimeOffset.UtcNow;
        await Sales.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = LicenseId, Platform = platform, Username = name, GroupId = groupId,
                UpdatedAt = now, PurgedAt = purged ? now.AddMinutes(-30) : null,
            });
            if (balance != 0m || balanceRow)
                db.CustomerBalances.Add(new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = id,
                    Balance = balance, UpdatedAt = now.AddMinutes(-10),
                });
            if (balance != 0m)
                db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = id, Amount = balance,
                    Kind = balance > 0 ? "refund-full" : KindManual,
                    OriginalAmount = balance > 0 ? balance : null,
                    CreatedByCustomerId = CustomerId, CreatedAt = now.AddMinutes(-10),
                });
            return Task.CompletedTask;
        });
        return id;
    }

    /// <summary>Var olan bir satıra iade (+): defter satırı ve bakiye (satır yoksa açılır). Döner: hareketin Id'si.</summary>
    public async Task<Guid> FundAsync(Guid row, decimal amount, string? reason = null, DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid();
        var createdAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-10);
        await Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = id, LicenseId = LicenseId, WpfCustomerId = row, Amount = amount, Kind = "refund-full",
                OriginalAmount = amount, Reason = reason, CreatedByCustomerId = CustomerId, CreatedAt = createdAt,
            });
            var balance = await db.CustomerBalances.SingleOrDefaultAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
            if (balance is null)
                db.CustomerBalances.Add(new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = row, Balance = amount, UpdatedAt = createdAt,
                });
            else
                balance.Balance += amount;
        });
        return id;
    }

    /// <summary>
    /// S2 biçiminde (elle) bir çekim grubu — spec §3.2. İlk bacak çapadır (Id = DrawGroupId; OriginalAmount,
    /// SaleScope, Reason, ReversesTransactionId yalnız onda); hepsi aynı CreatedAt. Bakiye satırları bacak tutarı
    /// kadar düşer (D1). Döner: bacak Id'leri, çapa ilk.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SeedDrawAsync(
        string kind, IReadOnlyList<(Guid Row, decimal Amount)> legs, decimal? originalAmount = null,
        string? saleScope = null, string? reason = null, Guid? reverses = null, DateTimeOffset? at = null)
    {
        var ids = legs.Select(_ => Guid.NewGuid()).ToList();
        var anchor = ids[0];
        var createdAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        await Sales.WithDbAsync(async db =>
        {
            for (var i = 0; i < legs.Count; i++)
            {
                var (row, amount) = legs[i];
                db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
                {
                    Id = ids[i], LicenseId = LicenseId, WpfCustomerId = row, Amount = -amount, Kind = kind,
                    OriginalAmount = i == 0 ? originalAmount : null,
                    SaleScope = i == 0 ? saleScope : null,
                    Reason = i == 0 ? reason : null,
                    ReversesTransactionId = i == 0 ? reverses : null,
                    DrawGroupId = anchor, CreatedByCustomerId = CustomerId, CreatedAt = createdAt,
                });
                if (amount == 0m) continue;
                var balance = await db.CustomerBalances.SingleAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
                balance.Balance -= amount;
            }
        });
        return ids;
    }

    /// <summary>Eski, tek bacaklı düşüm (DrawGroupId NULL) — bugünkü apply'ın yazdığı biçim.</summary>
    public async Task<Guid> SeedLegacyDeductionAsync(Guid row, decimal amount, decimal productTotal, string? saleScope = null)
    {
        var id = Guid.NewGuid();
        await Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = id, LicenseId = LicenseId, WpfCustomerId = row, Amount = -amount, Kind = KindDeduction,
                OriginalAmount = productTotal, SaleScope = saleScope,
                CreatedByCustomerId = CustomerId, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            var balance = await db.CustomerBalances.SingleAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
            balance.Balance -= amount;
        });
        return id;
    }

    /// <summary>Spec Örnek 1'in sonu.</summary>
    public sealed record Example1(Guid T, Guid I, Guid Y, Guid K, Guid L1, Guid L2, string Group);

    /// <summary>
    /// Spec Örnek 1'in SONUCU (S2'nin yazacağı satırlar, elle): kişi {T (sahip, 0), I (Instagram, 100), Y (30)};
    /// çekim K (T, 0,00, OriginalAmount 120, session:42), L1 (I, −100), L2 (Y, −20) → T 0, I 0, Y 10.
    /// </summary>
    public async Task<Example1> Example1Async(string? group = null, string? iUsername = null)
    {
        var g = group ?? NewGroup();
        var t = await MemberAsync(g);
        var i = await MemberAsync(g, 100m, platform: "instagram", username: iUsername);
        var y = await MemberAsync(g, 30m);
        var ids = await SeedDrawAsync(KindDeduction, [(t, 0m), (i, 100m), (y, 20m)], originalAmount: 120m, saleScope: Session42);
        return new Example1(t, i, y, ids[0], ids[1], ids[2], g);
    }

    // ── Okuma ─────────────────────────────────────────────────────────────────

    public Task<decimal> BalanceAsync(Guid row) =>
        Sales.ReadDbAsync(async db => await db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == LicenseId && b.WpfCustomerId == row)
            .Select(b => (decimal?)b.Balance).SingleOrDefaultAsync() ?? 0m);

    public Task<CustomerBalance?> BalanceRowAsync(Guid row) =>
        Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .SingleOrDefaultAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row));

    public Task<decimal> LedgerSumAsync(Guid row) =>
        Sales.ReadDbAsync(async db => (await db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId && t.WpfCustomerId == row)
            .Select(t => t.Amount).ToListAsync()).Sum());

    /// <summary>D1: satırın bakiyesi defter toplamına eşit mi.</summary>
    public async Task<bool> LedgerHoldsAsync(Guid row) => await BalanceAsync(row) == await LedgerSumAsync(row);

    /// <summary>Verilen hareketleri geri alan satırlar.</summary>
    public Task<List<CustomerBalanceTransaction>> ReversalsOfAsync(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        return Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.ReversesTransactionId != null && list.Contains(t.ReversesTransactionId.Value))
            .ToListAsync());
    }

    public Task<WpfCustomerProjection> ProjectionAsync(Guid id) =>
        Sales.ReadDbAsync(db => db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id));

    // ── İstek ─────────────────────────────────────────────────────────────────

    public Task<HttpResponseMessage> PreviewAsync(Guid row) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/preview?wpfCustomerId={row}");

    public Task<HttpResponseMessage> ScopeAsync(Guid row, string saleScope) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/scope?wpfCustomerId={row}&saleScope={Uri.EscapeDataString(saleScope)}");

    public Task<HttpResponseMessage> TransactionAsync(Guid id) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/transactions/{id}");

    public Task<HttpResponseMessage> ReverseAsync(Guid id) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/transactions/{id}/reverse");

    public Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/apply",
            new { wpfCustomerId = row, amount, productTotal, idempotencyKey = key, saleScope });

    public Task<HttpResponseMessage> PanelGetAsync(Guid row, int take = 50) =>
        SendAsync(HttpMethod.Get, $"/api/panel/customers/{row}/balance?take={take}", userAgent: null);

    public Task<HttpResponseMessage> PanelReverseAsync(Guid row, Guid transactionId) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/transactions/{transactionId}/reverse", userAgent: null);

    public Task<HttpResponseMessage> PanelRefundAsync(Guid row, decimal amount) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/refund-full",
            new { amount, reason = (string?)null }, userAgent: null);

    /// <summary>WPF senkronu (Format 2) ile üyenin grup numarasını damgalı yazar.</summary>
    public Task<HttpResponseMessage> SyncGroupAsync(Guid member, string? groupId, DateTimeOffset? stamp = null)
    {
        var (platform, username) = _identities[member];
        var at = stamp ?? DateTimeOffset.UtcNow;
        return SendAsync(HttpMethod.Post, $"/api/v1/licenses/{LicenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = member, platform, username, updatedAt = at, format = 2, groupId, groupIdChangedAt = at } } });
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? userAgent = SalesCommandTestKit.WpfUserAgent)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return await Client.SendAsync(request);
    }

    public static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }
}
