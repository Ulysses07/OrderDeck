using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Hesap bağlama kuralı TEK yerde: elle bağlayan admin ucu ile Embedded Signup
/// ucu aynı gövdeyi çağırır. İki kopya olsaydı biri "PhoneNumberId başkasında"
/// kontrolünü kaybettiğinde webhook'lar sessizce yanlış tenant'a giderdi.
/// </summary>
public sealed class WhatsAppAccountUpsertTests
{
    private static WhatsAppAccountService Service(out LicenseDbContext db)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase("wa-upsert-" + Guid.NewGuid().ToString("N"))
            .Options;
        db = new LicenseDbContext(options);
        return new WhatsAppAccountService(
            db, DataProtectionProvider.Create("tests"), Options.Create(new WhatsAppOptions()));
    }

    private static WhatsAppAccountUpsert Input(string pnid) =>
        new("WABA_1", pnid, "+90 555 111 22 33", "TOKEN", "Emar", "123456");

    [Fact]
    public async Task Connecting_twice_updates_the_same_row_instead_of_adding_one()
    {
        var svc = Service(out var db);
        using var _ = db;
        var licenseId = Guid.NewGuid();

        (await svc.UpsertAsync(licenseId, Input("PNID_1"), CancellationToken.None)).Ok
            .Should().BeTrue();
        (await svc.UpsertAsync(licenseId, Input("PNID_1"), CancellationToken.None)).Ok
            .Should().BeTrue();

        db.WhatsAppAccounts.Count(a => a.LicenseId == licenseId).Should().Be(1);
    }

    [Fact]
    public async Task A_number_already_bound_to_another_license_is_refused()
    {
        var svc = Service(out var db);
        using var _ = db;

        await svc.UpsertAsync(Guid.NewGuid(), Input("PNID_SHARED"), CancellationToken.None);
        var second = await svc.UpsertAsync(Guid.NewGuid(), Input("PNID_SHARED"), CancellationToken.None);

        // Kabul edilseydi gelen webhook'un hangi lisansa ait olduğu belirsiz kalırdı.
        second.Ok.Should().BeFalse();
        second.Conflict.Should().BeTrue();
    }

    [Fact]
    public async Task The_token_and_the_pin_are_never_stored_in_clear_text()
    {
        var svc = Service(out var db);
        using var _ = db;
        var licenseId = Guid.NewGuid();

        await svc.UpsertAsync(licenseId, Input("PNID_2"), CancellationToken.None);

        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == licenseId);
        row.AccessTokenProtected.Should().NotContain("TOKEN");
        row.TwoStepPinProtected.Should().NotBeNull().And.NotContain("123456");
        svc.TryUnprotectToken(row.AccessTokenProtected).Should().Be("TOKEN");
    }
}
