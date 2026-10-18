using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Licensing;

/// <summary>
/// Ödeme isteği ve kargo ayarı uçlarının tel sözleşmesi (spec 2026-10-13 §4.7, §4.8, §6.4, §6.6; PR #523'ün onay özeti;
/// W3-01). Gövdeler istemci DTO'su üzerinden değil ham JSON üzerinden denetlenir: alan adları sunucudaki kayıtlarla
/// (<c>Services/Sync/PaymentRequests/PaymentRequestContracts.cs</c>, <c>LicensesShippingSettingsController.PutRequest</c>,
/// <c>LicenseShippingSettingsStore.Snapshot</c>) birebir olmalı. Uçlar fırlatmaz: 200 tipli, 400/409 ProblemDetails ek
/// üyeleriyle, 404 uç yok, ağ hatası ve zaman aşımı çevrimdışı, 5xx ve bozuk gövde başarısız; yalnız iptal fırlar.
/// </summary>
public sealed class LicenseApiClientPaymentRequestTests
{
    private static readonly Guid License = Guid.NewGuid();
    private static readonly Guid Replica = Guid.NewGuid();

    private static (LicenseApiClient Client, FakeHttpMessageHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var client = new LicenseApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.local") }, new LicenseTokenStore());
        return (client, handler);
    }

    private static LicenseApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        Create(responder).Client;

    private static string Read(HttpRequestMessage req) => req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();

    private static string Member() => Guid.NewGuid().ToString("N");

    /// <summary>Sunucunun <c>priorsDigest</c> biçiminde (<c>v1.</c> + 32 baytın dolgusuz base64url'i) çalışma anında
    /// üretilmiş bir değer; testte sabit dize yok.</summary>
    private static string NewDigest() =>
        "v1." + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static PaymentRequestBeginRequest Begin(
        IReadOnlyList<Guid>? confirmed = null, string? digest = null, IReadOnlyList<string>? members = null)
    {
        var owner = Member();
        return new PaymentRequestBeginRequest(
            RecordId: Guid.NewGuid(),
            OwnerId: owner,
            MemberIds: members ?? [owner, "ham-yerel-id"],
            ScopeKey: "cumulative",
            OrderIds: [Guid.NewGuid(), Guid.NewGuid()],
            ProductTotal: 500m,
            TotalBeforeBalance: 550m,
            RecipientPays: false,
            ShippingSettingsVersion: 3,
            ConfirmedPriorIds: confirmed ?? [],
            ConfirmedPriorsDigest: digest,
            ReplicaId: Replica,
            Protocol: 2,
            SeenMode: 1);
    }

    private static PaymentRequestFinishRequest Finish() =>
        new("delivered", 450m, 100m, false, Replica, 2, 1);

    private static PaymentRequestBackfillItem BackfillItem(Guid? recordId = null)
    {
        var owner = Member();
        return new PaymentRequestBackfillItem(
            RecordId: recordId ?? Guid.NewGuid(),
            OwnerId: owner,
            MemberIds: [owner],
            ScopeKey: "legacy",
            OrderIds: [Guid.NewGuid()],
            State: PaymentRequestStates.Unconfirmed,
            NetAmount: 300m,
            AppliedAmount: null,
            FullyFromBalance: false,
            At: DateTimeOffset.Parse("2026-10-01T18:30:00+03:00"));
    }

    private static PaymentRequestBackfillRequest Backfill(params PaymentRequestBackfillItem[] items) =>
        new(items.Length == 0 ? [BackfillItem()] : items, Replica, 2, 0);

    private static ShippingSettingsPutRequest Put(decimal? threshold = 1000m, decimal? fee = 50m) =>
        new(4, threshold, fee, Replica, 2, 1);

    // ── begin: istek ──────────────────────────────────────────────────────

    [Fact]
    public async Task Begin_govdesi_sunucu_kaydinin_alan_adlarini_tasir_ozet_yokken_anahtar_hic_yazilmaz()
    {
        string? body = null;
        string? path = null;
        HttpMethod? method = null;
        var client = Client(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = Read(req);
            return FakeHttpMessageHandler.Problem(409, "mode-off");
        });
        var confirmed = Guid.NewGuid();
        var request = Begin(confirmed: [confirmed]);

        await client.PostPaymentRequestBeginAsync(License, request);

        method.Should().Be(HttpMethod.Post);
        path.Should().Be($"/api/v1/licenses/{License}/payment-requests/begin");
        var json = JsonDocument.Parse(body!).RootElement;
        Names(json).Should().BeEquivalentTo(
            ["recordId", "ownerId", "memberIds", "scopeKey", "orderIds", "productTotal", "totalBeforeBalance",
             "recipientPays", "shippingSettingsVersion", "confirmedPriorIds", "replicaId", "protocol", "seenMode"],
            "sunucu PaymentRequestBeginRequest'i bu adlarla okur");
        json.TryGetProperty("confirmedPriorsDigest", out _).Should().BeFalse("özet yokken anahtar telde yazılmaz");
        json.TryGetProperty("confirmedThrough", out _).Should().BeFalse("zaman filigranı terk edildi (#523)");
        json.GetProperty("recordId").GetGuid().Should().Be(request.RecordId);
        json.GetProperty("ownerId").GetString().Should().Be(request.OwnerId);
        json.GetProperty("memberIds").EnumerateArray().Select(m => m.GetString()).Should()
            .Equal(request.MemberIds, "GUID'siz ham Id de aynen gider");
        json.GetProperty("scopeKey").GetString().Should().Be("cumulative");
        json.GetProperty("orderIds").EnumerateArray().Select(o => o.GetGuid()).Should().Equal(request.OrderIds);
        json.GetProperty("productTotal").GetDecimal().Should().Be(500m);
        json.GetProperty("totalBeforeBalance").GetDecimal().Should().Be(550m);
        json.GetProperty("recipientPays").GetBoolean().Should().BeFalse();
        json.GetProperty("shippingSettingsVersion").GetInt32().Should().Be(3);
        json.GetProperty("confirmedPriorIds").EnumerateArray().Select(o => o.GetGuid()).Should().Equal(confirmed);
        json.GetProperty("replicaId").GetGuid().Should().Be(Replica);
        json.GetProperty("protocol").GetInt32().Should().Be(2);
        json.GetProperty("seenMode").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Begin_onay_ozeti_doluyken_aynen_yankilanir_bos_onay_listesiyle_birlikte()
    {
        string? body = null;
        var client = Client(req =>
        {
            body = Read(req);
            return FakeHttpMessageHandler.Problem(409, "mode-off");
        });
        var digest = NewDigest();

        await client.PostPaymentRequestBeginAsync(License, Begin(digest: digest));

        var json = JsonDocument.Parse(body!).RootElement;
        json.GetProperty("confirmedPriorsDigest").GetString().Should().Be(digest, "409'un priorsDigest'i aynen geri gider");
        json.GetProperty("confirmedPriorIds").GetArrayLength().Should().Be(0, "liste boş olabilir; bütün kümeyi özet onaylar");
        json.TryGetProperty("confirmedThrough", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Begin_500_onay_Idsi_gider_501_istek_atmadan_firlar()
    {
        var (client, handler) = Create(_ => FakeHttpMessageHandler.Problem(409, "mode-off"));
        var full = Enumerable.Range(0, PaymentRequestLimits.MaxConfirmedPriorIds).Select(_ => Guid.NewGuid()).ToList();

        await client.PostPaymentRequestBeginAsync(License, Begin(confirmed: full));
        handler.Requests.Should().HaveCount(1, "500 sınırın içinde");

        var over = full.Append(Guid.NewGuid()).ToList();
        await FluentActions.Awaiting(() => client.PostPaymentRequestBeginAsync(License, Begin(confirmed: over)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>("sunucu 400 invalid-confirmed-priors verirdi; fazlası gönderilmez");
        handler.Requests.Should().HaveCount(1, "501 Id ile HTTP isteği atılmaz");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Begin_ve_liste_uye_sayisi_1_100_disinda_istek_atmadan_firlar(int count)
    {
        var (client, handler) = Create(_ => FakeHttpMessageHandler.Problem(409, "mode-off"));
        var members = Enumerable.Range(0, count).Select(_ => Member()).ToList();

        await FluentActions.Awaiting(() => client.PostPaymentRequestBeginAsync(License, Begin(members: members)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => client.GetPaymentRequestsAsync(License, "cumulative", members))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => client.PostPaymentRequestBackfillAsync(License,
                Backfill(BackfillItem() with { MemberIds = members })))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Begin_100_uye_sinirin_icinde_gider()
    {
        var (client, handler) = Create(_ => FakeHttpMessageHandler.Problem(409, "mode-off"));
        var members = Enumerable.Range(0, PaymentRequestLimits.MaxMembers).Select(_ => Member()).ToList();

        await client.PostPaymentRequestBeginAsync(License, Begin(members: members));

        handler.Requests.Should().HaveCount(1);
    }

    // ── begin: yanıt ──────────────────────────────────────────────────────

    [Fact]
    public async Task Begin_200_kaydin_guncel_halini_ve_sunucu_uyelerini_tipli_cozer()
    {
        var request = Begin();
        var client = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"recordId":"{{request.RecordId}}","state":"sending","leaseUntil":"2026-10-12T19:02:00+00:00",
             "members":["{{request.OwnerId}}","ham-yerel-id","t2"]}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, request);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.StatusCode.Should().Be(200);
        result.Response!.RecordId.Should().Be(request.RecordId);
        result.Response.State.Should().Be(PaymentRequestStates.Sending);
        result.Response.LeaseUntil.Should().Be(DateTimeOffset.Parse("2026-10-12T19:02:00+00:00"));
        result.Response.Members.Should().Equal(request.OwnerId, "ham-yerel-id", "t2");
        result.Problem.Should().BeNull();
    }

    [Fact]
    public async Task Begin_409_already_requested_oncul_listesini_ve_ozeti_tasir()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var digest = NewDigest();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"already-requested","status":409,
             "detail":"Bu kişiden bu kapsamda daha önce ödeme istendi; tekrar gönderilsin mi?",
             "priors":[{"id":"{{first}}","machine":"PC-A","netAmount":450.00,"totalBeforeBalance":null,
                        "at":"2026-10-10T20:10:00+00:00","finishedAt":"2026-10-10T20:10:05+00:00","unconfirmed":false},
                       {"id":"{{second}}","machine":null,"netAmount":null,"totalBeforeBalance":550,
                        "at":"2026-10-11T19:00:00+00:00","finishedAt":"2026-10-11T19:02:00+00:00","unconfirmed":true}],
             "priorsDigest":"{{digest}}",
             "traceId":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.StatusCode.Should().Be(409);
        var problem = result.Problem!;
        problem.Code.Should().Be(PaymentRequestProblemCodes.AlreadyRequested);
        problem.PriorsDigest.Should().Be(digest);
        problem.Priors.Should().Equal(
            new PaymentRequestPrior(first, "PC-A", 450m, null, DateTimeOffset.Parse("2026-10-10T20:10:00+00:00"),
                DateTimeOffset.Parse("2026-10-10T20:10:05+00:00"), false),
            new PaymentRequestPrior(second, null, null, 550m, DateTimeOffset.Parse("2026-10-11T19:00:00+00:00"),
                DateTimeOffset.Parse("2026-10-11T19:02:00+00:00"), true));
    }

    [Fact]
    public async Task Begin_409_already_requested_ozetsiz_eski_sunucuda_da_problemdir()
    {
        var prior = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"title":"already-requested","status":409,"detail":"x",
             "priors":[{"id":"{{prior}}","machine":"PC-B","netAmount":120,"totalBeforeBalance":null,
                        "at":"2026-10-10T20:10:00+00:00","finishedAt":null,"unconfirmed":false}]}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem, "#523 öncesi sunucu priorsDigest yazmaz");
        result.Problem!.PriorsDigest.Should().BeNull();
        result.Problem.Priors.Should().ContainSingle().Which.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Begin_409_members_stale_sunucu_grubunu_tasir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"title":"members-stale","status":409,"detail":"x","members":["t1","t2","ham-yerel-id"]}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.Code.Should().Be(PaymentRequestProblemCodes.MembersStale);
        result.Problem.Members.Should().Equal("t1", "t2", "ham-yerel-id");
    }

    [Fact]
    public async Task Begin_409_stale_total_sunucu_toplamini_ve_baska_bilgisayardaki_yayinlari_tasir()
    {
        var session = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"title":"stale-total","status":409,"detail":"x","serverTotal":480.50,
             "liveElsewhere":[{"sessionId":"{{session}}","machine":"PC-A"},{"sessionId":"{{other}}","machine":null}]}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        var problem = result.Problem!;
        problem.Code.Should().Be(PaymentRequestProblemCodes.StaleTotal);
        problem.ServerTotal.Should().Be(480.50m);
        problem.LiveElsewhere.Should().Equal(
            new PaymentRequestLiveSession(session, "PC-A"), new PaymentRequestLiveSession(other, null));
    }

    [Fact]
    public async Task Begin_409_stale_total_liveElsewhere_null_iken_null_kalir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"title":"stale-total","status":409,"detail":"x","serverTotal":0,"liveElsewhere":null}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.ServerTotal.Should().Be(0m);
        result.Problem.LiveElsewhere.Should().BeNull("fark başka kopyanın devredilmemiş yayınıyla açıklanmıyor");
    }

    [Fact]
    public async Task Begin_409_settings_stale_guncel_kargo_ayarini_tasir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"title":"settings-stale","status":409,"detail":"x",
             "current":{"version":4,"freeShippingThreshold":1000.00,"fee":49.90,
                        "updatedAt":"2026-10-12T08:00:00+00:00","updatedBy":"cli"}}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.Code.Should().Be(PaymentRequestProblemCodes.SettingsStale);
        result.Problem.Current.Should().Be(new ShippingSettingsSnapshot(
            4, 1000m, 49.90m, DateTimeOffset.Parse("2026-10-12T08:00:00+00:00"), "cli"));
    }

    [Fact]
    public async Task Begin_409_in_progress_elsewhere_makineyi_anini_ve_kaydi_tasir()
    {
        var record = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"title":"in-progress-elsewhere","status":409,"detail":"x",
             "machine":"PC-A","since":"2026-10-12T19:00:00+00:00","recordId":"{{record}}"}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        var problem = result.Problem!;
        problem.Code.Should().Be(PaymentRequestProblemCodes.InProgressElsewhere);
        problem.Machine.Should().Be("PC-A");
        problem.Since.Should().Be(DateTimeOffset.Parse("2026-10-12T19:00:00+00:00"));
        problem.RecordId.Should().Be(record);
    }

    [Fact]
    public async Task Begin_409_in_progress_elsewhere_kilit_zaman_asiminda_bos_alanlarla_da_problemdir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"title":"in-progress-elsewhere","status":409,"detail":"x","machine":null,"since":null,"recordId":null}
            """));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.Machine.Should().BeNull();
        result.Problem.Since.Should().BeNull();
        result.Problem.RecordId.Should().BeNull();
    }

    [Theory]
    [InlineData(400, "invalid-priors-digest")]
    [InlineData(400, "invalid-confirmed-priors")]
    [InlineData(409, "mode-off")]
    [InlineData(409, "client-update-required")]
    [InlineData(409, "content-conflict")]
    public async Task Begin_ek_uyesiz_retler_problem_olarak_doner(int status, string code)
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(status, code, "x"));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin(digest: NewDigest()));

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.StatusCode.Should().Be(status);
        result.Problem!.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("""{"title":"already-requested","status":409,"priors":null}""")]
    [InlineData("""{"title":"already-requested","status":409,"priors":[null]}""")]
    [InlineData("""{"title":"members-stale","status":409}""")]
    [InlineData("""{"title":"members-stale","status":409,"members":["t1",null]}""")]
    [InlineData("""{"title":"stale-total","status":409,"liveElsewhere":null}""")]
    [InlineData("""{"title":"stale-total","status":409,"serverTotal":10,"liveElsewhere":[null]}""")]
    [InlineData("""{"title":"settings-stale","status":409}""")]
    [InlineData("""{"status":409,"detail":"başlıksız"}""")]
    [InlineData("<html>proxy</html>")]
    public async Task Begin_ek_uyesi_eksik_ya_da_basliksiz_ret_basarisizdir(string body)
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, body));

        var result = await client.PostPaymentRequestBeginAsync(License, Begin());

        result.Transport.Should().Be(PaymentRequestTransport.Failed, "eksik retle soru ya da yeniden deneme kurulamaz");
        result.Problem.Should().BeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"recordId":"RID","state":"sending","leaseUntil":null}""")]
    [InlineData("""{"recordId":"RID","state":"sending","leaseUntil":null,"members":["a",null]}""")]
    [InlineData("""{"recordId":"RID","leaseUntil":null,"members":["a"]}""")]
    [InlineData("""{"recordId":"00000000-0000-0000-0000-000000000001","state":"sending","members":["a"]}""")]
    [InlineData("<html>bakım</html>")]
    public async Task Begin_bozuk_200_basarisizdir(string body)
    {
        var request = Begin();
        var client = Client(_ => FakeHttpMessageHandler.Json(200, body.Replace("RID", request.RecordId.ToString())));

        var result = await client.PostPaymentRequestBeginAsync(License, request);

        result.Transport.Should().Be(PaymentRequestTransport.Failed, "kira yazılmış olabilir; aynı recordId güvenle yinelenir");
        result.Response.Should().BeNull();
    }

    // ── finish ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finish_govdesi_ve_yaniti_sunucu_kayitlariyla_eslesir()
    {
        var record = Guid.NewGuid();
        string? body = null;
        string? path = null;
        var client = Client(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            body = Read(req);
            return FakeHttpMessageHandler.Json(200, $$"""
                {"recordId":"{{record}}","state":"delivered","finishedAt":"2026-10-12T19:00:30+00:00"}
                """);
        });

        var result = await client.PostPaymentRequestFinishAsync(License, record, Finish());

        path.Should().Be($"/api/v1/licenses/{License}/payment-requests/{record}/finish");
        var json = JsonDocument.Parse(body!).RootElement;
        Names(json).Should().BeEquivalentTo(
            "outcome", "netAmount", "appliedAmount", "fullyFromBalance", "replicaId", "protocol", "seenMode");
        json.GetProperty("outcome").GetString().Should().Be("delivered");
        json.GetProperty("netAmount").GetDecimal().Should().Be(450m);
        json.GetProperty("appliedAmount").GetDecimal().Should().Be(100m);
        json.GetProperty("fullyFromBalance").GetBoolean().Should().BeFalse();
        json.GetProperty("replicaId").GetGuid().Should().Be(Replica);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.Response.Should().Be(new PaymentRequestFinishResponse(
            record, PaymentRequestStates.Delivered, DateTimeOffset.Parse("2026-10-12T19:00:30+00:00")));
    }

    [Fact]
    public async Task Finish_bilinmeyen_kayit_404_record_not_found_uc_yok_degil_problemdir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(404, "record-not-found", "x"));

        var result = await client.PostPaymentRequestFinishAsync(License, Guid.NewGuid(), Finish());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.StatusCode.Should().Be(404);
        result.Problem!.Code.Should().Be(PaymentRequestProblemCodes.RecordNotFound);
    }

    [Fact]
    public async Task Finish_baska_kaydin_yaniti_basarisizdir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"recordId":"{{Guid.NewGuid()}}","state":"delivered","finishedAt":null}
            """));

        var result = await client.PostPaymentRequestFinishAsync(License, Guid.NewGuid(), Finish());

        result.Transport.Should().Be(PaymentRequestTransport.Failed);
    }

    // ── liste ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Liste_GET_sorgusu_memberIds_anahtarini_yineler_ve_kacar()
    {
        HttpRequestMessage? captured = null;
        var client = Client(req =>
        {
            captured = req;
            return FakeHttpMessageHandler.Json(200, """{"records":[]}""");
        });
        var session = Guid.NewGuid().ToString("N");

        var result = await client.GetPaymentRequestsAsync(License, $"session:{session}", ["a", "b", "ham id/ş&x"]);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.Response!.Records.Should().BeEmpty();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.Content.Should().BeNull("GET gövde taşımaz");
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/v1/licenses/{License}/payment-requests");
        captured.RequestUri.Query.Should().Contain("memberIds=a&memberIds=b", "sunucu List<string> memberIds'i yinelenen anahtarla okur");
        var query = HttpUtility.ParseQueryString(captured.RequestUri.Query);
        query["scopeKey"].Should().Be($"session:{session}");
        query.GetValues("memberIds").Should().Equal("a", "b", "ham id/ş&x");
        captured.RequestUri.Query.Should().NotContain("ham id", "değerler kaçışlı");
    }

    [Fact]
    public async Task Liste_200_kayitlari_siparis_kumesiyle_tipli_cozer()
    {
        var id = Guid.NewGuid();
        var replica = Guid.NewGuid();
        var order = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"records":[{"id":"{{id}}","replicaId":"{{replica}}","machine":"PC-A","kind":"lease","state":"unconfirmed",
                         "netAmount":null,"appliedAmount":null,"fullyFromBalance":null,"productTotal":500,
                         "totalBeforeBalance":550,"startedAt":"2026-10-12T19:00:00+00:00",
                         "finishedAt":"2026-10-12T19:02:00+00:00","orderIds":["{{order}}"]}]}
            """));

        var result = await client.GetPaymentRequestsAsync(License, "cumulative", ["a"]);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        var item = result.Response!.Records.Should().ContainSingle().Subject;
        item.Should().Be(new PaymentRequestListItem(
            id, replica, "PC-A", PaymentRequestKinds.Lease, PaymentRequestStates.Unconfirmed, null, null, null, 500m, 550m,
            DateTimeOffset.Parse("2026-10-12T19:00:00+00:00"), DateTimeOffset.Parse("2026-10-12T19:02:00+00:00"),
            item.OrderIds));
        item.OrderIds.Should().Equal(order);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"records":[null]}""")]
    [InlineData("""{"records":[{"id":"00000000-0000-0000-0000-000000000001","replicaId":"00000000-0000-0000-0000-000000000002","kind":"record","state":"delivered","startedAt":"2026-10-12T19:00:00+00:00"}]}""")]
    public async Task Liste_bozuk_200_basarisizdir(string body)
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(200, body));

        var result = await client.GetPaymentRequestsAsync(License, "cumulative", ["a"]);

        result.Transport.Should().Be(PaymentRequestTransport.Failed, "boş liste 'kayıt yok' demektir; bozuk gövde onunla karışmaz");
    }

    // ── backfill ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_govdesi_ve_oge_sonuclari_sunucu_kayitlariyla_eslesir()
    {
        var a = BackfillItem();
        var b = BackfillItem() with { ScopeKey = "cumulative", State = PaymentRequestStates.Delivered, AppliedAmount = 20m };
        string? body = null;
        string? path = null;
        var client = Client(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            body = Read(req);
            return FakeHttpMessageHandler.Json(200, $$"""
                {"records":[{"recordId":"{{a.RecordId}}","status":"applied"},
                            {"recordId":"{{b.RecordId}}","status":"content-conflict"}]}
                """);
        });

        var result = await client.PostPaymentRequestBackfillAsync(License, Backfill(a, b));

        path.Should().Be($"/api/v1/licenses/{License}/payment-requests/backfill");
        var json = JsonDocument.Parse(body!).RootElement;
        Names(json).Should().BeEquivalentTo("records", "replicaId", "protocol", "seenMode");
        json.GetProperty("seenMode").GetInt32().Should().Be(0, "geri doldurma mod Off'ta da gider");
        var first = json.GetProperty("records")[0];
        Names(first).Should().BeEquivalentTo(
            "recordId", "ownerId", "memberIds", "scopeKey", "orderIds", "state", "netAmount", "appliedAmount",
            "fullyFromBalance", "at");
        first.GetProperty("scopeKey").GetString().Should().Be("legacy");
        first.GetProperty("state").GetString().Should().Be("unconfirmed");
        first.GetProperty("appliedAmount").ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("at").GetDateTimeOffset().Should().Be(a.At);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.Response!.Records.Should().Equal(
            new PaymentRequestBackfillResult(a.RecordId, PaymentRequestBackfillResult.StatusApplied),
            new PaymentRequestBackfillResult(b.RecordId, PaymentRequestBackfillResult.StatusContentConflict));
    }

    [Theory]
    [InlineData("""{"records":[]}""")]
    [InlineData("""{"records":[null]}""")]
    [InlineData("""{"records":[{"recordId":"OTHER","status":"applied"}]}""")]
    [InlineData("""{"records":[{"recordId":"RID"}]}""")]
    public async Task Backfill_istekle_hizalanmayan_sonuc_basarisizdir(string body)
    {
        var item = BackfillItem();
        var client = Client(_ => FakeHttpMessageHandler.Json(200,
            body.Replace("RID", item.RecordId.ToString()).Replace("OTHER", Guid.NewGuid().ToString())));

        var result = await client.PostPaymentRequestBackfillAsync(License, Backfill(item));

        result.Transport.Should().Be(PaymentRequestTransport.Failed, "sonuç öğe başına ve istekteki sırayla gelir");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Backfill_parti_1_200_disinda_istek_atmadan_firlar(int count)
    {
        var (client, handler) = Create(_ => FakeHttpMessageHandler.Json(200, """{"records":[]}"""));
        var request = new PaymentRequestBackfillRequest(
            Enumerable.Range(0, count).Select(_ => BackfillItem()).ToList(), Replica, 2, 0);

        await FluentActions.Awaiting(() => client.PostPaymentRequestBackfillAsync(License, request))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Requests.Should().BeEmpty();
    }

    // ── kargo ayarı ───────────────────────────────────────────────────────

    [Fact]
    public async Task Kargo_ayari_GET_anlik_goruntuyu_ve_satirsiz_hali_cozer()
    {
        string? path = null;
        var client = Client(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return FakeHttpMessageHandler.Json(200,
                """{"version":0,"freeShippingThreshold":null,"fee":null,"updatedAt":null,"updatedBy":null}""");
        });

        var result = await client.GetShippingSettingsAsync(License);

        path.Should().Be($"/api/v1/licenses/{License}/shipping-settings");
        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.Response.Should().Be(new ShippingSettingsSnapshot(0, null, null, null, null));
    }

    [Fact]
    public async Task Kargo_ayari_PUT_govdesi_sunucu_kaydiyla_eslesir_bos_degerler_de_yazilir()
    {
        string? body = null;
        HttpMethod? method = null;
        string? path = null;
        var updatedBy = Replica.ToString();
        var client = Client(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = Read(req);
            return FakeHttpMessageHandler.Json(200, $$"""
                {"version":5,"freeShippingThreshold":null,"fee":null,"updatedAt":"2026-10-12T09:00:00+00:00",
                 "updatedBy":"{{updatedBy}}"}
                """);
        });

        var result = await client.PutShippingSettingsAsync(License, Put(threshold: null, fee: null));

        method.Should().Be(HttpMethod.Put);
        path.Should().Be($"/api/v1/licenses/{License}/shipping-settings");
        var json = JsonDocument.Parse(body!).RootElement;
        Names(json).Should().BeEquivalentTo(
            "expectedVersion", "freeShippingThreshold", "fee", "replicaId", "protocol", "seenMode");
        json.GetProperty("expectedVersion").GetInt32().Should().Be(4);
        json.GetProperty("freeShippingThreshold").ValueKind.Should().Be(JsonValueKind.Null, "ikisi de boş = kargo kapalı");
        json.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("protocol").GetInt32().Should().Be(2);

        result.Transport.Should().Be(PaymentRequestTransport.Ok);
        result.Response.Should().Be(new ShippingSettingsSnapshot(
            5, null, null, DateTimeOffset.Parse("2026-10-12T09:00:00+00:00"), updatedBy));
    }

    [Fact]
    public async Task Kargo_ayari_PUT_409_stale_guncel_satiri_tasir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"title":"stale","status":409,"detail":"x",
             "current":{"version":6,"freeShippingThreshold":750,"fee":39.90,"updatedAt":"2026-10-12T09:30:00+00:00",
                        "updatedBy":"cli"}}
            """));

        var result = await client.PutShippingSettingsAsync(License, Put());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.Code.Should().Be(PaymentRequestProblemCodes.Stale);
        result.Problem.Current.Should().Be(new ShippingSettingsSnapshot(
            6, 750m, 39.90m, DateTimeOffset.Parse("2026-10-12T09:30:00+00:00"), "cli"));
    }

    [Theory]
    [InlineData(409, "mode-off")]
    [InlineData(400, "invalid-shipping")]
    [InlineData(400, "invalid-expected-version")]
    public async Task Kargo_ayari_PUT_ek_uyesiz_retler_problemdir(int status, string code)
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(status, code, "x"));

        var result = await client.PutShippingSettingsAsync(License, Put());

        result.Transport.Should().Be(PaymentRequestTransport.Problem);
        result.Problem!.Code.Should().Be(code);
    }

    [Fact]
    public async Task Kargo_ayari_PUT_current_tasimayan_stale_basarisizdir()
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(409, "stale", "x"));

        (await client.PutShippingSettingsAsync(License, Put())).Transport.Should().Be(PaymentRequestTransport.Failed);
    }

    // ── bütün uçlarda taşıma eşlemesi ─────────────────────────────────────

    public static TheoryData<string> Endpoints => new() { "begin", "finish", "list", "backfill", "settings-get", "settings-put" };

    private static async Task<(PaymentRequestTransport Transport, int Status)> CallAsync(
        string endpoint, LicenseApiClient client, CancellationToken ct = default)
    {
        switch (endpoint)
        {
            case "begin":
            {
                var r = await client.PostPaymentRequestBeginAsync(License, Begin(), ct);
                return (r.Transport, r.StatusCode);
            }
            case "finish":
            {
                var r = await client.PostPaymentRequestFinishAsync(License, Guid.NewGuid(), Finish(), ct);
                return (r.Transport, r.StatusCode);
            }
            case "list":
            {
                var r = await client.GetPaymentRequestsAsync(License, "cumulative", ["a"], ct);
                return (r.Transport, r.StatusCode);
            }
            case "backfill":
            {
                var r = await client.PostPaymentRequestBackfillAsync(License, Backfill(), ct);
                return (r.Transport, r.StatusCode);
            }
            case "settings-get":
            {
                var r = await client.GetShippingSettingsAsync(License, ct);
                return (r.Transport, r.StatusCode);
            }
            case "settings-put":
            {
                var r = await client.PutShippingSettingsAsync(License, Put(), ct);
                return (r.Transport, r.StatusCode);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Her_uc_404u_uc_yok_sayar_govdeli_ve_govdesiz(string endpoint)
    {
        var bare = Client(_ => FakeHttpMessageHandler.Empty(404));
        var aspNet = Client(_ => FakeHttpMessageHandler.Problem(404, "Not Found"));

        (await CallAsync(endpoint, bare)).Should().Be((PaymentRequestTransport.EndpointMissing, 404));
        (await CallAsync(endpoint, aspNet)).Should().Be((PaymentRequestTransport.EndpointMissing, 404),
            "lisans sahipliği reddinin 404'ü de uç yok gibi ele alınır");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Her_uc_5xx_ve_bozuk_200u_basarisiz_sayar(string endpoint)
    {
        (await CallAsync(endpoint, Client(_ => FakeHttpMessageHandler.Problem(500, "fake-fault"))))
            .Should().Be((PaymentRequestTransport.Failed, 500));
        (await CallAsync(endpoint, Client(_ => FakeHttpMessageHandler.Problem(503, "fake-fault"))))
            .Should().Be((PaymentRequestTransport.Failed, 503));
        (await CallAsync(endpoint, Client(_ => FakeHttpMessageHandler.Problem(429, "fake-fault"))))
            .Should().Be((PaymentRequestTransport.Failed, 429));
        (await CallAsync(endpoint, Client(_ => FakeHttpMessageHandler.Json(200, "null"))))
            .Should().Be((PaymentRequestTransport.Failed, 200));
        (await CallAsync(endpoint, Client(_ => FakeHttpMessageHandler.Json(200, "<html>bakım</html>"))))
            .Should().Be((PaymentRequestTransport.Failed, 200));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Her_uc_ag_hatasini_ve_zaman_asimini_cevrimdisi_sayar_iptal_firlatir(string endpoint)
    {
        (await CallAsync(endpoint, Client(_ => throw new HttpRequestException("fake offline"))))
            .Should().Be((PaymentRequestTransport.Offline, 0));
        (await CallAsync(endpoint, Client(_ => throw new TaskCanceledException("fake timeout"))))
            .Should().Be((PaymentRequestTransport.Offline, 0));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = Client(_ => throw new TaskCanceledException("iptal"));
        await FluentActions.Awaiting(() => CallAsync(endpoint, cancelled, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ── customer-balance/apply: scope-has-deduction (§6.6) ────────────────

    private static CustomerBalanceApplyRequest Apply() =>
        new(Guid.NewGuid(), 200m, 500m, Guid.NewGuid(), "cumulative");

    [Fact]
    public async Task Apply_409_scope_has_deduction_tipli_istisna_alanlariyla()
    {
        var transaction = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, $$"""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"scope-has-deduction","status":409,
             "detail":"Bu satışta geri alınmamış bir bakiye düşümü var.",
             "transactionId":"{{transaction}}","appliedAmount":150.00,"productTotal":500}
            """));

        var thrown = await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<ScopeHasDeductionException>();

        thrown.Which.TransactionId.Should().Be(transaction);
        thrown.Which.AppliedAmount.Should().Be(150m);
        thrown.Which.ProductTotal.Should().Be(500m);
        thrown.Which.Code.Should().Be("scope-has-deduction");
        thrown.Which.Should().BeAssignableTo<LicenseApiException>("çağıranın genel yakalaması bugünkü gibi tutar");
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("no-balance")]
    [InlineData("content-conflict")]
    public async Task Apply_obur_409lar_bugunku_gibi_ValidationException(string code)
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(409, code, "x"));

        var thrown = await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<ValidationException>();

        thrown.Which.Code.Should().Be(code, "kilit zaman aşımı 'stale' yeniden denemesi bu koda bakar (§6.6)");
    }

    [Fact]
    public async Task Apply_alanlari_eksik_scope_has_deduction_bugunku_gibi_ValidationException()
    {
        var client = Client(_ => FakeHttpMessageHandler.Problem(409, "scope-has-deduction", "x"));

        var thrown = await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<ValidationException>();

        thrown.Which.Code.Should().Be("scope-has-deduction");
    }

    [Fact]
    public async Task Apply_200_ve_400_bugunku_gibi()
    {
        var transaction = Guid.NewGuid();
        var ok = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"transactionId":"{{transaction}}","appliedAmount":150,"remainingBalance":0}
            """));
        (await ok.ApplyBalanceAsync(License, Apply())).Should()
            .Be(new CustomerBalanceApplyResponse(transaction, 150m, 0m));

        var bad = Client(_ => FakeHttpMessageHandler.Problem(400, "invalid-scope", "x"));
        (await FluentActions.Awaiting(() => bad.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be("invalid-scope");

        var down = Client(_ => FakeHttpMessageHandler.Problem(503, "fake-fault"));
        await FluentActions.Awaiting(() => down.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<LicenseApiUnknownException>();
    }
}
