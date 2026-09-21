using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmSmsExceptionTests
{
    [Theory]
    [InlineData("30")]
    [InlineData("40")]
    [InlineData("50")]
    [InlineData("51")]
    public void AccountClass_Codes(string code)
        => Assert.Equal(NetgsmErrorClass.Account, new NetgsmSmsException(code, "x").Classify());

    [Fact]
    public void RecipientClass_Code70()
        => Assert.Equal(NetgsmErrorClass.Recipient, new NetgsmSmsException("70", "x").Classify());

    // §3.4 karar 2: bilinmeyen kod kitleyi HARCAMAZ — varsayılan kampanya duraklatma.
    [Theory]
    [InlineData("20")]
    [InlineData("80")]
    [InlineData("85")]
    [InlineData("999")]
    [InlineData(null)]
    public void UnknownClass_DefaultsToCampaignPause(string? code)
        => Assert.Equal(NetgsmErrorClass.CampaignPause, new NetgsmSmsException(code, "x").Classify());
}
