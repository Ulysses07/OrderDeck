using OrderDeck.LicenseServer.Services.IntakeForm.Login;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Sağlayıcıya gitmeden bağlama akışını test etmek için. Result
/// test başında kurulur; Codes, hangi authorization code'un iletildiğini çiviler.</summary>
public sealed class FakeGoogleChannelClient : IGoogleChannelClient
{
    public IntakeLoginResult Result { get; set; } = new(false, "saglayici", null);
    public List<string> Codes { get; } = new();

    public Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct)
    {
        Codes.Add(code);
        return Task.FromResult(Result);
    }
}

public sealed class FakeFacebookNameClient : IFacebookNameClient
{
    public IntakeLoginResult Result { get; set; } = new(false, "saglayici", null);
    public List<string> Codes { get; } = new();

    public Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct)
    {
        Codes.Add(code);
        return Task.FromResult(Result);
    }
}
