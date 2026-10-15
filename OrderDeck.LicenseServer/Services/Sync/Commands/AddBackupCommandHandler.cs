using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>add-backup</c> komutu — spec 2026-10-13 §4.6 tablosu, §2 "Oluşum alanları", DM3, DM4. Yük
/// <c>{newOrderId, parentOrderId, customer {id, platform, username, displayName}, messageText, price?, code?}</c>; yerel
/// ayna <c>LabelService.AddBackup</c> (ebeveynin fiyatı ve kodu varsayılan, ürünsüz <c>Add</c>).
/// <list type="bullet">
///   <item><b>Ebeveyn</b> bu lisansta olmalı (400 <c>unknown-order</c>) ve devredilmiş bir yayında olmalı (409
///   <c>session-live</c>): canlı ya da devrediliyor yayına yalnız sahibi ekler (Y1, Y8).</item>
///   <item><b>Yeni Id</b> sunucuda hiçbir lisansta olmamalı; varsa 409 <c>content-conflict</c> — Orders'ın birincil
///   anahtarı yalnız Id, ön denetimden sonraki eşzamanlı çakışmayı da servis aynı rete çevirir (500 olmaz). Aynı
///   komutun yeniden gönderimi buraya gelmez, kayıtlı sonucu alır.</item>
///   <item><b>Ekleme</b> (oluşum alanları yalnız burada, DM4): ebeveynin yayını; müşteri asıl kayda çözülür
///   (<see cref="CustomerIdResolver.CanonicalHexOfAsync"/>; GUID olmayan Id ham kalır); <c>IsTentativeBackup</c> ve
///   <c>IsBackupPromoted</c> = 1, <c>ParentOrderId</c>; <c>OriginReplicaId</c> = çağıran (kargo bağı bilinen sipariş,
///   M22); <c>SessionReleasedAt</c> yayının devir anı — sipariş akışa hemen girer (DM3); <c>AddedAt</c> sunucu saati;
///   fiyat ve kod verilmediyse ebeveyninki. <c>ProductId</c> ve varyant BOŞ: yerel <c>AddBackup</c> de ürünsüz ekler,
///   iki yol stok defterinde ayrışmasın (§12 "bilerek yapılmayanlar"). Bu yüzden defter satırı yok; birim damgası da
///   yok (<c>BackupSetAt</c>'i onay basar).</item>
///   <item><b>KVKK</b> (§9): <c>SalesSync:ScrubOrderTextOnPurge</c> açık ve asıl projeksiyon silinmişse görünen ad ve
///   yorum boş yazılır, <c>TextScrubbedAt</c> basılır (<see cref="OrderTextScrub"/>; <c>orders/sync</c>'in ekleme
///   kuralıyla aynı).</item>
/// </list>
/// Bildirim yok (push, WhatsApp etiket kuralı, shopper): geçici yedek satış değildir. Sonuç (<c>ResultJson</c>) yalnız
/// Id ve sürüm taşır; ad ve yorum yazılmaz. <c>expected</c> boş olmalı: komut var olan satırı yazmaz.
/// </summary>
public sealed class AddBackupCommandHandler : ISalesCommandHandler
{
    public const string KindName = "add-backup";

    // Orders kolon sınırları (LicenseDbContext): aşan alan 500'e düşmeden 400 alır.
    public const int MaxCustomerIdLength = 64;
    public const int MaxPlatformLength = 32;
    public const int MaxUsernameLength = 200;
    public const int MaxDisplayNameLength = 200;
    public const int MaxMessageTextLength = 2000;
    public const int MaxCodeLength = 64;

    private readonly CustomerIdResolver _customerIds;
    private readonly SalesSyncOptions _options;

    public AddBackupCommandHandler(CustomerIdResolver customerIds, IOptions<SalesSyncOptions> options)
    {
        _customerIds = customerIds;
        _options = options.Value;
    }

    public string Kind => KindName;

    /// <summary>Komutun müşterisi; Id ham (asıl kayda uygulamada çözülür).</summary>
    public sealed record Customer(string Id, string Platform, string Username, string? DisplayName);

    /// <summary>Kanonik yük: yorum boşsa <c>""</c>, fiyat iki ondalıklı; kod verildiği gibi (saklanan kod kırpılmaz).</summary>
    public sealed record Payload(
        Guid NewOrderId,
        Guid ParentOrderId,
        Customer Customer,
        string MessageText,
        decimal? Price,
        string? Code);

    private sealed record CustomerBody(string? Id, string? Platform, string? Username, string? DisplayName);

    private sealed record Body(
        Guid? NewOrderId,
        Guid? ParentOrderId,
        CustomerBody? Customer,
        string? MessageText,
        decimal? Price,
        string? Code);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.NewOrderId is not { } newOrderId || newOrderId == Guid.Empty
            || body.ParentOrderId is not { } parentOrderId || parentOrderId == Guid.Empty)
        {
            error = "newOrderId ve parentOrderId boş olamaz.";
            return false;
        }
        if (newOrderId == parentOrderId)
        {
            error = "newOrderId ebeveynin Id'si olamaz.";
            return false;
        }
        if (body.Customer is not { } customer
            || !Required(customer.Id, MaxCustomerIdLength)
            || !Required(customer.Platform, MaxPlatformLength)
            || !Required(customer.Username, MaxUsernameLength)
            || customer.DisplayName is { Length: > MaxDisplayNameLength })
        {
            error = $"customer id (en çok {MaxCustomerIdLength}), platform (en çok {MaxPlatformLength}) ve username "
                    + $"(en çok {MaxUsernameLength}) taşımalı; displayName en çok {MaxDisplayNameLength} karakter.";
            return false;
        }
        if (body.MessageText is { Length: > MaxMessageTextLength })
        {
            error = $"messageText en çok {MaxMessageTextLength} karakter.";
            return false;
        }
        if (body.Code is { Length: > MaxCodeLength })
        {
            error = $"code en çok {MaxCodeLength} karakter.";
            return false;
        }

        decimal? price = null;
        if (body.Price is { } raw)
        {
            if (!SalesCommandPayload.TryReadPrice(raw, "price", out var parsed, out error)) return false;
            price = parsed;
        }
        if (expected.Count > 0)
        {
            error = "add-backup var olan bir satırı yazmaz; expected boş olmalı.";
            return false;
        }

        command = new Command(
            new Payload(
                newOrderId,
                parentOrderId,
                new Customer(customer.Id!, customer.Platform!, customer.Username!, customer.DisplayName),
                body.MessageText ?? "",
                price,
                body.Code),
            _customerIds,
            _options.ScrubOrderTextOnPurge);
        return true;
    }

    private static bool Required(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private sealed class Command(Payload payload, CustomerIdResolver customerIds, bool scrubOrderTextOnPurge)
        : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds { get; } = [payload.NewOrderId];

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var parent = (await scope.LoadOrdersAsync([payload.ParentOrderId], ct))[payload.ParentOrderId];
            if (parent.SessionId is not { } sessionId)
                throw SalesCommandRejection.SessionLive(null, null, null);
            var releasedAt = await scope.EnsureSessionReleasedAsync(sessionId, ct);
            if (await scope.OrderIdTakenAsync(payload.NewOrderId, ct))
                throw SalesCommandRejection.ContentConflict(payload.NewOrderId);

            var rawCustomerId = payload.Customer.Id;
            var customerId = (await customerIds.CanonicalHexOfAsync(scope.LicenseId, [rawCustomerId], ct))[rawCustomerId];
            var backup = new Order
            {
                Id = payload.NewOrderId,
                LicenseId = scope.LicenseId,
                SessionId = sessionId,
                CustomerId = customerId,
                Platform = payload.Customer.Platform,
                Username = payload.Customer.Username,
                DisplayName = payload.Customer.DisplayName,
                MessageText = payload.MessageText,
                Code = payload.Code ?? parent.Code,
                Price = payload.Price ?? parent.Price,
                AddedAt = scope.Now,
                IsBackupPromoted = true,
                IsTentativeBackup = true,
                ParentOrderId = parent.Id,
                OriginReplicaId = scope.ReplicaId,
                SessionReleasedAt = releasedAt,
            };

            // KVKK: silmeden sonra eklenen yedek metni geri getirmez (ayar kapalıyken okunmaz).
            if (scrubOrderTextOnPurge)
            {
                var customers = await scope.LoadCustomersAsync([rawCustomerId], ct);
                if (OrderTextScrub.AppliesOnInsert(
                        scrubOrderTextOnPurge, customers.Resolve(rawCustomerId).Canonical?.PurgedAt))
                    OrderTextScrub.Apply(backup, scope.Now);
            }

            scope.AddOrder(backup);
            return new SalesCommandOutcome(OrderIds, []);
        }
    }
}
