using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Gelen medyanın kalıcılaştırılması. Meta'nın iki adımlı akışı taklit edilir:
/// önce <c>GET /{media-id}</c> (metadata + 5 dk ömürlü URL), sonra o URL'den
/// bayt indirme. İkisi de aynı sahte handler'dan geçer.
/// </summary>
public sealed class WhatsAppMediaDownloaderTests
{
    private const string MediaId = "MEDIA_1";
    private const string MediaUrl = "https://lookaside.fbsbx.test/whatsapp/asset";

    private sealed class FakeGraphHandler : HttpMessageHandler
    {
        private readonly string _metadataJson;
        private readonly HttpStatusCode _metadataStatus;
        private readonly byte[] _payload;

        public List<string> Requests { get; } = new();
        public List<string?> AuthHeaders { get; } = new();

        public FakeGraphHandler(
            string metadataJson, byte[] payload, HttpStatusCode metadataStatus = HttpStatusCode.OK)
        {
            _metadataJson = metadataJson;
            _payload = payload;
            _metadataStatus = metadataStatus;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            AuthHeaders.Add(request.Headers.Authorization?.ToString());

            // İndirme adımı, metadata'nın döndüğü mutlak URL'e gider.
            if (url.StartsWith(MediaUrl, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_payload),
                });
            }

            return Task.FromResult(new HttpResponseMessage(_metadataStatus)
            {
                Content = new StringContent(_metadataJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string MetadataJson(string mime, long fileSize) => $$"""
        {
          "messaging_product": "whatsapp",
          "url": "{{MediaUrl}}",
          "mime_type": "{{mime}}",
          "sha256": "abc",
          "file_size": {{fileSize}},
          "id": "{{MediaId}}"
        }
        """;

    private static string ImagePayload(
        string wamId = "wamid.IMG", string pnid = "PNID_1", string from = "905321234567") => $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "{{{pnid}}}" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "{{{from}}}" }],
            "messages": [{ "from": "{{{from}}}", "id": "{{{wamId}}}", "timestamp": "1753440000",
                           "type": "image",
                           "image": { "id": "{{{MediaId}}}", "mime_type": "image/jpeg",
                                      "caption": "kargo etiketi" } }]
          }}]}]
        }
        """;

    private static (LicenseDbContext Db, WhatsAppInboundJob Job, InMemoryWhatsAppMediaStore Store)
        Build(FakeGraphHandler handler)
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wamedia-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            WabaId = "waba-1",
            PhoneNumberId = "PNID_1",
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("TOKEN_X"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var store = new InMemoryWhatsAppMediaStore();
        var downloader = new WhatsAppMediaDownloader(
            new HttpClient(handler),
            store,
            Options.Create(new WhatsAppOptions { GraphBaseUrl = "https://graph.test" }),
            NullLogger<WhatsAppMediaDownloader>.Instance);

        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance, downloader);

        return (db, job, store);
    }

    [Fact]
    public async Task Incoming_media_is_downloaded_and_stored()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new FakeGraphHandler(MetadataJson("image/jpeg", bytes.Length), bytes);
        var (db, job, store) = Build(handler);

        await job.ProcessAsync(ImagePayload());

        var msg = db.WaMessages.Single();
        msg.MediaR2Key.Should().NotBeNullOrEmpty();
        msg.MediaR2Key.Should().EndWith(".jpg", "uzantı mime'dan türetiliyor");
        msg.MediaMimeType.Should().Be("image/jpeg");
        msg.MediaSizeBytes.Should().Be(bytes.Length);
        msg.Body.Should().Be("kargo etiketi", "caption gövde olarak saklanır");

        store.TryGet(msg.MediaR2Key!, out var stored).Should().BeTrue();
        stored.Should().Equal(bytes);
    }

    [Fact]
    public async Task Both_graph_calls_carry_the_tenant_bearer_token()
    {
        var handler = new FakeGraphHandler(MetadataJson("image/jpeg", 3), new byte[] { 9, 9, 9 });
        var (_, job, _) = Build(handler);

        await job.ProcessAsync(ImagePayload());

        handler.Requests.Should().HaveCount(2, "önce metadata, sonra indirme");
        handler.Requests[0].Should().Contain("phone_number_id=PNID_1");
        handler.AuthHeaders.Should().AllBe("Bearer TOKEN_X");
    }

    [Fact]
    public async Task Oversized_media_keeps_metadata_but_skips_the_download()
    {
        // Meta'nın görsel limiti 5 MB — üstünü indirmeye kalkmayız.
        var handler = new FakeGraphHandler(
            MetadataJson("image/jpeg", 6L * 1024 * 1024), Array.Empty<byte>());
        var (db, job, store) = Build(handler);

        await job.ProcessAsync(ImagePayload());

        var msg = db.WaMessages.Single();
        msg.MediaR2Key.Should().BeNull();
        msg.MediaMimeType.Should().Be("image/jpeg");
        msg.MediaSizeBytes.Should().Be(6L * 1024 * 1024);

        store.Count.Should().Be(0);
        handler.Requests.Should().ContainSingle("indirme adımına hiç geçilmemeli");
    }

    [Fact]
    public async Task Media_failure_does_not_lose_the_message()
    {
        var handler = new FakeGraphHandler(
            """{ "error": { "code": 100, "message": "media not found" } }""",
            Array.Empty<byte>(),
            HttpStatusCode.NotFound);
        var (db, job, store) = Build(handler);

        await job.ProcessAsync(ImagePayload());

        // Mesaj kaydedilir: medya ikincil veri, hata Hangfire retry'ına yol açmamalı.
        var msg = db.WaMessages.Single();
        msg.Type.Should().Be("image");
        msg.Body.Should().Be("kargo etiketi");
        msg.MediaR2Key.Should().BeNull();
        msg.MediaMimeType.Should().Be("image/jpeg", "webhook'tan gelen mime korunur");
        store.Count.Should().Be(0);
    }
}
