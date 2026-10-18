using System.Net;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Licensing;

/// <summary>
/// <c>POST sales/commands</c>'ın tel sözleşmesi (spec 2026-10-13 §4.6; W2-04): zarf ve on türün yükü sunucudaki kayıtların
/// alan adlarıyla, 200 yanıtı ve sonucu tipli, 400/409 ProblemDetails ek üyeleriyle fırlatmadan, 404 uç yok, ağ hatası ve
/// zaman aşımı çevrimdışı, 5xx ve bozuk gövde başarısız. Sunucu tarafı ad listeleri
/// <c>Services/Sync/Commands/*CommandHandler.cs</c>'teki <c>Body</c> kayıtlarından.
/// </summary>
public sealed class SalesCommandClientApiTests
{
    private static readonly Guid License = Guid.NewGuid();

    private static LicenseApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://test.local") },
            new LicenseTokenStore());

    private static SalesCommandRequest Request(Guid? commandId = null) => new(
        commandId ?? Guid.NewGuid(), Guid.NewGuid(), 2, 1, SalesCommandKinds.Cancel,
        JsonDocument.Parse(new SalesCancelPayload([Guid.NewGuid()], "musteri").ToWireJson()).RootElement.Clone(),
        [new SalesCommandExpected(Guid.NewGuid(), 3)]);

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();

    // ── istek ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SalesCommandClient_api_zarfi_sunucunun_alan_adlariyla_ve_dogru_uca_gonderir()
    {
        string? body = null;
        string? path = null;
        var client = Client(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Problem(409, "mode-off");
        });
        var request = Request();

        await client.PostSalesCommandAsync(License, request);

        path.Should().Be($"/api/v1/licenses/{License}/sales/commands");
        var json = JsonDocument.Parse(body!).RootElement;
        Names(json).Should().BeEquivalentTo("commandId", "replicaId", "protocol", "seenMode", "kind", "payload", "expected");
        json.GetProperty("commandId").GetGuid().Should().Be(request.CommandId);
        json.GetProperty("replicaId").GetGuid().Should().Be(request.ReplicaId);
        json.GetProperty("protocol").GetInt32().Should().Be(2);
        json.GetProperty("seenMode").GetInt32().Should().Be(1);
        json.GetProperty("kind").GetString().Should().Be("cancel");
        json.GetProperty("payload").GetRawText().Should().Be(request.Payload.GetRawText(), "yük aynen gider");
        var expected = json.GetProperty("expected").EnumerateArray().Single();
        Names(expected).Should().BeEquivalentTo("orderId", "version");
        expected.GetProperty("version").GetInt32().Should().Be(3);
    }

    private static SalesCommandPayload PayloadOf(string kind)
    {
        var id = Guid.NewGuid();
        return kind switch
        {
            "cancel" => new SalesCancelPayload([id], "musteri"),
            "uncancel" => new SalesUncancelPayload([id]),
            "set-code-price" => new SalesSetCodePricePayload(id, "A1", [id], 120m, []),
            "confirm-backup" => new SalesConfirmBackupPayload(id, 90m),
            "add-backup" => new SalesAddBackupPayload(id, Guid.NewGuid(),
                new SalesAddBackupCustomer("ham", "tiktok", "alici_ornek", null), "", null, null),
            "mark-printed" => new SalesMarkPrintedPayload("b-1", [new SalesMarkPrintedItem(id, 1)]),
            "reprint" => new SalesReprintPayload([id]),
            "attach-shipment" => new SalesAttachShipmentPayload("ham", id),
            "shipment-decision" => new SalesShipmentDecisionPayload(id, SalesShipmentDecisionPayload.Hold, 4),
            "force-end" => new SalesForceEndPayload(id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [InlineData("cancel", new[] { "items", "reason" })]
    [InlineData("uncancel", new[] { "items" })]
    [InlineData("set-code-price", new[] { "sessionId", "code", "alsoSelectedIds", "newPrice", "paidFromBalanceIds" })]
    [InlineData("confirm-backup", new[] { "backupOrderId", "newPrice" })]
    [InlineData("add-backup", new[] { "newOrderId", "parentOrderId", "customer", "messageText", "price", "code" })]
    [InlineData("mark-printed", new[] { "batchId", "items" })]
    [InlineData("reprint", new[] { "orderIds" })]
    [InlineData("attach-shipment", new[] { "customerId", "proposedShipmentId" })]
    [InlineData("shipment-decision", new[] { "shipmentId", "decision", "expectedVersion" })]
    [InlineData("force-end", new[] { "sessionId", "confirm" })]
    public void SalesCommandClient_api_on_turun_yuku_sunucunun_Body_kaydinin_alan_adlarini_tasir_tur_alani_yok(
        string kind, string[] names)
    {
        var payload = PayloadOf(kind);
        payload.Kind.Should().Be(kind);
        Names(JsonDocument.Parse(payload.ToWireJson()).RootElement).Should().BeEquivalentTo(names,
            "sunucu yükü kendi Body kaydıyla okur; ad kayması 400 invalid-payload olurdu");
    }

    [Fact]
    public void SalesCommandClient_api_ic_ice_yuk_alanlari_ve_force_end_onayi()
    {
        var item = JsonDocument.Parse(new SalesMarkPrintedPayload("b-1", [new SalesMarkPrintedItem(Guid.NewGuid(), 2)])
            .ToWireJson()).RootElement.GetProperty("items")[0];
        Names(item).Should().BeEquivalentTo("orderId", "seq");

        var customer = JsonDocument.Parse(new SalesAddBackupPayload(Guid.NewGuid(), Guid.NewGuid(),
                new SalesAddBackupCustomer("ham", "tiktok", "alici_ornek", null), "", null, null).ToWireJson())
            .RootElement.GetProperty("customer");
        Names(customer).Should().BeEquivalentTo("id", "platform", "username", "displayName");

        JsonDocument.Parse(new SalesForceEndPayload(Guid.NewGuid()).ToWireJson()).RootElement
            .GetProperty("confirm").GetBoolean().Should().BeTrue("sunucu confirm = true ister");
    }

    // ── 200 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SalesCommandClient_api_200_yaniti_satirlari_ve_ture_ozgu_sonucu_tipli_cozer()
    {
        var order = Guid.NewGuid();
        var session = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"status":"applied",
             "orders":[{"id":"{{order}}","sessionId":"{{session}}","customerId":"ham","platform":"tiktok",
                        "username":"alici_ornek","displayName":null,"messageText":"","code":"A1","price":150,
                        "addedAt":"2026-10-10T18:01:00+00:00","printedAt":"2026-10-10T20:05:00+00:00",
                        "cancelledAt":null,"cancelReason":null,"isShippingFee":false,"isBackupPromoted":false,
                        "isTentativeBackup":false,"productId":null,"productVariantId":null,
                        "updatedAt":"2026-10-10T20:05:00+00:00","version":4,"parentOrderId":null,"shipmentId":null,
                        "originReplicaId":null,"printBatchId":"b-1","printSeq":1,"priceCorrectedAt":null,
                        "paidPrice":null,"sessionReleasedAt":"2026-10-10T20:00:00+00:00","priceSetAt":null,
                        "cancelSetAt":null,"backupSetAt":null,"shipmentSetAt":null,"textScrubbedAt":null,
                        "changeSeq":77}],
             "shipments":[],
             "sessions":[{"id":"{{session}}","title":"Akşam","startedAt":"2026-10-10T18:00:00+00:00",
                          "endedAt":"2026-10-10T20:00:00+00:00","platforms":"tiktok","notes":null,
                          "updatedAt":"2026-10-10T20:00:00+00:00","ownerReplicaId":null,"ownerMachineName":"PC-B",
                          "forceEndedBy":null,"releasedAt":"2026-10-10T20:00:00+00:00","unsentLabelCount":null,
                          "changeSeq":70}],
             "result":{"changed":["{{order}}"],"skipped":[],"orders":[{"id":"{{order}}","version":4}],"shipments":[],
                       "marked":["{{order}}"],"alreadyPrinted":[],"cancelled":[]} }
            """));

        var result = await client.PostSalesCommandAsync(License, Request());

        result.Transport.Should().Be(SalesCommandTransport.Ok);
        result.StatusCode.Should().Be(200);
        var response = result.Response!;
        response.Status.Should().Be(SalesCommandResponse.StatusApplied);
        response.Orders.Should().ContainSingle().Which.Version.Should().Be(4);
        response.Sessions.Should().ContainSingle().Which.OwnerMachineName.Should().Be("PC-B");
        response.Result.Changed.Should().Equal(order);
        response.Result.Orders.Should().ContainSingle().Which.Should().Be(new SalesCommandOrderVersion(order, 4));
        response.Result.Marked.Should().Equal(order);
        response.Result.AlreadyPrinted.Should().BeEmpty();
        response.Result.ReprintIds.Should().BeNull("türe özgü liste yalnız o türde dolu");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"status":"applied","orders":[],"shipments":[],"sessions":[]}""")]
    [InlineData("""{"status":"applied","orders":[null],"shipments":[],"sessions":[],"result":{"changed":[],"skipped":[],"orders":[],"shipments":[]}}""")]
    [InlineData("<html>bakım</html>")]
    public async Task SalesCommandClient_api_bozuk_200_govdesi_basarisiz_sayilir_firlatmaz(string body)
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(200, body));

        var result = await client.PostSalesCommandAsync(License, Request());

        result.Transport.Should().Be(SalesCommandTransport.Failed, "komut uygulanmış olabilir; aynı niyet aynen yeniden gider");
        result.Response.Should().BeNull();
    }

    // ── ret ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SalesCommandClient_api_409_ProblemDetails_ek_uyelerini_firlatmadan_cozer()
    {
        var session = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"session-live","status":409,
             "detail":"Bu yayın henüz sunucuya devredilmedi; yalnız onu açan bilgisayar değiştirebilir.",
             "machine":"PC-A","ownerLastSeenAt":"2026-10-10T21:14:00+00:00","sessionId":"{{session}}",
             "traceId":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}
            """));

        var result = await client.PostSalesCommandAsync(License, Request());

        result.Transport.Should().Be(SalesCommandTransport.Problem);
        result.StatusCode.Should().Be(409);
        var problem = result.Problem!;
        problem.Code.Should().Be(SalesCommandProblemCodes.SessionLive);
        problem.Machine.Should().Be("PC-A");
        problem.OwnerLastSeenAt.Should().Be(DateTimeOffset.Parse("2026-10-10T21:14:00+00:00"));
        problem.SessionId.Should().Be(session);
    }

    [Fact]
    public async Task SalesCommandClient_api_stale_ve_plan_stale_guncel_satirlari_ve_plani_tasir()
    {
        var order = Guid.NewGuid();
        var shipment = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"title":"plan-stale","status":409,"detail":"x",
             "plan":[{"orderId":"{{order}}","version":6}],
             "current":[{"id":"{{order}}","sessionId":null,"customerId":"ham","platform":"tiktok","username":"alici_ornek",
                         "displayName":null,"messageText":"","code":"A1","price":90,"addedAt":"2026-10-10T18:01:00+00:00",
                         "printedAt":null,"cancelledAt":null,"cancelReason":null,"isShippingFee":false,
                         "isBackupPromoted":false,"isTentativeBackup":false,"productId":null,"productVariantId":null,
                         "updatedAt":"2026-10-10T20:05:00+00:00","version":6,"parentOrderId":null,"shipmentId":null,
                         "originReplicaId":null,"printBatchId":null,"printSeq":null,"priceCorrectedAt":null,
                         "paidPrice":null,"sessionReleasedAt":null,"priceSetAt":null,"cancelSetAt":null,
                         "backupSetAt":null,"shipmentSetAt":null,"textScrubbedAt":null,"changeSeq":80}],
             "currentShipments":[{"id":"{{shipment}}","customerId":"ham","status":"held","cumulativeAmount":90,
                                  "createdAt":"2026-10-10T18:01:00+00:00","heldAt":null,"shippedAt":null,
                                  "updatedAt":"2026-10-10T20:05:00+00:00","version":2,"amountDerivedAt":null,
                                  "commandedAt":null,"originReplicaId":null,"changeSeq":81}],
             "count":12001,"max":10000,"unsentLabelCount":3,"lastSeenAt":"2026-10-10T21:00:00+00:00",
             "orderId":"{{order}}","shipmentId":"{{shipment}}","orderIds":["{{order}}"]}
            """));

        var problem = (await client.PostSalesCommandAsync(License, Request())).Problem!;

        problem.Code.Should().Be(SalesCommandProblemCodes.PlanStale);
        problem.Plan.Should().Equal(new SalesCommandExpected(order, 6));
        problem.Current.Should().ContainSingle().Which.Version.Should().Be(6);
        problem.CurrentShipments.Should().ContainSingle().Which.Version.Should().Be(2);
        problem.Count.Should().Be(12001);
        problem.Max.Should().Be(10000);
        problem.UnsentLabelCount.Should().Be(3);
        problem.LastSeenAt.Should().Be(DateTimeOffset.Parse("2026-10-10T21:00:00+00:00"));
        problem.OrderId.Should().Be(order);
        problem.ShipmentId.Should().Be(shipment);
        problem.OrderIds.Should().Equal(order);
    }

    [Fact]
    public async Task SalesCommandClient_api_400_basligi_problem_olarak_doner()
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(400, "invalid-payload", "payload okunamadı."));

        var result = await client.PostSalesCommandAsync(License, Request());

        result.Transport.Should().Be(SalesCommandTransport.Problem);
        result.Problem!.Code.Should().Be(SalesCommandProblemCodes.InvalidPayload);
        result.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData(404, SalesCommandTransport.EndpointMissing)]
    [InlineData(500, SalesCommandTransport.Failed)]
    [InlineData(503, SalesCommandTransport.Failed)]
    [InlineData(429, SalesCommandTransport.Failed)]
    public async Task SalesCommandClient_api_durum_kodlari_eslenir(int status, SalesCommandTransport expected)
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(status, "fake-fault"));

        var result = await client.PostSalesCommandAsync(License, Request());

        result.Transport.Should().Be(expected);
        result.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task SalesCommandClient_api_basliksiz_409_basarisizdir()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("<html>proxy</html>"),
        });

        (await client.PostSalesCommandAsync(License, Request())).Transport.Should().Be(SalesCommandTransport.Failed);
    }

    [Fact]
    public async Task SalesCommandClient_api_ag_hatasi_ve_zaman_asimi_cevrimdisidir_iptal_firlatir()
    {
        var offline = Client(_ => throw new HttpRequestException("fake offline"));
        var timeout = Client(_ => throw new TaskCanceledException("fake timeout"));

        (await offline.PostSalesCommandAsync(License, Request())).Transport.Should().Be(SalesCommandTransport.Offline);
        (await timeout.PostSalesCommandAsync(License, Request())).Transport.Should().Be(SalesCommandTransport.Offline);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = Client(_ => throw new TaskCanceledException("iptal"));
        await FluentActions.Awaiting(() => cancelled.PostSalesCommandAsync(License, Request(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
