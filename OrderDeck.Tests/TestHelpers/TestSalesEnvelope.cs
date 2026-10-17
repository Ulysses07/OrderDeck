using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Gönderim servislerinin testleri için satış senkronu zarfı (<see cref="SalesSyncEnvelope"/>): kopya kimliği ve
/// görülen kip testin veritabanından (göç 049 kimliği tohumlar), donanım parmak izi her örnekte üretilir.
/// </summary>
internal static class TestSalesEnvelope
{
    public static SalesSyncEnvelope For(
        IDbConnectionFactory db, ICurrentLicenseProvider license, ILogger<SalesSyncEnvelope>? log = null)
        => new(new ReplicaIdentityRepository(db), new SalesSyncStateRepository(db), new GeneratedHardware(),
            license, log ?? NullLogger<SalesSyncEnvelope>.Instance);

    private sealed class GeneratedHardware : IHardwareIdProvider
    {
        private readonly string _id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public string GetHardwareId() => _id;
        public string? GetLegacyHardwareId() => null;
    }
}
