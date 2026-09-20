using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Owner istemcisinden bir <c>staff</c> operatör açar ve o operatörün jetonunu
/// taşıyan istemciyi döndürür. Owner-only uçların 403 döndürdüğünü kanıtlamak
/// için gereken tek kurulum bu.
/// (Kalıp: <c>PanelWhatsAppAccountControllerTests.StaffClientAsync</c>.)
/// </summary>
public static class PanelOperatorHelper
{
    public static async Task<HttpClient> StaffClientAsync(ApiFactory factory, HttpClient ownerClient)
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}";

        var create = await ownerClient.PostAsJsonAsync(
            "/api/panel/operators", new { email, password, role = "staff", name = "Staff" });
        create.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/operator-login", new { email, password });
        login.EnsureSuccessStatusCode();

        // Alan adı `token` — `accessToken` DEĞİL. Sözleşme
        // `AuthController.OperatorLoginResponse.Token`; JSON camelCase ile
        // `token` olarak çıkar. Yanlış ad yazarsan `GetProperty` fırlatır ve
        // testler yetki denetimine hiç varamaz.
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
