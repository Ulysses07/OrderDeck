using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

/// <summary>
/// §2.1 — kurulumu tamamlanmamış yayıncının formunda SMS onay kutusu
/// GÖRÜNMEZ. Kutu görünüp onay yazılmazsa kullanıcıya verilmemiş bir söz
/// verilmiş olur; kutu görünüp onay yazılırsa marka olmadan İYS'ye
/// bildirilemeyen, üç iş günü sonra hukuken geçersiz bir onay doğar.
/// İkisi de kabul edilemez, o yüzden kutu kurulumla birlikte doğar.
/// </summary>
public sealed class IntakeFormSmsConsentVisibilityTests : IClassFixture<ApiFactory>
{
    private const string CheckboxText = "SMS üzerinden bilgilendirme";

    private readonly ApiFactory _factory;
    public IntakeFormSmsConsentVisibilityTests(ApiFactory factory) => _factory = factory;

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private async Task<string> SeedConfigAsync(NetgsmAccountStatus? accountStatus)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"sv-{Guid.NewGuid():N}@x",
            Name = "Sv",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-SV-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        if (accountStatus is { } status)
        {
            db.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                UserCode = NewUserCode(),
                PasswordProtected = $"pw-{Guid.NewGuid():N}",
                Header = "ORDERDECK",
                BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var slug = $"sv-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return slug;
    }

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task Dogrulanmis_kurulumda_kutu_gorunur()
    {
        var slug = await SeedConfigAsync(NetgsmAccountStatus.Verified);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().Contain(CheckboxText);
    }

    [Fact]
    public async Task Hesapsiz_kurulumda_kutu_gorunmez()
    {
        var slug = await SeedConfigAsync(accountStatus: null);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().NotContain(CheckboxText);
    }

    [Theory]
    [InlineData(NetgsmAccountStatus.Failed)]
    [InlineData(NetgsmAccountStatus.Disabled)]
    public async Task Dogrulanmamis_hesapta_kutu_gorunmez(NetgsmAccountStatus status)
    {
        var slug = await SeedConfigAsync(status);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().NotContain(CheckboxText);
    }

    [Fact]
    public async Task Kutu_kapaliyken_elle_gonderilen_onay_yok_sayilir()
    {
        // Kutunun gizlenmesi görsel bir önlem; istemci alanı elle ekleyebilir.
        // Sunucu tarafı da reddetmezse gizleme sadece dürüst kullanıcıyı korur.
        var slug = await SeedConfigAsync(accountStatus: null);
        var client = NewClient();
        var phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

        var getResp = await client.GetAsync($"/r/{slug}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(
            await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "kutu",
            ["Input.FullName"] = "Ad Soyad",
            ["Input.Email"] = "a@example.com",
            ["Input.Address"] = "Adres",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = phone,
            ["Input.SmsConsent"] = "true",
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions.AsNoTracking()
            .SingleAsync(s => s.Phone == phone);
        sub.SmsConsent.Should().BeFalse("kutu kapalıyken onay kaydı doğmamalı");
        (await db.IysConsentEvents.AsNoTracking().AnyAsync(e => e.Recipient == phone))
            .Should().BeFalse();
    }
}
