using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;

namespace OrderDeck.LicenseServer.Services.Email;

/// <summary>
/// Hangfire dashboard auth filter — sadece AdminCookie ile auth edilmiş kullanıcılara izin verir.
/// Anonim istekler 401 Unauthorized alır (Hangfire dashboard kendi response'unu render eder).
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var result = http.AuthenticateAsync("AdminCookie").GetAwaiter().GetResult();
        return result.Succeeded;
    }
}
