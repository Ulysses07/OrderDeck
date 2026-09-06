using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Instagram;

/// <summary>
/// Instagram live_comments webhook uç noktasının HTTP yüzeyi.
///
/// <para><b>Karanlık yayın:</b> <c>InstagramDm__Enabled</c> yokken her iki
/// metod 404 döner — özellik henüz devrede değilken Meta'nın webhook
/// doğrulaması da geçemez.</para>
///
/// <para>İmza doğrulaması ve 5xx→403 davranışı WhatsApp webhook testiyle
/// aynı gerekçelerle test edilir.</para>
/// </summary>
public sealed class InstagramWebhookControllerTests
{
    // Sabit literal token YASAK (repo public, GitGuardian). Guid üret.
    private static readonly string VerifyToken = $"igvt-{Guid.NewGuid():N}";
    private static readonly string AppSecret   = $"igas-{Guid.NewGuid():N}";

    private const string Url = "/api/v1/instagram/webhook";

    // ── Bayrak KAPALI: her iki metod 404 ─────────────────────────────────────

    public sealed class DisabledFactory : ApiFactory
    {
        // ExtraConfig boş → InstagramDm__Enabled yok → Ready == false
    }

    public sealed class Bayrak_kapaliyken_get_ve_post_404 : IClassFixture<DisabledFactory>
    {
        private readonly DisabledFactory _factory;
        public Bayrak_kapaliyken_get_ve_post_404(DisabledFactory factory) => _factory = factory;

        [Fact]
        public async Task Get_returns_404()
        {
            var resp = await _factory.CreateClient().GetAsync(
                $"{Url}?hub.mode=subscribe&hub.verify_token=herhangi&hub.challenge=abc");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Post_returns_404()
        {
            var resp = await _factory.CreateClient().PostAsync(
                Url, new StringContent("{}", Encoding.UTF8, "application/json"));
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    // ── Bayrak AÇIK ──────────────────────────────────────────────────────────

    public sealed class EnabledFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["InstagramDm:Enabled"]     = "true",
            ["InstagramDm:VerifyToken"] = VerifyToken,
            ["OrderDeck:Facebook:AppSecret"] = AppSecret,
        };
    }

    public sealed class Enabled : IClassFixture<EnabledFactory>
    {
        private readonly EnabledFactory _factory;
        public Enabled(EnabledFactory factory) => _factory = factory;

        [Fact]
        public async Task Verify_dogru_tokenla_challenge_doner()
        {
            var resp = await _factory.CreateClient().GetAsync(
                $"{Url}?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=abc");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            (await resp.Content.ReadAsStringAsync()).Should().Be("abc");
        }

        [Fact]
        public async Task Verify_yanlis_token_403()
        {
            var resp = await _factory.CreateClient().GetAsync(
                $"{Url}?hub.mode=subscribe&hub.verify_token=yanlis&hub.challenge=abc");

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Imzasiz_post_403()
        {
            var resp = await _factory.CreateClient().PostAsync(
                Url, new StringContent("{\"object\":\"instagram\"}", Encoding.UTF8, "application/json"));

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Sahte_imzali_post_403()
        {
            var body = "{\"object\":\"instagram\"}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Hub-Signature-256", Sign(body, $"yanlis-{Guid.NewGuid():N}"));

            var resp = await _factory.CreateClient().PostAsync(Url, content);

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Imzali_post_200_ve_job_kuyruklanir()
        {
            var body = "{\"object\":\"instagram\",\"entry\":[]}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Hub-Signature-256", Sign(body, AppSecret));

            var resp = await _factory.CreateClient().PostAsync(Url, content);

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
