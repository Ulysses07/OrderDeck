using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>
/// Sipariş metninin KVKK politikası — spec 2026-10-13 §9. Kalıcı boşaltma tek bir ayara bağlı ve varsayılan kapalı;
/// açıkken yalnız takma ad ve yorum gider, mali kayıt kalır. Boşaltma anı adli kayıttır: ilk an korunur.
/// </summary>
public sealed class OrderTextScrubTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bosaltma_metni_siler_mali_kaydi_ve_ilk_ani_korur()
    {
        var order = new Order
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid().ToString("N"), Platform = "youtube",
            Username = "kisi", DisplayName = "Takma ad", MessageText = "A1 alırım", Code = "A1", Price = 150m,
        };

        OrderTextScrub.Apply(order, T0);
        order.DisplayName.Should().BeNull();
        order.MessageText.Should().BeEmpty();
        order.TextScrubbedAt.Should().Be(T0);
        order.Username.Should().Be("kisi");
        order.Code.Should().Be("A1");
        order.Price.Should().Be(150m);

        OrderTextScrub.Apply(order, T0.AddDays(3));
        order.TextScrubbedAt.Should().Be(T0, "tekrar eden boşaltma ilk anı korur");
    }

    /// <summary>Eklemede boşaltma ancak ayar açık VE asıl projeksiyon silinmişse; okuma maskesi ayardan bağımsız.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Eklemede_bosaltma_ayar_ve_silinmislik_ister(bool settingOn, bool purged, bool expected)
    {
        DateTimeOffset? purgedAt = purged ? T0 : null;
        OrderTextScrub.AppliesOnInsert(settingOn, purgedAt).Should().Be(expected);
        OrderTextScrub.MasksOnRead(purgedAt).Should().Be(purged);
    }
}
