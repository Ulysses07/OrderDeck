using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>
/// <c>payment-requests</c> uçlarının ortak sınırları, kapsam ve tutar kuralları ve telde kullanılan hata kodları (spec
/// 2026-10-13 §4.7, §6.4). <c>begin</c>, <c>finish</c>, liste ve <c>backfill</c> aynı doğrulamayı kullanır: bir ucun kabul
/// ettiği üye, sipariş ya da tutar öbüründe de aynı anlamı taşır.
/// </summary>
public static class PaymentRequestRules
{
    /// <summary>Bir isteğin en çok üye sayısı.</summary>
    public const int MaxMembers = 100;

    /// <summary>Bir isteğin en çok sipariş sayısı; küme eşitliği isteyen komutların tavanıyla aynı (§4.6).</summary>
    public const int MaxOrders = SalesCommandService.MaxSetOrders;

    /// <summary>decimal(18,2)'ye sığan en büyük tutar.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    /// <summary>Bir geri doldurma partisinin en çok kayıt sayısı (risk 9: 100 istek/dk/IP bütçesi).</summary>
    public const int MaxBackfillBatch = 200;

    /// <summary>Listenin en çok kayıt sayısı: en yeni 200.</summary>
    public const int MaxListRecords = 200;

    public const string CumulativeScope = "cumulative";
    public const string SessionScopePrefix = "session:";
    public const string LegacyScope = "legacy";
    public const string LegacyScopePrefix = "legacy:";

    public const string CodeInvalidEnvelope = "invalid-envelope";
    public const string CodeInvalidBatch = "invalid-batch";
    public const string CodeInvalidRecord = "invalid-record";
    public const string CodeInvalidOutcome = "invalid-outcome";
    public const string CodeInvalidState = "invalid-state";
    public const string CodeInvalidAt = "invalid-at";
    public const string CodeRecordNotFound = "record-not-found";
    public const string CodeInvalidMembers = "invalid-members";
    public const string CodeInvalidScope = "invalid-scope";
    public const string CodeInvalidOrders = "invalid-orders";
    public const string CodeInvalidAmount = "invalid-amount";
    public const string CodeInvalidShipping = "invalid-shipping";
    public const string CodeInvalidConfirmedPriors = "invalid-confirmed-priors";
    public const string CodeModeOff = "mode-off";
    public const string CodeClientUpdateRequired = "client-update-required";
    public const string CodeContentConflict = "content-conflict";
    public const string CodeMembersStale = "members-stale";
    public const string CodeInProgressElsewhere = "in-progress-elsewhere";
    public const string CodeAlreadyRequested = "already-requested";
    public const string CodeStaleTotal = "stale-total";
    public const string CodeSettingsStale = "settings-stale";

    /// <summary>Kira kapsamı (§6.3): <c>cumulative</c> ya da <c>session:</c> + yayının "N" hex Id'si (WPF'in
    /// yazımı).</summary>
    public static bool IsLeaseScope(string? scopeKey) =>
        scopeKey is not null
        && scopeKey.Length <= PaymentRequestRecord.ScopeKeyMaxLength
        && (string.Equals(scopeKey, CumulativeScope, StringComparison.Ordinal)
            || (scopeKey.StartsWith(SessionScopePrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(scopeKey.AsSpan(SessionScopePrefix.Length), "N", out _)));

    /// <summary>
    /// Kayıt kapsamı (geri doldurma ve liste): kira kapsamı (<see cref="IsLeaseScope"/>) ya da yerel <c>PaymentJob</c>'un
    /// miras kapsamı — <c>legacy</c> ya da <c>legacy:{anahtar}</c> (<c>PaymentJob.IsLegacyScope</c>; anahtar boş olamaz).
    /// Ordinal, en çok <see cref="PaymentRequestRecord.ScopeKeyMaxLength"/> karakter.
    /// </summary>
    public static bool IsRecordScope(string? scopeKey) =>
        IsLeaseScope(scopeKey)
        || (scopeKey is not null
            && scopeKey.Length <= PaymentRequestRecord.ScopeKeyMaxLength
            && (string.Equals(scopeKey, LegacyScope, StringComparison.Ordinal)
                || (scopeKey.StartsWith(LegacyScopePrefix, StringComparison.Ordinal)
                    && scopeKey.Length > LegacyScopePrefix.Length)));

    /// <summary><c>session:{"N" hex}</c> kapsamının yayını; başka kapsam için null. Kapsam doğrulanmış olmalı.</summary>
    public static Guid? SessionOf(string scopeKey) =>
        scopeKey.StartsWith(SessionScopePrefix, StringComparison.Ordinal)
            ? Guid.ParseExact(scopeKey.AsSpan(SessionScopePrefix.Length), "N")
            : null;

    /// <summary>Üye Id'si: boş ya da yalnız boşluk değil, en çok
    /// <see cref="PaymentRequestRecord.WpfCustomerIdMaxLength"/> karakter. GUID'li ("N" hex) ya da GUID'siz ham yerel
    /// Id olabilir.</summary>
    public static bool IsMemberId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= PaymentRequestRecord.WpfCustomerIdMaxLength;

    /// <summary>Tutar: 0 ya da daha büyük, en çok iki ondalıklı, decimal(18,2)'ye sığar.</summary>
    public static bool IsMoney(decimal? value) =>
        value is { } v && v >= 0 && v <= MaxAmount && decimal.Round(v, 2) == v;
}
