using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>KVKK K-3: yönetici yayıncının veri sorumlusu bilgilerini müşteri detay sayfasından girer.</summary>
public sealed class AdminCustomerDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminCustomerDataControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Yonetici_veri_sorumlusu_bilgilerini_kaydeder_ve_denetime_yazar()
    {
        var customerId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.Add(new Customer { Id = customerId, Email = $"dc-{Guid.NewGuid():N}@example.test", Name = "Dc",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = $"/admin/customers/{customerId}";
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(path));

        var resp = await client.PostAsync($"{path}?handler=DataController", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["DcLegalName"] = "Örnek Ticaret Ltd. Şti.",
            ["DcPostalAddress"] = "Örnek Mah. 1. Sok. No:2 İstanbul", ["DcKvkkEmail"] = "kvkk@example.test",
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var profile = await vdb.DataControllerProfiles.AsNoTracking().SingleAsync(p => p.CustomerId == customerId);
        profile.LegalName.Should().Be("Örnek Ticaret Ltd. Şti.");
        profile.KepAddress.Should().BeNull();
        var adminId = (await vdb.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id;
        profile.UpdatedBy.Should().Be("admin:" + adminId, "yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14)");
        (await vdb.DataControllerProfileVersions.AsNoTracking().SingleAsync(v => v.CustomerId == customerId))
            .Should().Match<DataControllerProfileVersion>(v => v.Version == 1 && v.LegalName == "Örnek Ticaret Ltd. Şti."
                && v.CreatedBy == "admin:" + adminId);
        (await vdb.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.CustomerDataControllerUpdate && a.TargetId == customerId.ToString()))
            .Should().BeTrue();
        (await client.GetStringAsync(path)).Should().Contain("kvkk@example.test");
    }
}
