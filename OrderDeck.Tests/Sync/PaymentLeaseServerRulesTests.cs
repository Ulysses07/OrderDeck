using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sahte sunucunun ödeme isteği kuralları (W3-12; spec 2026-10-13 §4.7, §4.8, §6.2, §6.4 a–h, §6.6, PR #523'ün onay
/// özeti) — tel üzerinden, gerçek <see cref="LicenseApiClient"/>'la: iki bilgisayarlı ödeme yakınsama testleri bu ikize
/// güveniyor. Kurallar sunucunun <c>PaymentRequestLeaseService</c>, <c>PaymentRequestRecordService</c>,
/// <c>LicensesShippingSettingsController</c> ve apply kapsam kuralıyla aynı; özet, sunucunun kodundan bağımsız hesaplanan
/// beklenen değerle sınanır (<see cref="ExpectedDigest"/>).
/// </summary>
public sealed class PaymentLeaseServerRulesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);

    private readonly FakeSalesServer _server = new()
    {
        Mode = 1, MinClientVersion = "0.9.11", LeaseRules = true,
        Shipping = new FakeSalesServer.ShippingBody(3, 500m, 60m),
    };
    private readonly HttpClient _http;
    private readonly LicenseApiClient _api;
    private readonly Guid _pcA = Guid.NewGuid();
    private readonly Guid _pcB = Guid.NewGuid();
    private DateTimeOffset _now = T0;

    public PaymentLeaseServerRulesTests()
    {
        _server.PaymentClock = () => _now;
        _http = new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _api = new LicenseApiClient(_http, new LicenseTokenStore());
        Hello(_pcA, "PC-A");
        Hello(_pcB, "PC-B");
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private void Hello(Guid replica, string machine, Guid? previous = null) =>
        _api.PostSalesHelloAsync(_server.LicenseId, new SalesHelloRequest(replica, previous, machine,
            $"parmak-{Guid.NewGuid():N}", 2, 3, 1, null)).GetAwaiter().GetResult();

    private Guid License => _server.LicenseId;

    private static string NewCustomer() => Guid.NewGuid().ToString("N");

    // ── sunucu satırları ───────────────────────────────────────────────────

    private Guid Session(bool released, Guid? owner = null, string? machine = null)
    {
        var id = Guid.NewGuid();
        _server.SeedSession(id, T0, released ? T0.AddHours(1) : null, owner: owner ?? _pcA,
            ownerMachineName: machine ?? "PC-A");
        if (released) _server.ReleaseSession(id);
        return id;
    }

    private Guid Order(Guid session, string customer, decimal price, bool printed = true, bool cancelled = false,
        bool tentative = false)
    {
        var id = Guid.NewGuid();
        _server.SeedOrder(new FakeSalesServer.OrderBody(id, session, customer, "tiktok", "alici_ornek", null, "aldım",
                "K1", price, T0, printed ? T0.AddMinutes(1) : null, cancelled ? T0.AddMinutes(2) : null,
                cancelled ? "musteri" : null, false, tentative, tentative, null, null, null, null, null, null, null, null,
                null),
            version: 1, origin: _pcA);
        return id;
    }

    // ── istekler ───────────────────────────────────────────────────────────

    private static decimal Fee(decimal total, bool recipientPays = false) =>
        recipientPays || total >= 500m ? 0m : 60m;

    private static PaymentRequestBeginRequest Begin(Guid replica, string owner, IReadOnlyList<string> members, string scope,
        IReadOnlyList<Guid> orders, decimal productTotal, decimal? totalBeforeBalance = null, int version = 3,
        bool recipientPays = false, IReadOnlyList<Guid>? confirmedIds = null, string? digest = null, Guid? recordId = null)
        => new(recordId ?? Guid.NewGuid(), owner, members, scope, orders, productTotal,
            totalBeforeBalance ?? productTotal + Fee(productTotal, recipientPays), recipientPays, version,
            confirmedIds ?? [], digest, replica, 2, 1);

    private Task<PaymentRequestHttpResult<PaymentRequestBeginResponse>> SendAsync(PaymentRequestBeginRequest request) =>
        _api.PostPaymentRequestBeginAsync(License, request);

    private Task<PaymentRequestHttpResult<PaymentRequestFinishResponse>> FinishAsync(Guid replica, Guid recordId,
        string outcome, decimal net, decimal? applied = null, bool fully = false) =>
        _api.PostPaymentRequestFinishAsync(License, recordId,
            new PaymentRequestFinishRequest(outcome, net, applied, fully, replica, 2, 1));

    /// <summary>Kurulan kira: kişinin iki basılmış etiketi (devredilmiş yayın), A'nın kirası.</summary>
    private (string Customer, Guid Session, List<Guid> Orders, decimal Total) Person()
    {
        var customer = NewCustomer();
        var session = Session(released: true);
        var orders = new List<Guid> { Order(session, customer, 150m), Order(session, customer, 200m) };
        return (customer, session, orders, 350m);
    }

    /// <summary>
    /// Özetin sunucu kodundan BAĞIMSIZ hesabı (§6.4-e "Özetin biçimi"): Id'ler tekil, küçük harf "N" yazımının ordinal
    /// sırasıyla (= RFC 9562 büyük uçlu baytların sırası); her biri 16 bayt hex'ten, art arda SHA-256; <c>v1.</c> +
    /// dolgusuz base64url.
    /// </summary>
    private static string ExpectedDigest(IEnumerable<Guid> ids)
    {
        var hex = ids.Select(i => i.ToString("N")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var bytes = Convert.FromHexString(string.Concat(hex));
        return "v1." + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ── kapılar ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Off_mode_off_alt_surumun_altinda_client_update_required_hicbir_sey_yazilmaz()
    {
        var p = Person();
        _server.Mode = 0;
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total)))
            .Problem!.Code.Should().Be("mode-off", "§4.7: kira denetimi yapmadan başarı dönmez");

        _server.Mode = 1;
        _server.MinClientVersion = "0.9.12";
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total)))
            .Problem!.Code.Should().Be("client-update-required");
        _server.MinClientVersion = null;
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total)))
            .Problem!.Code.Should().Be("client-update-required", "alt sürüm tanımsız: kapalı hata");

        _server.PaymentRecords().Should().BeEmpty();
        _server.Faults.Should().BeEmpty();
    }

    // ── (a), (h) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Kira_120_sn_yazilir_yeniden_oynatma_guncel_hali_doner_icerik_farkliysa_content_conflict()
    {
        var p = Person();
        var request = Begin(_pcA, p.Customer, [p.Customer], $"session:{p.Session:N}", p.Orders, p.Total);

        var first = await SendAsync(request);

        first.Transport.Should().Be(PaymentRequestTransport.Ok, string.Join(" | ", _server.Faults));
        first.Response!.State.Should().Be("sending");
        first.Response.LeaseUntil.Should().Be(T0.AddSeconds(120));
        first.Response.Members.Should().Equal(p.Customer);
        var record = _server.PaymentRecord(request.RecordId)!;
        record.Kind.Should().Be("lease");
        record.Machine.Should().Be("PC-A");
        record.PersonKey.Should().Be("c:" + p.Customer);
        record.OrderIds.Should().BeEquivalentTo(p.Orders);

        _now = T0.AddSeconds(30);
        var replay = await SendAsync(request);
        replay.Response!.State.Should().Be("sending", "aynı Id ve içerik: kaydın güncel hâli");
        replay.Response.LeaseUntil.Should().Be(T0.AddSeconds(120));
        _server.PaymentRecords().Should().ContainSingle("yeniden oynatma yazmaz");

        (await SendAsync(request with { ProductTotal = 351m, TotalBeforeBalance = 411m }))
            .Problem!.Code.Should().Be("content-conflict");

        _now = T0.AddSeconds(121);
        (await SendAsync(request)).Response!.State.Should().Be("unconfirmed",
            "süresi dolmuş kira yeniden oynatmada sunucu saatiyle unconfirmed; istemci saatiyle tartılmaz");
        _server.PaymentRecord(request.RecordId)!.State.Should().Be("sending", "satır yazılmadı");
    }

    // ── (c), (d), (e) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Baska_kopyanin_canli_kirasi_in_progress_elsewhere_dolunca_Unconfirmed_ve_istenmis_olabilir_sorusu()
    {
        var p = Person();
        var a = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(a)).Transport.Should().Be(PaymentRequestTransport.Ok);

        _now = T0.AddSeconds(5);
        var busy = await SendAsync(Begin(_pcB, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total));

        busy.Problem!.Code.Should().Be("in-progress-elsewhere");
        busy.Problem.Machine.Should().Be("PC-A");
        busy.Problem.Since.Should().Be(T0);
        busy.Problem.RecordId.Should().Be(a.RecordId);

        _now = T0.AddSeconds(121);
        var asked = await SendAsync(Begin(_pcB, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total));

        asked.Problem!.Code.Should().Be("already-requested");
        var prior = asked.Problem.Priors.Should().ContainSingle().Subject;
        prior.Id.Should().Be(a.RecordId);
        prior.Unconfirmed.Should().BeTrue("teslim kaydı olmadan dolan kira: gitmiş olabilir");
        prior.NetAmount.Should().BeNull();
        prior.TotalBeforeBalance.Should().Be(410m, "kiranın bakiye öncesi tutarı: 350 + lisans ayarıyla kargo 60");
        prior.Machine.Should().Be("PC-A");
        prior.FinishedAt.Should().Be(T0.AddSeconds(120), "kapanış anı kira sonu, B'nin begin anı değil");
        asked.Problem.PriorsDigest.Should().Be(ExpectedDigest([a.RecordId]));
        var record = _server.PaymentRecord(a.RecordId)!;
        record.State.Should().Be("unconfirmed", "(c)'nin yazımı retlerde de kalır");
        record.FinishedAt.Should().Be(T0.AddSeconds(120));
    }

    [Fact]
    public async Task Ayni_kopyanin_onceki_Sendingi_yeni_beginde_Unconfirmed_olur_ve_sorulur()
    {
        var p = Person();
        var crashed = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(crashed)).Transport.Should().Be(PaymentRequestTransport.Ok);

        _now = T0.AddSeconds(10);
        var again = await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total));

        again.Problem!.Code.Should().Be("already-requested", "süreç çöküp yeniden açıldı: mesaj gitmiş olabilir");
        again.Problem.Priors!.Single().Unconfirmed.Should().BeTrue();
        _server.PaymentRecord(crashed.RecordId)!.FinishedAt.Should().Be(T0.AddSeconds(10),
            "süresi dolmamış kira bu anda kapanır");
    }

    // ── (b) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Members_stale_sunucu_grubunu_kapsamayan_kume_reddedilir_uye_kesisimi_baska_anahtarli_kaydi_bulur()
    {
        var t = NewCustomer();
        var i = NewCustomer();
        var session = Session(released: true);
        var kt = Order(session, t, 150m);
        var ki = Order(session, i, 200m);

        // Grup henüz yokken T'nin kaydı (PersonKey c:T, üye T) teslim edildi.
        var early = Begin(_pcA, t, [t], "cumulative", [kt], 150m);
        (await SendAsync(early)).Transport.Should().Be(PaymentRequestTransport.Ok);
        (await FinishAsync(_pcA, early.RecordId, "delivered", 210m)).Transport.Should().Be(PaymentRequestTransport.Ok);

        // Sonra sunucuda T ve I aynı gruba girdi; B bunu henüz bilmiyor.
        _server.SetServerGroup("grup-ornek", t, i);
        var stale = await SendAsync(Begin(_pcB, t, [t], "cumulative", [kt], 150m));

        stale.Problem!.Code.Should().Be("members-stale");
        stale.Problem.Members.Should().Equal(new[] { t, i }.Order(StringComparer.Ordinal));

        // Kişiyi I'dan kuran istek PersonKey'i g:… — T'nin c:T kaydını üye kesişimi bulur.
        var regrouped = await SendAsync(Begin(_pcB, i, [i, t], "cumulative", [kt, ki], 350m));
        regrouped.Problem!.Code.Should().Be("already-requested");
        regrouped.Problem.Priors!.Single().Id.Should().Be(early.RecordId);

        var confirmed = await SendAsync(Begin(_pcB, i, [i, t], "cumulative", [kt, ki], 350m,
            confirmedIds: [early.RecordId]));
        confirmed.Response!.Members.Should().Equal(new[] { t, i }.Order(StringComparer.Ordinal));
        _server.PaymentRecord(confirmed.Response.RecordId)!.PersonKey.Should().Be("g:grup-ornek");
    }

    /// <summary>Sunucu grubu müşteri gönderiminin grup biriminden (biçim 2): yalnız damgası daha yeni olan yazar — grubu
    /// hiç değiştirmemiş bilgisayarın damgasız boş <c>groupId</c>'si grubu silmez.</summary>
    [Fact]
    public async Task Sunucu_grubu_musteri_gonderiminin_damgali_grup_biriminden_damgasiz_bos_grup_silmez()
    {
        var t = NewCustomer();
        var i = NewCustomer();
        var session = Session(released: true);
        var kt = Order(session, t, 150m);

        async Task PushAsync(string id, string? groupId, DateTimeOffset? changedAt)
        {
            var body = new
            {
                customers = new[]
                {
                    new
                    {
                        id = Guid.ParseExact(id, "N"), platform = "tiktok", username = "alici_" + id[..6], fullName = (string?)null,
                        phone = (string?)null, address = (string?)null, updatedAt = T0, format = 2, groupId,
                        groupIdChangedAt = changedAt,
                    },
                },
            };
            (await _http.PostAsJsonAsync($"/api/v1/licenses/{License}/wpf-customers/sync", body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
                .EnsureSuccessStatusCode();
        }

        await PushAsync(t, "grup-gonderim", T0.AddMinutes(1));
        await PushAsync(i, "grup-gonderim", T0.AddMinutes(1));
        _server.ServerGroupOf(t).Should().Be("grup-gonderim");
        (await SendAsync(Begin(_pcB, t, [t], "cumulative", [kt], 150m))).Problem!.Code.Should().Be("members-stale");

        await PushAsync(t, null, null);                                   // grubu bilmeyen bilgisayarın taslağı
        _server.ServerGroupOf(t).Should().Be("grup-gonderim", "damgasız gönderim grup birimine dokunmaz");
        await PushAsync(t, null, T0);                                     // eski damgalı ayrılma
        _server.ServerGroupOf(t).Should().Be("grup-gonderim", "eski damga yazmaz");

        await PushAsync(t, null, T0.AddMinutes(5));                       // gruptan ayrıldı (yeni damga)
        _server.ServerGroupOf(t).Should().BeNull();
        (await SendAsync(Begin(_pcB, t, [t], "cumulative", [kt], 150m))).Transport.Should().Be(PaymentRequestTransport.Ok);
    }

    [Fact]
    public async Task Kopyanin_siparisi_ve_kaydi_asil_kayitla_sayilir_GUIDsiz_uye_ham_Idle()
    {
        var canonical = Guid.NewGuid();
        var copy = Guid.NewGuid();
        _server.SeedCustomer(canonical, "tiktok", "alici_asil", null);
        _server.SeedCustomer(copy, "tiktok", "alici_kopya", null, mergedInto: canonical);
        const string raw = "tiktok:yerel-taslak";
        var session = Session(released: true);
        var fromCopy = Order(session, copy.ToString("N"), 100m);
        var fromRaw = Order(session, raw, 40m);

        var begin = Begin(_pcA, canonical.ToString("N"), [canonical.ToString("N"), raw], "cumulative",
            [fromCopy, fromRaw], 140m);
        var reply = await SendAsync(begin);

        reply.Transport.Should().Be(PaymentRequestTransport.Ok,
            "kopyanın Id'siyle saklanmış sipariş asıl kayda, GUID'siz üyenin siparişi ham Id'ye sayılır");
    }

    // ── (e) onay: Id listesi "ya da" özet (#523) ────────────────────────────

    [Fact]
    public async Task Already_requested_onayi_Id_listesi_ya_da_butun_kumenin_ozeti_kume_degisirse_yeniden_sorar()
    {
        var p = Person();
        var scope = "cumulative";
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        foreach (var (id, at) in new[] { (first, T0.AddMinutes(-20)), (second, T0.AddMinutes(-10)) })
            _server.AddPaymentPrior(new FakeSalesServer.PaymentPrior(id, "PC-B", [p.Customer], scope, 410m, null, at, at,
                false));

        var asked = await SendAsync(Begin(_pcA, p.Customer, [p.Customer], scope, p.Orders, p.Total));

        asked.Problem!.Code.Should().Be("already-requested");
        asked.Problem.Priors!.Select(x => x.Id).Should().Equal(first, second);
        asked.Problem.PriorsDigest.Should().Be(ExpectedDigest([second, first])).And.HaveLength(46);

        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], scope, p.Orders, p.Total, confirmedIds: [first, second])))
            .Transport.Should().Be(PaymentRequestTransport.Ok, "Id listesi bütün öncülleri kapsıyor");
        _server.PaymentRecords().Where(r => r.State == "sending").Should().ContainSingle();
    }

    [Fact]
    public async Task Ozet_Id_listesinden_bagimsiz_tutar_geç_katilan_ve_cikan_kayit_yeniden_sordurur()
    {
        var p = Person();
        var scope = "cumulative";
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        foreach (var id in new[] { first, second })
            _server.AddPaymentPrior(new FakeSalesServer.PaymentPrior(id, "PC-B", [p.Customer], scope, 410m, null,
                T0.AddMinutes(-5), T0.AddMinutes(-5), false));
        var digest = (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], scope, p.Orders, p.Total)))
            .Problem!.PriorsDigest!;

        // Soru ile onay arasında küme büyüdü (geç commit): özet tutmaz, taze soru yeni özetle.
        var late = Guid.NewGuid();
        _server.AddPaymentPrior(new FakeSalesServer.PaymentPrior(late, "PC-B", [p.Customer], scope, 410m, null,
            T0.AddMinutes(-30), T0.AddMinutes(-30), true));
        var fresh = await SendAsync(Begin(_pcA, p.Customer, [p.Customer], scope, p.Orders, p.Total,
            confirmedIds: [first], digest: digest));
        fresh.Problem!.Code.Should().Be("already-requested", "küme sorudan beri değişti: yeniden sorulur");
        fresh.Problem.Priors!.Select(x => x.Id).Should().Equal(late, second);
        fresh.Problem.PriorsDigest.Should().Be(ExpectedDigest([first, second, late])).And.NotBe(digest);

        // Yeni özetle, kesilmiş (yalnız bir Id) listeyle geçer: bütün kümeyi özet onaylar.
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], scope, p.Orders, p.Total, confirmedIds: [first],
                digest: fresh.Problem.PriorsDigest)))
            .Transport.Should().Be(PaymentRequestTransport.Ok);
    }

    [Fact]
    public async Task Confirmed_prior_ids_500u_asarsa_400_bozuk_ya_da_bilinmeyen_surumlu_ozet_400_hicbir_sey_yazilmaz()
    {
        var p = Person();
        var body = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        var tooMany = await _http.PostAsJsonAsync($"/api/v1/licenses/{License}/payment-requests/begin",
            body with { ConfirmedPriorIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList() },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        ((int)tooMany.StatusCode).Should().Be(400);
        (await tooMany.Content.ReadAsStringAsync()).Should().Contain("invalid-confirmed-priors");

        var valid = ExpectedDigest([Guid.NewGuid()]);
        var lastIndex = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".IndexOf(valid[^1]);
        var nonCanonicalTail = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[lastIndex ^ 1];
        foreach (var bad in new[] { "v1.abc", "v2." + valid[3..], valid[..^1] + nonCanonicalTail, valid + "=" })
            (await SendAsync(body with { RecordId = Guid.NewGuid(), ConfirmedPriorsDigest = bad }))
                .Problem!.Code.Should().Be("invalid-priors-digest", bad);

        _server.PaymentRecords().Should().BeEmpty();
    }

    // ── (f) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_total_toplami_esit_kumesi_farkli_istegi_reddeder_liveElsewhere_baska_kopyanin_canli_yayini()
    {
        var customer = NewCustomer();
        var released = Session(released: true);
        var counted = Order(released, customer, 150m);
        var cancelled = Order(released, customer, 150m, cancelled: true);
        Order(released, customer, 70m, printed: false);
        Order(released, customer, 70m, tentative: true);

        var sameTotal = await SendAsync(Begin(_pcB, customer, [customer], "cumulative", [cancelled], 150m));
        sameTotal.Problem!.Code.Should().Be("stale-total", "küme karşılaştırması toplamı eşit iki farklı kümeyi yakalar");
        sameTotal.Problem.ServerTotal.Should().Be(150m);
        sameTotal.Problem.LiveElsewhere.Should().BeNull("yalnız sunucuda olan sipariş devredilmiş yayında");

        var live = Session(released: false, owner: _pcA, machine: "PC-A");
        var onLive = Order(live, customer, 120m);
        var behind = await SendAsync(Begin(_pcB, customer, [customer], "cumulative", [counted], 150m));
        behind.Problem!.Code.Should().Be("stale-total");
        behind.Problem.ServerTotal.Should().Be(270m);
        behind.Problem.LiveElsewhere.Should().ContainSingle().Which.Should().Be(new PaymentRequestLiveSession(live, "PC-A"));

        var own = await SendAsync(Begin(_pcA, customer, [customer], "cumulative", [counted], 150m));
        own.Problem!.LiveElsewhere.Should().BeNull("çağıranın kendi yayını: istemci kendisi yetişebilir");

        (await SendAsync(Begin(_pcA, customer, [customer], $"session:{released:N}", [counted], 150m)))
            .Transport.Should().Be(PaymentRequestTransport.Ok, "yayın kapsamı yalnız o yayının siparişleri");
        (await SendAsync(Begin(_pcB, customer, [customer], "cumulative", [counted, onLive], 270m)))
            .Transport.Should().Be(PaymentRequestTransport.Ok,
                "küme ve toplam tuttu; A'nın kirası başka kapsamda (kişi × kapsam başına kira)");
    }

    // ── (g) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_stale_surum_eskiyse_ya_da_tutar_lisans_ayariyla_tutmuyorsa_current_ile()
    {
        var p = Person();

        var oldVersion = await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total, version: 2));
        oldVersion.Problem!.Code.Should().Be("settings-stale");
        oldVersion.Problem.Current.Should().BeEquivalentTo(new { Version = 3, FreeShippingThreshold = 500m, Fee = 60m });

        var localFee = await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total,
            totalBeforeBalance: 400m));
        localFee.Problem!.Code.Should().Be("settings-stale", "yerel ayarla (50) hesaplanmış tutar doğru sürümle geçemez");

        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total, recipientPays: true)))
            .Transport.Should().Be(PaymentRequestTransport.Ok, "alıcı ödemeli: kargo 0");
    }

    [Fact]
    public async Task Kargo_ayari_yoksa_surum_0_ve_kargo_0_esik_ustunde_kargo_0()
    {
        _server.Shipping = null;
        var p = Person();
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total, totalBeforeBalance: 350m,
                version: 0)))
            .Transport.Should().Be(PaymentRequestTransport.Ok);

        _server.Shipping = new FakeSalesServer.ShippingBody(4, 300m, 60m);
        var q = Person();
        (await SendAsync(Begin(_pcB, q.Customer, [q.Customer], "cumulative", q.Orders, q.Total, totalBeforeBalance: 350m,
                version: 4)))
            .Transport.Should().Be(PaymentRequestTransport.Ok, "350 ≥ 300: ücretsiz kargo");
    }

    // ── finish ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finish_soy_kontrolu_idempotent_son_hal_degismez_kayit_yoksa_404()
    {
        var p = Person();
        var lease = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(lease)).Transport.Should().Be(PaymentRequestTransport.Ok);

        (await FinishAsync(_pcB, lease.RecordId, "delivered", 410m)).Problem!.Code.Should().Be("content-conflict",
            "kirayı başka bir kopya açtı");
        _now = T0.AddSeconds(20);
        var delivered = await FinishAsync(_pcA, lease.RecordId, "delivered", 410m, 0m);
        delivered.Response!.State.Should().Be("delivered");
        delivered.Response.FinishedAt.Should().Be(T0.AddSeconds(20));
        _now = T0.AddSeconds(40);
        (await FinishAsync(_pcA, lease.RecordId, "delivered", 410m, 0m)).Response!.FinishedAt.Should().Be(T0.AddSeconds(20),
            "aynı sonuç: yazım yok");
        (await FinishAsync(_pcA, lease.RecordId, "abandoned", 0m)).Problem!.Code.Should().Be("content-conflict",
            "son hâlde başka sonuç");
        (await FinishAsync(_pcA, Guid.NewGuid(), "delivered", 1m)).Problem!.Code.Should().Be("record-not-found");
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Gec_delivered_suresi_dolmus_kiranin_Unconfirmedini_yukseltir_geri_yuklenmis_ardil_bitirebilir()
    {
        var p = Person();
        var lease = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(lease)).Transport.Should().Be(PaymentRequestTransport.Ok);
        _now = T0.AddSeconds(130);
        (await SendAsync(Begin(_pcB, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total)))
            .Problem!.Code.Should().Be("already-requested");
        _server.PaymentRecord(lease.RecordId)!.State.Should().Be("unconfirmed");

        var restored = Guid.NewGuid();
        Hello(restored, "PC-A", previous: _pcA);
        var late = await FinishAsync(restored, lease.RecordId, "delivered", 410m);

        late.Response!.State.Should().Be("delivered", "geç delivered süre dolumunun Unconfirmed'ını yükseltir; geri "
                                                      + "yüklenmiş kopya PreviousReplicaId zinciriyle tanınır");
        var asked = await SendAsync(Begin(_pcB, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total));
        asked.Problem!.Priors!.Single().Should().Match<PaymentRequestPrior>(x => !x.Unconfirmed && x.NetAmount == 410m);
    }

    // ── liste ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Liste_kapsam_ve_uye_kesisimi_vazgecilmis_girmez_suresi_dolmus_kira_unconfirmed_siparisleriyle()
    {
        var p = Person();
        var abandoned = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(abandoned)).Transport.Should().Be(PaymentRequestTransport.Ok);
        (await FinishAsync(_pcA, abandoned.RecordId, "abandoned", 0m)).Transport.Should().Be(PaymentRequestTransport.Ok);
        _now = T0.AddSeconds(5);
        var open = Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total);
        (await SendAsync(open)).Transport.Should().Be(PaymentRequestTransport.Ok);

        var live = await _api.GetPaymentRequestsAsync(License, "cumulative", [p.Customer]);
        live.Response!.Records.Should().ContainSingle().Which.Should().Match<PaymentRequestListItem>(r =>
            r.Id == open.RecordId && r.State == "sending" && r.FinishedAt == null && r.Machine == "PC-A"
            && r.Kind == "lease");

        _now = T0.AddSeconds(200);
        var expired = (await _api.GetPaymentRequestsAsync(License, "cumulative", [p.Customer])).Response!.Records.Single();
        expired.State.Should().Be("unconfirmed");
        expired.FinishedAt.Should().Be(T0.AddSeconds(125), "kira sonu, satır yazılmadan");
        expired.OrderIds.Should().Equal(p.Orders.Order());
        _server.PaymentRecord(open.RecordId)!.State.Should().Be("sending", "liste yazmaz");
        (await _api.GetPaymentRequestsAsync(License, $"session:{p.Session:N}", [p.Customer])).Response!.Records
            .Should().BeEmpty("kapsam ordinal ayrı");
        (await _api.GetPaymentRequestsAsync(License, "cumulative", [NewCustomer()])).Response!.Records
            .Should().BeEmpty();
    }

    // ── geri doldurma ──────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_applied_unchanged_yeni_uyeler_eklenir_baska_icerik_ve_kira_Idsi_content_conflict_kipten_bagimsiz()
    {
        _server.Mode = 0;
        var p = Person();
        var friend = NewCustomer();
        var lease = Guid.NewGuid();
        var item = new PaymentRequestBackfillItem(Guid.NewGuid(), p.Customer, [p.Customer], "cumulative", p.Orders,
            "delivered", 410m, null, false, T0.AddDays(-1));

        async Task<string> Send(PaymentRequestBackfillItem i) =>
            (await _api.PostPaymentRequestBackfillAsync(License, new PaymentRequestBackfillRequest([i], _pcA, 2, 0)))
            .Response!.Records.Single().Status;

        (await Send(item)).Should().Be("applied", "mod Off'ta da");
        (await Send(item with { MemberIds = [p.Customer, friend], At = T0 })).Should().Be("unchanged",
            "aynı içerik; üyeler ve an özete girmez");
        _server.PaymentRecord(item.RecordId)!.Members.Should().BeEquivalentTo([p.Customer, friend], "yeni üye eklendi");
        (await Send(item with { NetAmount = 400m })).Should().Be("content-conflict");
        (await Send(item with { OwnerId = friend, MemberIds = [friend] })).Should().Be("content-conflict",
            "sahip asıl kayıt düzeyinde ayrıca karşılaştırılır");

        _server.Mode = 1;
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total, recordId: lease)))
            .Transport.Should().Be(PaymentRequestTransport.Problem, "kirasız kayıt öncül: already-requested");
        var record = _server.PaymentRecord(item.RecordId)!;
        record.Kind.Should().Be("record");
        record.Machine.Should().Be("PC-A");
        record.StartedAt.Should().Be(T0.AddDays(-1), "istemcinin anı (gösterim)");
        record.FinishedAt.Should().Be(T0, "sunucunun alış anı");
    }

    // ── kargo ayarı (§4.8) ─────────────────────────────────────────────────

    [Fact]
    public async Task Kargo_ayari_GET_surumu_doner_PUT_mod_Offta_mode_off_eski_surumde_stale_kabulde_surum_artar()
    {
        var get = await _api.GetShippingSettingsAsync(License);
        get.Response!.Should().BeEquivalentTo(new { Version = 3, FreeShippingThreshold = 500m, Fee = 60m });

        _server.Mode = 0;
        (await _api.PutShippingSettingsAsync(License, new ShippingSettingsPutRequest(3, 600m, 70m, _pcB, 2, 0)))
            .Problem!.Code.Should().Be("mode-off");

        _server.Mode = 1;
        var stale = await _api.PutShippingSettingsAsync(License, new ShippingSettingsPutRequest(2, 600m, 70m, _pcB, 2, 1));
        stale.Problem!.Code.Should().Be("stale");
        stale.Problem.Current!.Version.Should().Be(3);

        var put = await _api.PutShippingSettingsAsync(License, new ShippingSettingsPutRequest(3, 600m, 70m, _pcB, 2, 1));
        put.Response!.Should().BeEquivalentTo(new
        {
            Version = 4, FreeShippingThreshold = 600m, Fee = 70m, UpdatedBy = _pcB.ToString(), UpdatedAt = (DateTimeOffset?)T0,
        });
        _server.Shipping.Should().Be(new FakeSalesServer.ShippingBody(4, 600m, 70m), "state de yeni değeri bildirir");

        var p = Person();
        (await SendAsync(Begin(_pcA, p.Customer, [p.Customer], "cumulative", p.Orders, p.Total)))
            .Problem!.Current!.Version.Should().Be(4, "öbür bilgisayarın PUT'undan sonra begin settings-stale");
        _server.Faults.Should().BeEmpty();
    }

    // ── bakiye: kapsam başına tek geçerli düşüm (§6.6) ──────────────────────

    [Fact]
    public async Task Apply_kapsamda_dusum_varsa_scope_has_deduction_kilit_stale_betiklenir_geri_alininca_gecer()
    {
        _server.BalancePreview = 100m;
        var customer = Guid.NewGuid();
        var scope = $"session:{Guid.NewGuid():N}";
        var first = await _api.ApplyBalanceAsync(License, new CustomerBalanceApplyRequest(customer, 100m, 600m,
            Guid.NewGuid(), scope));
        first.AppliedAmount.Should().Be(100m);

        var held = await FluentActions.Awaiting(() => _api.ApplyBalanceAsync(License,
                new CustomerBalanceApplyRequest(customer, 100m, 650m, Guid.NewGuid(), scope)))
            .Should().ThrowAsync<ScopeHasDeductionException>();
        held.Which.TransactionId.Should().Be(first.TransactionId);
        held.Which.AppliedAmount.Should().Be(100m);
        held.Which.ProductTotal.Should().Be(600m);

        (await _api.ApplyBalanceAsync(License, new CustomerBalanceApplyRequest(customer, 100m, 600m,
            first.TransactionId, scope))).TransactionId.Should().Be(first.TransactionId, "aynı anahtar: yeniden oynatma");

        _server.ApplyLockTimeouts = 1;
        (await FluentActions.Awaiting(() => _api.ApplyBalanceAsync(License,
                new CustomerBalanceApplyRequest(customer, 100m, 650m, Guid.NewGuid(), scope)))
            .Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be("stale");

        (await _api.GetBalanceScopeAsync(License, customer, scope))!.TransactionId.Should().Be(first.TransactionId);
        await _api.ReverseBalanceTransactionAsync(License, first.TransactionId);
        (await _api.GetBalanceTransactionStatusAsync(License, first.TransactionId))!.Reversed.Should().BeTrue();
        (await _api.GetBalanceScopeAsync(License, customer, scope)).Should().BeNull();
        (await FluentActions.Awaiting(() => _api.ReverseBalanceTransactionAsync(License, first.TransactionId))
            .Should().ThrowAsync<LicenseApiException>()).Which.Code.Should().Be("already-reversed");

        var fresh = await _api.ApplyBalanceAsync(License, new CustomerBalanceApplyRequest(customer, 100m, 650m,
            Guid.NewGuid(), scope));
        fresh.AppliedAmount.Should().Be(100m, "geri alınan düşümün bakiyesi döndü");
        _server.BalanceTransactions().Count(t => !t.Reversed).Should().Be(1);

        _server.Mode = 0;
        (await _api.ApplyBalanceAsync(License, new CustomerBalanceApplyRequest(customer, 10m, 650m, Guid.NewGuid(), scope)))
            .Should().NotBeNull("mod Off'ta kapsam kuralı yok (§6.6)");
    }
}
