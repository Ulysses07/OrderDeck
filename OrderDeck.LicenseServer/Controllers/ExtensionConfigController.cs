using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using OrderDeck.LicenseServer.Extension;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Public, anonymous endpoint that serves the DOM selector bundle the
/// OrderDeck browser extension depends on. The body comes straight from
/// <see cref="SelectorRegistry"/> — no DB, no auth, no per-user shape — so
/// the response is identical for every caller and is therefore safe (and
/// fast) to cache aggressively at any layer along the way.
///
/// <para><b>Caching contract:</b> we issue a strong ETag and a 10-minute
/// max-age. The extension sends <c>If-None-Match</c> on every refresh
/// tick; matching ETags return 304 with no body, so the wire cost stays
/// near zero between selector changes.</para>
///
/// <para><b>CORS:</b> wildcard <c>Access-Control-Allow-Origin: *</c> is
/// applied locally in this controller — the global default policy is
/// intentionally closed (see <c>Program.cs</c>). The bundle has no
/// per-user data, so wildcard access is acceptable.</para>
/// </summary>
[ApiController]
[Route("api/v1/extension")]
public sealed class ExtensionConfigController : ControllerBase
{
    [HttpGet("selectors")]
    public IActionResult GetSelectors()
    {
        var etag = SelectorRegistry.CurrentETag;

        // Honour If-None-Match — return 304 without a body to save bandwidth
        // when the cached client copy is still up to date.
        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch))
        {
            foreach (var v in ifNoneMatch)
            {
                if (string.Equals(v, etag, System.StringComparison.Ordinal))
                {
                    Response.Headers[HeaderNames.ETag] = etag;
                    return StatusCode(StatusCodes.Status304NotModified);
                }
            }
        }

        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=600";
        // Wildcard CORS — the bundle is public selector data, no credentials.
        Response.Headers["Access-Control-Allow-Origin"] = "*";

        return Content(SelectorRegistry.CurrentJson, "application/json");
    }
}
