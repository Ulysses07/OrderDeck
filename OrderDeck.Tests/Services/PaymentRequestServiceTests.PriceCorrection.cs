using System;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Fiyat düzeltmesi sonrası tekrar gönderme sorusu (K5, 2026-10-12) ─────────
//
// #506'nın "tekrar gönderilsin mi?" sorusu yalnız ESKİ tutarı gösteriyordu; fiyat düzeltildikten sonra
// operatör "Evet"in aynı isteği mi düzeltilmiş isteği mi göndereceğini göremiyordu. Tutar değiştiyse
// soru yeni tutarı da taşır.
public partial class PaymentRequestServiceTests
{
    [Fact]
    public async Task Tutar_degistiyse_tekrar_gonderme_sorusu_yeni_tutari_tasir()
    {
        EnableCloudApi();
        var (sut, _) = MakeCloudSut(_store, _launcher);
        var c = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(c, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Sent);

        var degisti = await sut.RequestPaymentAsync(c, _ => 205m, T, "session:s1");
        degisti.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        degisti.Prompt!.NewTotal.Should().Be(205m);
        degisti.Prompt.Requests.Should().ContainSingle().Which.Amount.Should().Be(250m);

        var ayni = await sut.RequestPaymentAsync(c, _ => 250m, T, "session:s1");
        ayni.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        ayni.Prompt!.NewTotal.Should().BeNull("tutar aynıysa soru eskisi gibi");
    }
}
