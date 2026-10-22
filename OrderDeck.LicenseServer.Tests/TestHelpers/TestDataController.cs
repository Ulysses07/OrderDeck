using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Yayıncının veri sorumlusu bilgisi (Görev 21): banka bağlantısı kuran testlerin ön koşulu (KVKK K-3 kapısı,
/// <c>Obifin:RequireDataControllerProfile</c>). Değerler sahte, e-posta üretilir; kaydetmez.</summary>
public static class TestDataController
{
    public static void Add(LicenseDbContext db, Guid customerId)
        => db.DataControllerProfiles.Add(new DataControllerProfile
        {
            CustomerId = customerId, LegalName = "Test Yayıncı", PostalAddress = "Test Mah. 1. Sok. No:1",
            KvkkEmail = $"kvkk-{Guid.NewGuid():N}@example.test", Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
        });
}
