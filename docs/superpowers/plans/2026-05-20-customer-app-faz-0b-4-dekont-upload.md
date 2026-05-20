# Müşteri (Shopper) App — Faz 0b-4: Dekont Upload + Devices Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.

**Goal:** Shopper PDF dekont upload endpoint (6 fraud katmanı dahil) + push device register/unregister. Server-side parse, R2'ya direct upload, yayıncıya push notification fan-out.

**Architecture:** Yeni `IShopperPaymentStorage` (direct-upload R2, mevcut broadcast media pre-signed URL pattern'inden farklı). `ParserConfidenceCalculator` static utility. `ShopperPaymentSubmissionService` orchestrator (parse + dedupe + IBAN check + fraud flags + R2 upload + DB persist + push). Rate limit DB-based (`PaymentSubmissionAudit` son 1 saat count'u). Push fan-out mevcut `INotificationSender.SendToCustomerAsync` ile.

**Tech Stack:** ASP.NET Core multipart, AWSSDK.S3 (R2), PdfPig (parser zaten var), Konscious.Argon2id (mevcut), EF Core InMemory (test).

---

## Task 1: `IShopperPaymentStorage` interface + Stub + R2

**Files:**
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/IShopperPaymentStorage.cs`
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/StubShopperPaymentStorage.cs`
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/R2ShopperPaymentStorage.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/ShopperPayments/StubShopperPaymentStorageTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI register)

**Interface:**
```csharp
public interface IShopperPaymentStorage
{
    Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}
```

**Stub (in-memory, for tests):**
- ConcurrentDictionary<string, (byte[] bytes, string contentType)> for storage
- UploadAsync: insert and return objectKey unchanged
- CreateDownloadUrlAsync: return `stub://payments/{key}`
- DeleteAsync: remove from dict

**R2 implementation:**
- Reuse `R2Options` from `OrderDeck.LicenseServer/Services/BroadcastPosts/R2Options.cs`
- Different bucket prefix: `payments/` (vs broadcast's `posts/`)
- Same `AWSConfigsS3.UseSignatureVersion4 = true` static flag pattern as `R2BroadcastMediaStorage`
- `PutObjectAsync` for upload, `GeneratePreSignedURL` for download URL

**Tests:**
- Stub: roundtrip upload + download URL + delete
- R2 implementation NOT unit-tested here (integration testing in staging)

**DI register in Program.cs:**
```csharp
// After IBroadcastMediaStorage registration:
if (storageProvider == "R2")
    builder.Services.AddSingleton<IShopperPaymentStorage, R2ShopperPaymentStorage>();
else
    builder.Services.AddSingleton<IShopperPaymentStorage, StubShopperPaymentStorage>();
```

(Or reuse existing R2 provider selection — see existing `IBroadcastMediaStorage` registration pattern in Program.cs.)

**Commit:** `feat(shopper-payments): IShopperPaymentStorage interface + Stub + R2 impl`

---

## Task 2: `ParserConfidenceCalculator` utility

**Files:**
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/ParserConfidenceCalculator.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/ShopperPayments/ParserConfidenceCalculatorTests.cs`

**Static method:**
```csharp
public static string Compute(PdfDekontParser.ParseResult result)
{
    int score = 0;
    if (!string.IsNullOrWhiteSpace(result.PayerName)) score++;
    if (result.Amount.HasValue) score++;
    if (result.PaidAt.HasValue) score++;
    if (!string.IsNullOrWhiteSpace(result.ReferansNo)) score++;
    if (!string.IsNullOrWhiteSpace(result.RecipientIban)) score++;

    // 5 alan: 4-5 = High, 2-3 = Medium, 0-1 = Low
    return score >= 4 ? "High" : score >= 2 ? "Medium" : "Low";
}
```

**Tests:**
- All 5 fields populated → High
- 4 fields → High
- 3 fields → Medium
- 2 fields → Medium
- 1 field → Low
- 0 fields → Low

**Commit:** `feat(shopper-payments): ParserConfidenceCalculator`

---

## Task 3: `ShopperPaymentRateLimiter` service

**Files:**
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/ShopperPaymentRateLimiter.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/ShopperPayments/ShopperPaymentRateLimiterTests.cs`

**Constants:**
```csharp
private const int ShopperHourlyLimit = 5;
private const int LicenseHourlyLimit = 150;
```

**Interface (just one method):**
```csharp
public interface IShopperPaymentRateLimiter
{
    /// <returns>null if allowed; reason string if blocked (e.g. "shopper-hourly-limit", "license-hourly-limit")</returns>
    Task<string?> CheckAsync(Guid shopperId, Guid licenseId, CancellationToken ct);
}
```

**Implementation:**
- Query `PaymentSubmissionAudit` rows in last 1 hour
- Count by ShopperId → if ≥ 5 → return "shopper-hourly-limit"
- Then query by Payment.LicenseId (join through Payment) → if ≥ 150 → return "license-hourly-limit"
- Else null

Actually simpler: `PaymentSubmissionAudit` doesn't have LicenseId column. Need to join with Payment.LicenseId. Or — add LicenseId to PaymentSubmissionAudit (small migration). Let's go with second: simpler queries.

**Plan adjustment:** Add `LicenseId` to PaymentSubmissionAudit entity + migration. Do this in Task 3 setup.

**Tests:**
- 4 audits for shopper in last hour → allowed
- 5 audits for shopper → blocked (shopper-hourly-limit)
- 6 audits for shopper but >1h old → allowed (window check)
- 150 audits for license different shoppers → 151st blocked
- 0 audits → allowed

**Commit:** `feat(shopper-payments): ShopperPaymentRateLimiter + PaymentSubmissionAudit.LicenseId`

---

## Task 4: `ShopperPaymentSubmissionService` orchestrator

**Files:**
- Create: `OrderDeck.LicenseServer/Services/ShopperPayments/ShopperPaymentSubmissionService.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/ShopperPayments/ShopperPaymentSubmissionServiceTests.cs`

**Interface (sealed class, DI scoped):**
```csharp
public sealed record SubmitInput(
    Guid ShopperId, Guid LicenseId, byte[] PdfBytes,
    decimal? OverrideAmount, string? OverridePayerName,
    DateTimeOffset? OverridePaidAt, string? OverrideReferansNo,
    string IpAddress, string UserAgent);

public sealed record SubmitResult(
    Guid PaymentId, string[] FraudFlags, string ParserConfidence,
    PdfDekontParser.ParseResult ParserResult);

public sealed class SubmitFailure : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }
    public SubmitFailure(int statusCode, string errorCode, string? detail = null)
        : base(detail ?? errorCode) { StatusCode = statusCode; ErrorCode = errorCode; }
}
```

**Workflow:**
1. Magic byte check: PdfBytes[0..5] == "%PDF-" → else throw SubmitFailure(400, "invalid-pdf")
2. Rate limit: rateLimiter.CheckAsync(shopperId, licenseId) → if not null, throw 429
3. Try parse: PdfDekontParser.Parse(bytes) → catch any exception, throw 400 "invalid-pdf"
4. Compute confidence via ParserConfidenceCalculator
5. PdfHash duplicate check: query Payment.PdfHash == result.PdfHash
   - Hit + same shopperId + same licenseId → throw 409 "duplicate-dekont"
   - Hit + different tenant → throw 409 "cross-tenant-duplicate"
6. MetadataHash compute: SHA256(`{amount}|{payerName}|{paidAt}|{referansNo}|{recipientIban}`) where each null → empty
7. MetadataHash duplicate check (same tenant only): if found → fraud flag "metadata-duplicate" (soft)
8. Effective fields (client override OR parser):
   - amount = input.OverrideAmount ?? result.Amount
   - payerName = input.OverridePayerName ?? result.PayerName
   - paidAt = input.OverridePaidAt ?? result.PaidAt
   - referansNo = input.OverrideReferansNo ?? result.ReferansNo
9. TC > 9990 check:
   - if amount > 9990 → load Shopper, if Tc is null → throw 400 "tc-required"
10. IBAN match: load License.PaymentIban
   - License.PaymentIban null → fraud flag "no-iban-baseline"
   - normalize both IBANs (no spaces, uppercase), compare → mismatch → fraud flag "iban-mismatch"
11. Confidence flag: if "Low" → fraud flag "low-confidence"
12. R2 upload: objectKey = $"payments/{licenseId:N}/{Guid.NewGuid():N}.pdf"; storage.UploadAsync
13. DB insert (transaction not strictly needed since InMemory provider is simple; SQL prod can do plain):
   - Payment row: ShopperId, LicenseId, PayerName, Amount, PaidAt, ReferansNo, Status=Pending, ShipmentDirective=Normal, MediaObjectKey, MediaContentType="application/pdf", PdfHash, MetadataHash, RecipientIban, RecipientName, FraudFlags=join(","), ParserConfidence, CreatedAt, UpdatedAt
   - PaymentSubmissionAudit row: PaymentId, ShopperId, LicenseId, IpAddress, UserAgent, FraudFlags, ParserConfidence, ParserRawText, CreatedAt
14. Push notification to broadcaster: `INotificationSender.SendToCustomerAsync(broadcasterCustomerId, "Yeni dekont", $"{payerName}, {amount}₺", new { type="payment", paymentId, hasFraudFlags })`
   - If push fails, log warning but don't throw
15. Return SubmitResult

**Tests (15+):**
1. Happy path → 201 metadata, no flags, push sent
2. Invalid PDF magic → throws SubmitFailure 400 invalid-pdf
3. Parser throws → throws 400 invalid-pdf
4. Rate limit shopper exceeded → throws 429
5. Rate limit license exceeded → throws 429
6. PdfHash same tenant → throws 409 duplicate-dekont
7. PdfHash different tenant → throws 409 cross-tenant-duplicate
8. MetadataHash duplicate → soft flag metadata-duplicate (no throw)
9. IBAN match → no flag
10. IBAN mismatch → soft flag iban-mismatch
11. License.PaymentIban null → soft flag no-iban-baseline
12. ParserConfidence Low → soft flag low-confidence
13. Amount > 9990 + no TC → throws 400 tc-required
14. Amount > 9990 + has TC → success
15. Client override of payerName → DB has override, parser raw text still has original

For tests, use `StubShopperPaymentStorage`, `StubNotificationSender` (mevcut). Fixture PDF bytes for parse (use existing test fixtures from OrderDeck.Tests if any, or generate minimal PDF via library).

**Commit:** `feat(shopper-payments): ShopperPaymentSubmissionService (6 fraud layers)`

---

## Task 5: `ShopperPaymentSubmitController` endpoint

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs` — add POST payment endpoint
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperPaymentSubmitTests.cs`

**Endpoint:** `POST /api/v1/shopper/broadcasters/{licenseId}/payments` (Bearer-Shopper, multipart/form-data)

**Multipart fields:**
- `pdf` (file, required)
- `amount` (decimal, optional)
- `payerName` (string, optional)
- `paidAt` (DateTimeOffset, optional)
- `referansNo` (string, optional)

**Akış:**
1. Parse shopperId from claims; null → 401
2. Validate active link → 403 not-linked
3. Read PDF stream → max 5 MB; if larger → 413 payload-too-large
4. Construct SubmitInput, call ShopperPaymentSubmissionService.SubmitAsync
5. Catch SubmitFailure → return Problem(statusCode, errorCode)
6. On success → 201 + { paymentId, parsedMetadata, fraudFlags }

**Response DTO:**
```csharp
public sealed record SubmitResponse(
    Guid PaymentId,
    string[] FraudFlags,
    string ParserConfidence,
    SubmitParsedMetadata Parsed);

public sealed record SubmitParsedMetadata(
    string? PayerName, decimal? Amount,
    DateTimeOffset? PaidAt, string? ReferansNo,
    string? RecipientIban, string? RecipientName);
```

**Tests (~8):**
1. Happy path multipart upload → 201 + parsed metadata
2. No PDF file → 400
3. PDF > 5 MB → 413
4. Invalid PDF → 400
5. Not linked → 403
6. Rate limit exceeded → 429
7. Override fields override parser → 201 with override applied
8. No auth → 401

**Commit:** `feat(shopper): broadcasters/{id}/payments POST (upload + fraud)`

---

## Task 6: `ShopperDevicesController` (register + unregister)

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperDevicesController.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperDevicesTests.cs`

**Endpoints:**

`POST /api/v1/shopper/devices` (Bearer-Shopper)
- Body: `{ deviceId, platform, pushToken }`
- Akış: upsert ShopperPushDevice by (ShopperId, DeviceId)
- Return 204

`DELETE /api/v1/shopper/devices/{deviceId}` (Bearer-Shopper)
- Akış: find row by (ShopperId, DeviceId), delete; missing → 404
- Return 204

**Tests (~6):**
1. Register new device → 204 + DB has row
2. Re-register same deviceId (upsert) → 204, no duplicate (verify only 1 row in DB)
3. Re-register from different shopper → both rows exist (different ShopperId)
4. Delete existing → 204
5. Delete missing → 404
6. No auth → 401

**Commit:** `feat(shopper): devices register + unregister endpoints`

---

## Self-Review

**Spec coverage** (`docs/superpowers/specs/2026-05-20-customer-app-design.md`, dekont upload + push devices):

| Spec maddesi | Task |
|--------------|------|
| Server-side PDF parse | T2, T4 |
| 6 fraud katmanı (IBAN, PdfHash, MetadataHash, rate limit, parser confidence, audit) | T3, T4 |
| TC > 9990 koşullu zorunlu | T4 |
| R2 upload (direct, not pre-signed) | T1 |
| Push fan-out to broadcaster | T4 (via INotificationSender) |
| `POST /api/v1/shopper/broadcasters/{licenseId}/payments` | T5 |
| `POST /api/v1/shopper/devices` | T6 |
| `DELETE /api/v1/shopper/devices/{deviceId}` | T6 |

**Out of scope (separate plans):**
- PDF retention 30g Hangfire job — Faz 0b sonrası ayrı PR
- WPF tarafı sync (IBAN + Customer projection) — Faz 0c

**Type consistency:**
- `IShopperPaymentStorage.UploadAsync(objectKey, bytes, contentType)` — used by T4 SubmissionService
- `ParserConfidenceCalculator.Compute(ParseResult) → string` — value "High"/"Medium"/"Low"; matches `Payment.ParserConfidence` max length 16
- `SubmitFailure(statusCode, errorCode)` — controller maps to Problem(statusCode, title:errorCode)
- `PaymentSubmissionAudit.LicenseId` — yeni Guid kolon (T3 migration)

---

## Sonraki Plan

Faz 0b-4 merge sonrası:
- **Faz 0c** — WPF Customer projection sync + IBAN sync endpoint + WPF Settings UI + mobile panel ShopperCode UI (artık tüm server-side ready)
- **Faz 0 sonu** — PDF retention Hangfire job (kısa PR)
- **Faz 1** — Mobile shopper app (yeni repo, ilk 4 ekran: OnboardingCode, OnboardingForm, Login, Feed)
