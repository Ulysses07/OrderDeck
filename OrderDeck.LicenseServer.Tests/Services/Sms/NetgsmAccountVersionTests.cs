using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public sealed class NetgsmAccountVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parola_yazimi_saat_ilerlemese_bile_bayat_yaziyi_reddeder(
        bool useAsyncSave)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"netgsm-version-{Guid.NewGuid():N}")
            .Options;

        var accountId = Guid.NewGuid();
        var originalStamp = DateTimeOffset.UtcNow.AddDays(1);
        var replacement = $"protected-{Guid.NewGuid():N}";

        await using (var seed = new LicenseDbContext(options))
        {
            seed.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = accountId,
                LicenseId = Guid.NewGuid(),
                UserCode = Random.Shared
                    .NextInt64(8_500_000_000, 8_599_999_999).ToString(),
                PasswordProtected = $"protected-{Guid.NewGuid():N}",
                Header = "ORDERDECK",
                BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
                Status = NetgsmAccountStatus.Failed,
                CreatedAt = originalStamp,
                UpdatedAt = originalStamp,
            });

            await seed.SaveChangesAsync();
        }

        await using var verifier = new LicenseDbContext(options);
        await using var writer = new LicenseDbContext(options);

        var stale = await verifier.NetgsmAccounts
            .SingleAsync(a => a.Id == accountId);
        var current = await writer.NetgsmAccounts
            .SingleAsync(a => a.Id == accountId);

        // Yazıcı UpdatedAt atamayı unutsa bile koruma çalışmalı.
        current.PasswordProtected = replacement;

        if (useAsyncSave)
            await writer.SaveChangesAsync();
        else
            writer.SaveChanges();

        current.UpdatedAt.Should().Be(originalStamp.AddTicks(1));
        stale.Status = NetgsmAccountStatus.Verified;

        Func<Task> staleWrite = async () =>
        {
            if (useAsyncSave)
                await verifier.SaveChangesAsync();
            else
                verifier.SaveChanges();
        };

        await staleWrite.Should()
            .ThrowAsync<DbUpdateConcurrencyException>();

        await using var read = new LicenseDbContext(options);
        var persisted = await read.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == accountId);

        persisted.PasswordProtected.Should().Be(replacement);
        persisted.Status.Should().Be(NetgsmAccountStatus.Failed);
        persisted.UpdatedAt.Should().Be(originalStamp.AddTicks(1));
    }
}
