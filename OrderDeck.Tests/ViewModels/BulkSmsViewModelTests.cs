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
using OrderDeck.Licensing.Api.Models;
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
    public async Task SendCore_basarili_yol_formu_temizler_ve_gecmisi_yeniler()
    {
        var campaignId = Guid.NewGuid();
        var (vm, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (req.Method == HttpMethod.Post && path.EndsWith("/sms-campaigns"))
                return FakeHttpMessageHandler.Json(200, CreateJson(campaignId));
            if (path.Contains($"/sms-campaigns/{campaignId}"))
                return FakeHttpMessageHandler.Json(200, StatusJson(campaignId, "completed"));
            if (path.Contains("/sms-campaigns?"))
                return FakeHttpMessageHandler.Json(200, "[]");
            return FakeHttpMessageHandler.Json(404, "{}");
        });

        await vm.SendCoreAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.MessageBody.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("Tamamlandı");
        // Kredi emekli (§1.4b): başarı yolunda bakiye ucu HİÇ çağrılmaz.
        requests.Should().NotContain(r => r.Path.Contains("/sms/balance"),
            "kredi sistemi emekli — istemci bakiye ucunu bilmemeli");
    }

    /// <summary>§1.4b: açılış yalnız lisans çözer + geçmişi yükler. Bakiye
    /// ucu istemciden tamamen söküldü — çağrı listesinde görünmemeli.</summary>
    [Fact]
    public async Task LoadAsync_bakiye_cagirmaz_gecmisi_yukler()
    {
        var (vm, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/sms-campaigns?"))
                return FakeHttpMessageHandler.Json(200,
                    $@"[{{ ""campaignId"": ""{Guid.NewGuid()}"", ""status"": ""completed"",
                        ""messagePreview"": ""selam"", ""recipientCount"": 3, ""sent"": 3,
                        ""failed"": 0, ""skipped"": 0, ""creditsRefunded"": 0,
                        ""createdAt"": ""2026-09-10T10:00:00Z"",
                        ""completedAt"": ""2026-09-10T10:00:05Z"" }}]");
            return FakeHttpMessageHandler.Json(404, "{}");
        });

        await vm.LoadAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.History.Should().HaveCount(1, "geçmiş bakiyesiz de yüklenmeli");
        requests.Should().NotContain(r => r.Path.Contains("/sms/balance"),
            "kredi sistemi emekli — açılışta bakiye çağrısı olmamalı");
    }

    /// <summary>§1.4b: server `Sufficient` alanını "doğrulanmış NetgsmAccount
    /// var mı" anlamıyla dolduruyor. false → mesaj krediye değil Netgsm
    /// kurulumuna işaret etmeli ve Gönder kapalı kalmalı. Yanıttaki
    /// `creditsRemaining` alanı bilinmeyen JSON alanı olarak yok sayılır.</summary>
    [Fact]
    public async Task Preview_kurulum_dogrulanmamis_gonderimi_kapatir()
    {
        var (vm, _) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (req.Method == HttpMethod.Post && path.EndsWith("/sms-campaigns/preview"))
                return FakeHttpMessageHandler.Json(200,
                    @"{ ""recipientCount"": 5, ""segmentsPerMessage"": 1,
                        ""totalCredits"": 5, ""creditsRemaining"": 0, ""sufficient"": false }");
            return FakeHttpMessageHandler.Json(404, "{}");
        });

        await vm.PreviewCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Contain("Netgsm kurulumu doğrulanmamış",
            "mesaj krediye değil kurulum eksiğine işaret etmeli");
        vm.SendCommand.CanExecute(null).Should().BeFalse(
            "kurulum doğrulanmadan gönderim kapalı");
    }

    /// <summary>§3.3: İYS reddi yüzünden atlanan alıcılar geçmiş satırında
    /// görünmeli; kredi iadesi etiketi DTO ile birlikte gitti.</summary>
    [Fact]
    public void CampaignRow_skipped_varsa_atlandi_etiketi_ekler()
    {
        var d = new SmsCampaignListItem(
            Guid.NewGuid(), "completed", "selam", 10, 7, 1, 2,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        BulkSmsViewModel.CampaignRow.From(d).CountsLabel
            .Should().Contain("2 atlandı (İYS)");
    }

    [Fact]
    public void CampaignRow_skipped_sifirsa_atlandi_etiketi_yok()
    {
        var d = new SmsCampaignListItem(
            Guid.NewGuid(), "completed", "selam", 10, 10, 0, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        BulkSmsViewModel.CampaignRow.From(d).CountsLabel
            .Should().NotContain("atlandı");
    }

    /// <summary>§3.4: hesap hatasında server kampanyayı `paused` bırakır;
    /// etiket İngilizce durum kodu yerine Türkçe görünmeli.</summary>
    [Fact]
    public void StatusLabel_paused_Duraklatildi()
        => BulkSmsViewModel.StatusLabel("paused").Should().Be("Duraklatıldı");
}
