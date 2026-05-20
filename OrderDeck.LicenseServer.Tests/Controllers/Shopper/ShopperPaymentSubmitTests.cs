using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

// ── Fake PDF parser — returns a canned result but generates a unique PdfHash
//    per call so duplicate-detection doesn't trip across tests.
internal sealed class UniqueHashPdfDekontParser : IPdfDekontParser
{
    // Fixed metadata that tests can inspect
    public const string FixedPayerName = "AHMET YILMAZ";
    public const decimal FixedAmount = 250m;
    public const string FixedRecipientIban = "TR330006100519786457841326";

    public PdfDekontParser.ParseResult Parse(byte[] pdfBytes) =>
        new(
            PayerName: FixedPayerName,
            Amount: FixedAmount,
            PaidAt: new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc),
            ReferansNo: "REF20260510",
            PdfHash: Guid.NewGuid().ToString("N"),  // unique per call → no cross-test duplicates
            RawText: "fake raw text",
            RecipientIban: FixedRecipientIban,
            RecipientName: "SELLER COMPANY");
}

// ── Derived ApiFactory that swaps IPdfDekontParser with the fake
public sealed class PaymentSubmitApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPdfDekontParser>();
            services.AddSingleton<IPdfDekontParser>(new UniqueHashPdfDekontParser());
        });
    }
}

public class ShopperPaymentSubmitTests : IClassFixture<PaymentSubmitApiFactory>
{
    private readonly PaymentSubmitApiFactory _factory;

    public ShopperPaymentSubmitTests(PaymentSubmitApiFactory factory) => _factory = factory;

    // ── Minimal valid PDF bytes (magic header + stub body)
    private static readonly byte[] ValidPdfBytes =
    {
        0x25, 0x50, 0x44, 0x46, 0x2D, // %PDF-
        0x31, 0x2E, 0x34, 0x0A,        // 1.4\n
        0x00, 0x01, 0x02, 0x03,        // junk tail
    };

    private static readonly byte[] InvalidPdfBytes = { 0xDE, 0xAD, 0xBE, 0xEF };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string shopperCode)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"sub-{Guid.NewGuid():N}@x.test",
            Name = "Sub-BC-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "sub-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    private async Task<(string token, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string code)
    {
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "Sub User", phone, "SubPass1!", "Istanbul", "youtube", "subuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    /// <summary>Builds a MultipartFormDataContent with a PDF file field.</summary>
    private static MultipartFormDataContent BuildMultipart(
        byte[]? pdfBytes = null,
        string? amount = null,
        string? payerName = null)
    {
        var form = new MultipartFormDataContent();
        if (pdfBytes is not null)
        {
            var pdfContent = new ByteArrayContent(pdfBytes);
            pdfContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            form.Add(pdfContent, "pdf", "dekont.pdf");
        }
        if (amount is not null) form.Add(new StringContent(amount), "amount");
        if (payerName is not null) form.Add(new StringContent(payerName), "payerName");
        return form;
    }

    // ── T1: Happy path — valid PDF with fake parser → 201 ────────────────────

    [Fact]
    public async Task Submit_with_valid_link_uploads_and_returns_201()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildMultipart(ValidPdfBytes);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("paymentId").GetGuid().Should().NotBeEmpty();
        body.GetProperty("parserConfidence").GetString().Should().NotBeNullOrEmpty();
    }

    // ── T2: No pdf field → 400 ────────────────────────────────────────────────

    [Fact]
    public async Task Submit_no_pdf_returns_400()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Send multipart with NO pdf field
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("100"), "amount");
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("missing-pdf");
    }

    // ── T3: PDF too large → 413 ───────────────────────────────────────────────

    [Fact]
    public async Task Submit_pdf_too_large_returns_413()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 5MB + 1 byte — just over the limit
        var bigPdf = new byte[5 * 1024 * 1024 + 1];
        bigPdf[0] = 0x25; bigPdf[1] = 0x50; bigPdf[2] = 0x44; bigPdf[3] = 0x46; bigPdf[4] = 0x2D; // %PDF-
        using var form = BuildMultipart(bigPdf);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // ── T4: Invalid PDF bytes (no magic header) → 400 invalid-pdf ────────────

    [Fact]
    public async Task Submit_invalid_pdf_returns_400()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildMultipart(InvalidPdfBytes);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("invalid-pdf");
    }

    // ── T5: Not linked → 403 ─────────────────────────────────────────────────

    [Fact]
    public async Task Submit_when_not_linked_returns_403()
    {
        var client = _factory.CreateClient();
        var (linkedLicenseId, linkedCode) = await SeedLicenseAsync();
        var (unlinkedLicenseId, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, linkedCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = BuildMultipart(ValidPdfBytes);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{unlinkedLicenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("not-linked");
    }

    // ── T6: Deleted shopper → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task Submit_when_deleted_shopper_returns_401()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, code);

        // Soft-delete the shopper
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = await db.Shoppers.FindAsync(shopperId);
            shopper!.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var form = BuildMultipart(ValidPdfBytes);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T7: No auth header → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task Submit_without_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var (licenseId, _) = await SeedLicenseAsync();

        using var form = BuildMultipart(ValidPdfBytes);
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T8: Override fields accepted — response includes parsed metadata ──────
    //    The "parsed" block in the response reflects the raw parser output.
    //    Override values (amount, payerName) are persisted in the Payment row
    //    but the response's "parsed" section still shows what the parser extracted.

    [Fact]
    public async Task Submit_with_override_fields_returns_201()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Post with override fields — should still succeed
        // Note: use integer-only amount to avoid locale decimal separator ambiguity
        using var form = BuildMultipart(ValidPdfBytes, amount: "500", payerName: "OVERRIDE PAYER");
        var resp = await client.PostAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Response still has a paymentId and the parser's data in "parsed"
        body.GetProperty("paymentId").GetGuid().Should().NotBeEmpty();
        // "parsed" section shows raw parser output (not the client overrides)
        body.GetProperty("parsed").GetProperty("payerName").GetString()
            .Should().Be(UniqueHashPdfDekontParser.FixedPayerName);
        // parser amount (250m) is what's in parsed section; override (500) goes to DB
        body.GetProperty("parsed").GetProperty("amount").GetDecimal()
            .Should().Be(UniqueHashPdfDekontParser.FixedAmount);
    }
}
