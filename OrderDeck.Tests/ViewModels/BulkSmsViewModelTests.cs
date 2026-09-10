using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// <see cref="BulkSmsViewModel.SendCoreAsync"/> — N06: Create başarısı ile
/// sonrasındaki durum yoklaması ayrı hata alanları olmalı. Eski kodda tek
/// try/catch, yoklama hatasında "Gönderim başarısız" + dolu form + aktif
/// Gönder düğmesi bırakıyordu; anahtar sıfırlandığı için ikinci tıklama yeni
/// ClientRequestId ile İKİNCİ kampanyayı açıyordu.
/// </summary>
public sealed class BulkSmsViewModelTests
{
    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = TestLicenseKey;
    }

    private static readonly Guid TestLicenseId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string TestLicenseKey = "LDK-TEST-FIXTURE";

    private static string LicensesJson() =>
        $@"[{{ ""id"": ""{TestLicenseId}"", ""licenseKey"": ""{TestLicenseKey}"",
            ""skuCode"": ""STD"", ""expiresAt"": ""2030-01-01T00:00:00Z"" }}]";

    private static string CreateJson(Guid campaignId) =>
        $@"{{ ""campaignId"": ""{campaignId}"", ""recipientCount"": 5, ""totalCredits"": 5 }}";

    private static string StatusJson(Guid campaignId, string status) =>
        $@"{{ ""campaignId"": ""{campaignId}"", ""status"": ""{status}"",
            ""recipientCount"": 5, ""sent"": 5, ""failed"": 0, ""skipped"": 0,
            ""creditsRefunded"": 0, ""createdAt"": ""2026-09-10T10:00:00Z"",
            ""completedAt"": ""2026-09-10T10:00:05Z"" }}";

    private static (BulkSmsViewModel Vm, List<(HttpMethod Method, string Path, string? Body)> Requests) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<(HttpMethod, string, string?)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((req.Method, req.RequestUri!.PathAndQuery, body));
            return responder(req);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());

        var vm = new BulkSmsViewModel(api, new StubLicenseProvider())
        {
            StatusPollDelay = TimeSpan.FromMilliseconds(1),
        };
        // Önizleme yapılmış gibi kur (SendCore, MessageBox onayı sonrası gövde).
        vm.MessageBody = "Yarın 21:00'de canlı yayındayız!";
        vm.PreviewDone = true;
        vm.Sufficient = true;
        vm.RecipientCount = 5;
        vm.TotalCredits = 5;
        return (vm, requests);
    }

    private static Guid? RequestIdOf(string? createBody)
    {
        using var doc = JsonDocument.Parse(createBody!);
        return doc.RootElement.TryGetProperty("clientRequestId", out var v)
               && v.ValueKind == JsonValueKind.String
            ? v.GetGuid() : null;
    }

    /// <summary>N06 çekirdeği: Create BAŞARILI, sonraki durum yoklaması
    /// patlıyor. Form gönderilebilir durumda kalmamalı ("tekrar Gönder =
    /// ikinci kampanya" yolu) ve hata metni gönderimin başarısız olduğunu
    /// İMA ETMEMELİ — kampanya açıldı, SMS'ler arka planda gidiyor.</summary>
    [Fact]
    public async Task SendCore_izleme_hatasi_formu_gonderilebilir_birakmaz()
    {
        var campaignId = Guid.NewGuid();
        var (vm, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (req.Method == HttpMethod.Post && path.EndsWith("/sms-campaigns"))
                return FakeHttpMessageHandler.Json(200, CreateJson(campaignId));
            // Durum yoklaması: sunucu 500 — izleme hatası.
            return FakeHttpMessageHandler.Json(500, "{}");
        });

        await vm.SendCoreAsync();

        requests.Count(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/sms-campaigns"))
            .Should().Be(1, "kampanya bir kez oluşturuldu");

        vm.MessageBody.Should().BeEmpty("kampanya açıldı — form temizlenmeli");
        vm.PreviewDone.Should().BeFalse();
        vm.SendCommand.CanExecute(null).Should().BeFalse(
            "izleme hatası sonrası Gönder yeniden aktifleşirse ikinci kampanya açılır");

        vm.ErrorMessage.Should().NotBeNull();
        vm.ErrorMessage.Should().Contain("izlenemedi",
            "hata izlemeye ait — gönderim başarısız değil");
        vm.ErrorMessage.Should().NotContain("Gönderim başarısız",
            "bu metin operatörü tekrar göndermeye iter");
    }

    /// <summary>F09 kilidi: Create'in KENDİSİ patlarsa anahtar saklanır ve
    /// form dokunulmadan kalır — ikinci deneme aynı ClientRequestId ile
    /// gider, sunucu ikinci kampanya açmaz.</summary>
    [Fact]
    public async Task SendCore_create_hatasi_anahtari_ve_formu_korur()
    {
        var (vm, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            return FakeHttpMessageHandler.Json(500, "{}");
        });

        await vm.SendCoreAsync();
        vm.ErrorMessage.Should().Contain("Gönderim başarısız");
        vm.MessageBody.Should().NotBeEmpty("kampanya açılmadı — form kalmalı");

        await vm.SendCoreAsync();

        var createBodies = requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/sms-campaigns"))
            .Select(r => RequestIdOf(r.Body))
            .ToList();
        createBodies.Should().HaveCount(2);
        createBodies[0].Should().NotBeNull();
        createBodies[1].Should().Be(createBodies[0],
            "tekrar deneme aynı idempotency anahtarını taşımalı (F09)");
    }

    [Fact]
    public async Task SendCore_basarili_yol_formu_temizler_ve_bakiyeyi_yeniler()
    {
        var campaignId = Guid.NewGuid();
        var (vm, _) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (req.Method == HttpMethod.Post && path.EndsWith("/sms-campaigns"))
                return FakeHttpMessageHandler.Json(200, CreateJson(campaignId));
            if (path.Contains($"/sms-campaigns/{campaignId}"))
                return FakeHttpMessageHandler.Json(200, StatusJson(campaignId, "completed"));
            if (path.Contains("/sms/balance"))
                return FakeHttpMessageHandler.Json(200,
                    @"{ ""creditsRemaining"": 37, ""updatedAt"": ""2026-09-10T10:00:06Z"" }");
            if (path.Contains("/sms-campaigns?"))
                return FakeHttpMessageHandler.Json(200, "[]");
            return FakeHttpMessageHandler.Json(404, "{}");
        });

        await vm.SendCoreAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.MessageBody.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("Tamamlandı");
        vm.CreditsRemaining.Should().Be(37);
    }
}
