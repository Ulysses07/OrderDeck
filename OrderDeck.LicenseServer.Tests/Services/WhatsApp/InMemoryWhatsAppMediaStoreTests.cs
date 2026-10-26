using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class InMemoryWhatsAppMediaStoreTests
{
    [Fact]
    public async Task Lisans_oneki_yalniz_o_lisansin_medyasini_listeler_ve_siler()
    {
        var store = new InMemoryWhatsAppMediaStore();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mineKeys = new[]
        {
            $"{WhatsAppMediaKeys.LicensePrefix(mine)}2026/09/m1.jpg", $"{WhatsAppMediaKeys.LicensePrefix(mine)}2026/10/m2.ogg",
        };
        var otherKey = $"{WhatsAppMediaKeys.LicensePrefix(other)}2026/09/m3.jpg";
        foreach (var k in mineKeys.Append(otherKey)) await store.SaveAsync(k, new byte[] { 1 }, "image/jpeg");

        var listed = await store.ListLicenseObjectsAsync(mine);
        var failures = await store.DeleteObjectsAsync(listed);

        listed.Should().BeEquivalentTo(mineKeys);
        failures.Should().BeEmpty();
        store.Count.Should().Be(1);
        store.TryGet(otherKey, out _).Should().BeTrue("başka lisansın medyası kalır");
    }
}
