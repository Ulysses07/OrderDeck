using System.Net;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST payment-requests/begin</c> — spec 2026-10-13 §4.7, §6.2, §6.4 (a–h), §6.5-2, §15-21, §15-54 (InMemory).
/// Kapılar (sahiplik, 400, mod Off, alt sürüm), kiranın yazımı, yeniden oynatma, 409 sınıfları, sunucu toplamı
/// (<c>stale-total</c>) ve kargo ayarı (<c>settings-stale</c>). Eşzamanlılık (tekil indeks, kilitler) ve SQL harmanı
/// gerçek SQL Server'da (<see cref="PaymentRequestBeginRelationalTests"/>).
/// </summary>
public sealed class PaymentRequestBeginTests : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<ApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;
    private readonly ApiFactory _withoutMinimum;

    public PaymentRequestBeginTests(SalesSyncConfiguredApiFactory factory, ApiFactory withoutMinimum)
    {
        _factory = factory;
        _withoutMinimum = withoutMinimum;
    }

    // ── Kapılar ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Baska_yayincinin_lisansi_404_hicbir_sey_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var stranger = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();

        var resp = await stranger.Sales.SendToAsync(kit.BeginUrl, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { "protokolsuz", "invalid-envelope" },
        { "protokol-1", "invalid-envelope" },
        { "replicaId-yok", "invalid-envelope" },
        { "seenMode-yok", "invalid-envelope" },
        { "recordId-yok", "invalid-record" },
        { "recordId-bos", "invalid-record" },
        { "uyeler-yok", "invalid-members" },
        { "uyeler-bos", "invalid-members" },
        { "uyeler-101", "invalid-members" },
        { "uye-bos-metin", "invalid-members" },
        { "uye-65-karakter", "invalid-members" },
        { "sahip-uyelerde-yok", "invalid-members" },
        { "sahip-yok", "invalid-members" },
        { "kapsam-yok", "invalid-scope" },
        { "kapsam-buyuk-harf", "invalid-scope" },
        { "kapsam-tireli-guid", "invalid-scope" },
        { "kapsam-bozuk", "invalid-scope" },
        { "kapsam-legacy", "invalid-scope" },
        { "siparisler-yok", "invalid-orders" },
        { "siparis-bos-guid", "invalid-orders" },
        { "siparisler-10001", "invalid-orders" },
        { "urun-toplami-eksi", "invalid-amount" },
        { "urun-toplami-3-ondalik", "invalid-amount" },
        { "bakiye-oncesi-yok", "invalid-amount" },
        { "bakiye-oncesi-eksi", "invalid-amount" },
        { "alici-oder-yok", "invalid-shipping" },
        { "kargo-surumu-yok", "invalid-shipping" },
        { "kargo-surumu-eksi", "invalid-shipping" },
        { "onaylananlar-501", "invalid-confirmed-priors" },
        { "onaylanan-bos-guid", "invalid-confirmed-priors" },
        { "ozet-bos", "invalid-priors-digest" },
        { "ozet-oneksiz", "invalid-priors-digest" },
        { "ozet-surum-2", "invalid-priors-digest" },
        { "ozet-base64url-degil", "invalid-priors-digest" },
        { "ozet-dolgulu", "invalid-priors-digest" },
        { "ozet-kisa", "invalid-priors-digest" },
        { "ozet-bosluklu", "invalid-priors-digest" },
        { "ozet-kanonik-degil", "invalid-priors-digest" },
    };

    /// <summary>Her 400 sınıfı: hiçbir şey yazılmaz.</summary>
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Gecersiz_istek_400_kodlu_hicbir_sey_yazilmaz(string @case, string code)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var body = Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner, Kit.NewId()], orderIds: [Guid.NewGuid()]);
        Corrupt(body, @case, owner);

        var resp = await kit.BeginAsync(body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code, @case);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    private static void Corrupt(Dictionary<string, object?> body, string @case, string owner)
    {
        switch (@case)
        {
            case "protokolsuz": body.Remove("protocol"); break;
            case "protokol-1": body["protocol"] = 1; break;
            case "replicaId-yok": body.Remove("replicaId"); break;
            case "seenMode-yok": body.Remove("seenMode"); break;
            case "recordId-yok": body.Remove("recordId"); break;
            case "recordId-bos": body["recordId"] = Guid.Empty; break;
            case "uyeler-yok": body.Remove("memberIds"); break;
            case "uyeler-bos": body["memberIds"] = Array.Empty<string>(); break;
            case "uyeler-101":
                body["memberIds"] = Enumerable.Range(0, 100).Select(_ => Kit.NewId()).Append(owner).ToArray();
                break;
            case "uye-bos-metin": body["memberIds"] = new[] { owner, " " }; break;
            case "uye-65-karakter": body["memberIds"] = new[] { owner, new string('a', 65) }; break;
            case "sahip-uyelerde-yok": body["ownerId"] = Kit.NewId(); break;
            case "sahip-yok": body.Remove("ownerId"); break;
            case "kapsam-yok": body.Remove("scopeKey"); break;
            case "kapsam-buyuk-harf": body["scopeKey"] = "Cumulative"; break;
            case "kapsam-tireli-guid": body["scopeKey"] = $"session:{Guid.NewGuid():D}"; break;
            case "kapsam-bozuk": body["scopeKey"] = "session:yayin-1"; break;
            case "kapsam-legacy": body["scopeKey"] = "legacy"; break;
            case "siparisler-yok": body.Remove("orderIds"); break;
            case "siparis-bos-guid": body["orderIds"] = new[] { Guid.NewGuid(), Guid.Empty }; break;
            case "siparisler-10001":
                body["orderIds"] = Enumerable.Range(0, 10_001).Select(_ => Guid.NewGuid()).ToArray();
                break;
            case "urun-toplami-eksi": body["productTotal"] = -1m; break;
            case "urun-toplami-3-ondalik": body["productTotal"] = 10.005m; break;
            case "bakiye-oncesi-yok": body.Remove("totalBeforeBalance"); break;
            case "bakiye-oncesi-eksi": body["totalBeforeBalance"] = -0.01m; break;
            case "alici-oder-yok": body.Remove("recipientPays"); break;
            case "kargo-surumu-yok": body.Remove("shippingSettingsVersion"); break;
            case "kargo-surumu-eksi": body["shippingSettingsVersion"] = -1; break;
            case "onaylananlar-501":
                body["confirmedPriorIds"] = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
                break;
            case "onaylanan-bos-guid": body["confirmedPriorIds"] = new[] { Guid.Empty }; break;
            // Özet yalnız already-requested'in priorsDigest'i olabilir: "v1." + 32 baytın dolgusuz base64url'i (43
            // karakter). Değerler üretilir; geçerli olandan bir karakter bozulur.
            case "ozet-bos": body["confirmedPriorsDigest"] = ""; break;
            case "ozet-oneksiz": body["confirmedPriorsDigest"] = Kit.UnrelatedPriorsDigest()[3..]; break;
            case "ozet-surum-2": body["confirmedPriorsDigest"] = "v2." + Kit.UnrelatedPriorsDigest()[3..]; break;
            case "ozet-base64url-degil": body["confirmedPriorsDigest"] = ReplaceAt(Kit.UnrelatedPriorsDigest(), 10, '+'); break;
            case "ozet-dolgulu": body["confirmedPriorsDigest"] = Kit.UnrelatedPriorsDigest() + "="; break;
            case "ozet-kisa": body["confirmedPriorsDigest"] = Kit.UnrelatedPriorsDigest()[..^1]; break;
            case "ozet-bosluklu": body["confirmedPriorsDigest"] = ReplaceAt(Kit.UnrelatedPriorsDigest(), 10, ' '); break;
            case "ozet-kanonik-degil": body["confirmedPriorsDigest"] = NonCanonical(Kit.UnrelatedPriorsDigest()); break;
            default: throw new ArgumentOutOfRangeException(nameof(@case), @case, null);
        }
    }

    private static string ReplaceAt(string value, int index, char replacement) =>
        string.Concat(value.AsSpan(0, index), [replacement], value.AsSpan(index + 1));

    /// <summary>Aynı 32 bayta çözülen ikinci bir yazım: son karakterin artık iki biti (32 bayt 256 bit, 43 karakter 258
    /// bit) sıfır değil. Özetin tek bir yazımı vardır.</summary>
    private static string NonCanonical(string digest)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(digest[^1], StringComparison.Ordinal);
        return digest[..^1] + alphabet[last | 1];
    }

    /// <summary>
    /// §4.7, §15-54: mod Off'ta <c>begin</c> 409 <c>mode-off</c>, kira denetimi yapmadan başarı dönmez ve kayıt yazılmaz.
    /// İstek yine kopyanın kalp atışıdır.
    /// </summary>
    [Fact]
    public async Task Mod_Off_409_mode_off_kayit_yok_kalp_atisi_yazilir()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var replica = Guid.NewGuid();
        var owner = Kit.NewId();
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("mode-off");
        (await kit.RecordsAsync()).Should().BeEmpty();
        var heartbeat = await kit.Sales.ReplicaAsync(replica);
        heartbeat.Should().NotBeNull("istek kopyanın kalp atışıdır");
        heartbeat!.LastSeenAt.Should().BeOnOrAfter(before);
    }

    /// <summary>§6.5-2, M9: mod On'da eski, sürümsüz istemci 409 <c>client-update-required</c>; kayıt yazılmaz.</summary>
    [Theory]
    [InlineData("OrderDeck-WPF/0.9.10")]
    [InlineData(null)]
    [InlineData("Mozilla/5.0")]
    public async Task Eski_ya_da_surumsuz_istemci_409_client_update_required(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]), userAgent);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("client-update-required");
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>§2 "Ws": <c>SalesSync:MinClientVersion</c> boşken uç kapalı-hata çalışır, güncel istemci de geçemez.</summary>
    [Fact]
    public async Task Alt_surum_tanimsizken_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_withoutMinimum);
        var owner = Kit.NewId();

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("client-update-required");
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    // ── (h) kiranın yazımı ───────────────────────────────────────────────────

    /// <summary>
    /// §6.4-b, f, g, h: sahip gruplu (PersonKey <c>g:</c> + ham GroupId). İstemci grubun bir üyesini kopyasının Id'siyle
    /// gönderir (eski birleştirme), ayrıca GUID'siz bir üye. Üyeler asıl kayda çözülür; sunucu grubu kapsandığı, küme ve
    /// toplam sunucununkiyle, kargo lisans ayarıyla (eşik 500, ücret 60, sürüm 4) tuttuğu için kira yazılır: Sending,
    /// üyeler (istemcinin kümesi ∪ sunucu grubu), siparişler, <c>LeaseUntil = StartedAt + 120 sn</c>, makine adı kopya
    /// satırından.
    /// </summary>
    [Fact]
    public async Task Kira_yazilir_uyeler_asil_kayda_cozulur_siparisler_ve_120_sn()
    {
        var kit = await Kit.CreateAsync(_factory);
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(group);
        var sibling = await kit.SeedProjectionAsync(group);
        var siblingCopy = await kit.SeedProjectionAsync(group, mergedInto: Guid.ParseExact(sibling, "N"));
        var raw = $"yerel-{Guid.NewGuid():N}";
        var replica = Guid.NewGuid();
        await kit.SeedReplicaAsync(replica, "PC-A");
        await kit.SeedShippingAsync(4, threshold: 500m, fee: 60m);
        var recordId = Guid.NewGuid();
        var orders = new[] { (await kit.SeedOrderAsync(owner, 290m)).Id, (await kit.SeedOrderAsync(raw, 200m)).Id };
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.BeginAsync(Kit.Body(
            recordId, replica, owner, [owner, siblingCopy, raw, owner], orderIds: [.. orders, orders[0]],
            productTotal: 490m, totalBeforeBalance: 550m, shippingSettingsVersion: 4));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var expectedMembers = new[] { owner, sibling, raw }.Order(StringComparer.Ordinal).ToList();
        var body = await Kit.ReadAsync(resp);
        body.RecordId.Should().Be(recordId);
        body.State.Should().Be("sending");
        body.Members.Should().Equal(expectedMembers);

        var record = (await kit.RecordsAsync()).Should().ContainSingle().Subject;
        record.Should().BeEquivalentTo(new
        {
            Id = recordId,
            PersonKey = "g:" + group,
            OwnerWpfCustomerId = owner,
            ScopeKey = Kit.Cumulative,
            ReplicaId = replica,
            MachineName = "PC-A",
            Kind = PaymentRequestKind.Lease,
            State = PaymentRequestState.Sending,
            ProductTotal = (decimal?)490m,
            TotalBeforeBalance = (decimal?)550m,
            NetAmount = (decimal?)null,
            ShippingSettingsVersion = (int?)4,
            FinishedAt = (DateTimeOffset?)null,
            Version = 0,
        });
        record.PersonKeyHash.Should().Equal(PaymentPersonKey.Hash("g:" + group));
        record.StartedAt.Should().BeOnOrAfter(before);
        record.LeaseUntil.Should().Be(record.StartedAt.AddSeconds(120));
        body.LeaseUntil.Should().Be(record.LeaseUntil);
        record.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(expectedMembers);
        record.Members.Should().AllSatisfy(m =>
        {
            m.LicenseId.Should().Be(kit.LicenseId);
            m.ScopeKey.Should().Be(Kit.Cumulative);
        });
        record.RecordOrders.Select(o => o.OrderId).Should().BeEquivalentTo(orders);
        record.RecordOrders.Should().AllSatisfy(o => o.LicenseId.Should().Be(kit.LicenseId));
    }

    /// <summary>§2 PersonKey: gruplanmamış GUID'li sahip <c>c:</c>, GUID'siz sahip <c>r:</c> (ham Id).</summary>
    [Fact]
    public async Task Gruplanmamis_ve_GUIDsiz_sahibin_kisi_anahtari()
    {
        var kit = await Kit.CreateAsync(_factory);
        var lone = await kit.SeedProjectionAsync("   ");
        var raw = $"Yerel-{Guid.NewGuid():N}";
        var loneRecord = Guid.NewGuid();
        var rawRecord = Guid.NewGuid();

        (await kit.BeginAsync(Kit.Body(loneRecord, Guid.NewGuid(), lone, [lone]))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.BeginAsync(Kit.Body(rawRecord, Guid.NewGuid(), raw, [raw]))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.RecordAsync(loneRecord))!.PersonKey.Should().Be("c:" + lone);
        var rawSaved = await kit.RecordAsync(rawRecord);
        rawSaved!.PersonKey.Should().Be("r:" + raw);
        rawSaved.OwnerWpfCustomerId.Should().Be(raw);
    }

    // ── (a) yeniden oynatma ──────────────────────────────────────────────────

    /// <summary>§6.4-a, §15-21: yanıtı kaybolan <c>begin</c> aynı gövdeyle yinelenir — kayıtlı sonuç, ikinci satır yok.
    /// Üyelerin sırası ve tutarın yazımı özeti değiştirmez; <c>seenMode</c> ve <c>confirmedPriorIds</c> özete girmez.</summary>
    [Fact]
    public async Task Ayni_govde_yeniden_oynatilir_ikinci_satir_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var other = Kit.NewId();
        var recordId = Guid.NewGuid();
        var replica = Guid.NewGuid();
        var order = await kit.SeedOrderAsync(owner, 100m);
        var first = await kit.BeginAsync(Kit.Body(recordId, replica, owner, [owner, other], orderIds: [order.Id],
            productTotal: 100m));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var again = Kit.Body(recordId, replica, owner, [other, owner], orderIds: [order.Id], productTotal: 100.00m,
            confirmedPriorIds: [Guid.NewGuid()]);
        again["seenMode"] = 0;
        var second = await kit.BeginAsync(again);

        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(second)).Should().BeEquivalentTo(await Kit.ReadAsync(first));
        (await kit.RecordsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// §6.4-a, e: <c>confirmedPriorsDigest</c>, <c>confirmedPriorIds</c> gibi istek özetine girmez. Yanıtı kaybolan isteğin
    /// yeniden gönderimi sonraki bir sorunun onayıyla (başka özet) ya da onaysız gelebilir; ikisi de aynı kaydı döner,
    /// ikinci satır yok.
    /// </summary>
    [Fact]
    public async Task Yeniden_oynatma_onay_ozetine_bakmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var recordId = Guid.NewGuid();
        var replica = Guid.NewGuid();
        var first = await BeginOkAsync(kit, Kit.Body(recordId, replica, owner, [owner],
            confirmedPriorsDigest: Kit.UnrelatedPriorsDigest()));

        var otherDigest = await BeginOkAsync(kit, Kit.Body(recordId, replica, owner, [owner],
            confirmedPriorsDigest: Kit.UnrelatedPriorsDigest()));
        var noDigest = await BeginOkAsync(kit, Kit.Body(recordId, replica, owner, [owner]));

        otherDigest.Should().BeEquivalentTo(first);
        noDigest.Should().BeEquivalentTo(first);
        (await kit.RecordsAsync()).Should().ContainSingle();
    }

    /// <summary>§6.4-a: yeniden oynatma kaydın GÜNCEL hâlini döner.</summary>
    [Fact]
    public async Task Yeniden_oynatma_kaydin_guncel_durumunu_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var body = Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]);
        (await kit.BeginAsync(body)).StatusCode.Should().Be(HttpStatusCode.OK);
        await kit.Sales.WithDbAsync(async db =>
        {
            var row = await db.PaymentRequestRecords.FindAsync((Guid)body["recordId"]!);
            row!.State = PaymentRequestState.Delivered;
        });

        var replay = await kit.BeginAsync(body);

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(replay)).State.Should().Be("delivered");
    }

    /// <summary>
    /// §6.4-a: süresi dolmuş kiranın yeniden oynatması kira değildir. Yanıtı kaybolan begin'i kira dolduktan sonra
    /// yineleyen kopya <c>sending</c> görseydi, kiranın geçerli olup olmadığını yalnız kendi saatiyle anlayabilirdi.
    /// Durum, liste ucundaki gibi sunucu saatiyle <c>unconfirmed</c> bildirilir; yeniden oynatma kilitsizdir ve satırı
    /// yazmaz (yazımı sonraki begin'in c adımı yapar).
    /// </summary>
    [Fact]
    public async Task Suresi_dolmus_kiranin_yeniden_oynatmasi_unconfirmed_doner_satir_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var body = Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]);
        var recordId = (Guid)body["recordId"]!;
        (await kit.BeginAsync(body)).StatusCode.Should().Be(HttpStatusCode.OK);
        await kit.Sales.WithDbAsync(async db =>
        {
            var row = await db.PaymentRequestRecords.FindAsync(recordId);
            row!.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });

        var replay = await kit.BeginAsync(body);

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(replay)).State.Should().Be("unconfirmed");
        var saved = await kit.RecordAsync(recordId);
        saved!.State.Should().Be(PaymentRequestState.Sending);
        saved.Version.Should().Be(0);
    }

    /// <summary>§6.4-a: aynı <c>recordId</c> farklı içerikle (tutar, kapsam, kopya) — 409 <c>content-conflict</c>; kayıt
    /// değişmez.</summary>
    [Theory]
    [InlineData("tutar")]
    [InlineData("kapsam")]
    [InlineData("kopya")]
    [InlineData("siparis")]
    public async Task Ayni_Id_farkli_icerik_409_content_conflict(string change)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var recordId = Guid.NewGuid();
        var replica = Guid.NewGuid();
        (await kit.BeginAsync(Kit.Body(recordId, replica, owner, [owner]))).StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = change switch
        {
            "tutar" => Kit.Body(recordId, replica, owner, [owner], productTotal: 551m),
            "kapsam" => Kit.Body(recordId, replica, owner, [owner], scopeKey: Kit.SessionScope(Guid.NewGuid())),
            "kopya" => Kit.Body(recordId, Guid.NewGuid(), owner, [owner]),
            "siparis" => Kit.Body(recordId, replica, owner, [owner], orderIds: [Guid.NewGuid()]),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var resp = await kit.BeginAsync(changed);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        var record = (await kit.RecordsAsync()).Should().ContainSingle().Subject;
        record.ProductTotal.Should().Be(0m);
        record.ReplicaId.Should().Be(replica);
    }

    /// <summary>§6.4-a: Id kirasız bir kaydın (<c>Record</c>) ya da başka lisansın — 409 <c>content-conflict</c>.</summary>
    [Fact]
    public async Task Id_kirasiz_kaydin_ya_da_baska_lisansin_ise_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var other = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var record = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, DateTimeOffset.UtcNow.AddHours(-1));
        var foreign = await other.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow);

        foreach (var id in new[] { record.Id, foreign.Id })
        {
            var resp = await kit.BeginAsync(Kit.Body(id, Guid.NewGuid(), owner, [owner]));

            resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
            (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        }
        (await kit.RecordsAsync()).Should().ContainSingle().Which.Id.Should().Be(record.Id);
    }

    // ── (b) members-stale ────────────────────────────────────────────────────

    /// <summary>§6.4-b, §10.1-17: istemcinin üye kümesi sunucu grubunu kapsamıyor (grup senkronu gecikmiş) — 409
    /// <c>members-stale</c>, sunucunun grubuyla; kira yazılmaz.</summary>
    [Fact]
    public async Task Sunucu_grubunun_uyesi_eksikse_409_members_stale()
    {
        var kit = await Kit.CreateAsync(_factory);
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(group);
        var missing = await kit.SeedProjectionAsync(group);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("members-stale");
        problem.GetProperty("members").EnumerateArray().Select(m => m.GetString())
            .Should().BeEquivalentTo([owner, missing]);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Ortak bakiye §15-7 (§4.0-3'ün kuralı): (b) sunucu grubunun yalnız CANLI üyelerini ister. Silinmiş üye sunucuda
    /// numarası boşalan grubun yeni numarasını izler (<c>PersonGroupSaveHook</c>), WPF'te izlemez: g1'in son canlı üyesi
    /// g0'a geçince kanca silinmiş üyeyi g0'a taşır, öbür bilgisayarın g0'ı onu içermez ve hiçbir müşteri akışı turu
    /// içermesini sağlamaz. Kira yine açılır; silinmiş üye kaydın üyelerine girer (silinmiş sahipli kayıtlar bulunur),
    /// toplama yalnız istemcinin üyeleri girer. Canlı üye eksikse yine 409 <c>members-stale</c>.
    /// </summary>
    [Fact]
    public async Task Sunucuda_tasinan_silinmis_uye_istemcide_yoksa_kira_acilir_canli_uye_eksikse_409()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = $"grp-{Guid.NewGuid():N}";
        var g0 = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(g1);
        var purged = await kit.SeedProjectionAsync(g1, purged: true);
        var sibling = await kit.SeedProjectionAsync(g0);
        // Silinmiş üyenin sayılan bir siparişi: toplama girseydi istemci (onu üye saymıyor) stale-total alırdı.
        await kit.SeedOrderAsync(purged, 70m);
        // group-customers ya da başka bilgisayarın Birleştir'i: g1'in son canlı üyesi g0'a geçer.
        await kit.GroupAsync(g0, owner);

        var stale = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict, await stale.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(stale);
        problem.GetProperty("title").GetString().Should().Be("members-stale", "canlı üye (sibling) eksik");
        problem.GetProperty("members").EnumerateArray().Select(m => m.GetString())
            .Should().BeEquivalentTo([owner, sibling, purged], "kanca silinmiş üyeyi g0'a taşıdı");

        var recordId = Guid.NewGuid();
        var body = await BeginOkAsync(kit, Kit.Body(recordId, Guid.NewGuid(), owner, [owner, sibling]));

        var expected = new[] { owner, sibling, purged }.Order(StringComparer.Ordinal).ToList();
        body.Members.Should().Equal(expected);
        (await kit.RecordAsync(recordId))!.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(expected);
    }

    // ── (d) in-progress-elsewhere ────────────────────────────────────────────

    /// <summary>§6.4-d: başka kopyanın canlı kirası — 409 <c>in-progress-elsewhere {machine, since, recordId}</c>; ikinci
    /// satır yazılmaz, öbür kira değişmez.</summary>
    [Fact]
    public async Task Baska_kopyanin_canli_kirasi_409_in_progress_elsewhere()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var pcA = Guid.NewGuid();
        await kit.SeedReplicaAsync(pcA, "PC-A");
        var first = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), pcA, owner, [owner]));
        var lease = await Kit.ReadAsync(first);
        var saved = await kit.RecordAsync(lease.RecordId);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("machine").GetString().Should().Be("PC-A");
        problem.GetProperty("since").GetDateTimeOffset().Should().Be(saved!.StartedAt);
        problem.GetProperty("recordId").GetGuid().Should().Be(lease.RecordId);
        var records = await kit.RecordsAsync();
        records.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { State = PaymentRequestState.Sending, Version = 0, FinishedAt = (DateTimeOffset?)null });
    }

    // ── (e) already-requested ────────────────────────────────────────────────

    /// <summary>
    /// §6.4-e: onaylanmamış teslim edilmiş ve sonucu bilinmeyen kayıtlar, iki türden de — 409 <c>already-requested</c>.
    /// <c>at</c> istek anıdır (kira: sunucunun başlangıç anı; kayıt: istemcinin anı, yalnız gösterim). Sıra, listedeki
    /// gibi sunucunun kapanış anına (<c>finishedAt</c>) göredir; iki bilgisayarın saati karşılaştırılmaz. Öncül kiranın
    /// istenen tutarını (<c>totalBeforeBalance</c>) da taşır. Vazgeçilmiş kayıt ve başka kapsamın kaydı girmez. Üye
    /// kesişimiyle bulunan (başka PersonKey) kayıt da girer. Hepsi onaylanınca kira yazılır.
    /// </summary>
    [Fact]
    public async Task Onceki_istekler_409_already_requested_onaylaninca_200()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var shared = Kit.NewId();
        var key = "c:" + owner;
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var deliveredLease = await kit.SeedRecordAsync(key, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, t0, finishedAt: t0.AddSeconds(30), netAmount: 300m, machineName: "PC-A",
            productTotal: 300m, totalBeforeBalance: 360m);
        var unconfirmedLease = await kit.SeedRecordAsync(key, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Unconfirmed, t0.AddHours(1), finishedAt: t0.AddHours(1).AddMinutes(2), netAmount: 120m,
            productTotal: 120m, totalBeforeBalance: 120m);
        // İstemcinin anı t0 + 2 sa; sunucu kaydı t0 + 5 sa'te aldı (geri doldurma): sırada üye kaydından sonra gelir.
        var deliveredRecord = await kit.SeedRecordAsync(key, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, t0.AddHours(2), finishedAt: t0.AddHours(5), netAmount: 80m);
        var viaMember = await kit.SeedRecordAsync("c:" + Kit.NewId(), [Kit.NewId(), shared], PaymentRequestKind.Record,
            PaymentRequestState.Unconfirmed, t0.AddHours(3), finishedAt: t0.AddHours(4), netAmount: 60m,
            machineName: null);
        await kit.SeedRecordAsync(key, [owner], PaymentRequestKind.Lease, PaymentRequestState.Abandoned, t0.AddHours(4),
            finishedAt: t0.AddHours(4));
        await kit.SeedRecordAsync(key, [owner], PaymentRequestKind.Lease, PaymentRequestState.Delivered, t0.AddHours(4),
            finishedAt: t0.AddHours(4), scopeKey: Kit.SessionScope(Guid.NewGuid()));

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner, shared]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        Priors(problem).Should().Equal(
            new Prior(deliveredLease.Id, "PC-A", 300m, 360m, t0, t0.AddSeconds(30), false),
            new Prior(unconfirmedLease.Id, "PC-X", 120m, 120m, t0.AddHours(1), t0.AddHours(1).AddMinutes(2), true),
            new Prior(viaMember.Id, null, 60m, null, t0.AddHours(3), t0.AddHours(4), true),
            new Prior(deliveredRecord.Id, "PC-X", 80m, null, t0.AddHours(2), t0.AddHours(5), false));
        (await kit.RecordsAsync()).Should().HaveCount(6, "ret kira yazmaz");

        var confirmed = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner, shared],
            confirmedPriorIds: [deliveredLease.Id, unconfirmedLease.Id, deliveredRecord.Id, viaMember.Id]));

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        (await kit.RecordsAsync()).Should().HaveCount(7);
    }

    /// <summary>
    /// §6.4-e: kayıt yalnız PersonKey'den bulunur — üye kümesi ayrık (öbür bilgisayar grubun yalnız ondaki GUID'siz bir
    /// üyesini yazmış). Özet aynı kişi×kapsamı bulur.
    /// </summary>
    [Fact]
    public async Task Uye_kumesi_ayrik_kayit_PersonKey_ile_bulunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(group);
        var prior = await kit.SeedRecordAsync("g:" + group, [$"yerel-{Guid.NewGuid():N}"], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, DateTimeOffset.UtcNow.AddHours(-1), netAmount: 40m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        Priors(await SalesKit.ProblemAsync(resp)).Select(p => p.Id).Should().Equal(prior.Id);
    }

    /// <summary>Kısmi onay yetmez: onaylanmamış kalan kayıt yine sorulur.</summary>
    [Fact]
    public async Task Kismi_onayda_kalan_kayit_yine_sorulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var first = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, t0, netAmount: 10m);
        var second = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, t0.AddHours(1), netAmount: 20m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            confirmedPriorIds: [first.Id]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        Priors(await SalesKit.ProblemAsync(resp)).Select(p => p.Id).Should().Equal(second.Id);
    }

    /// <summary>
    /// §6.4-e: kayıt, yazıldığı andaki asıl kaydın Id'siyle saklanır (PersonKey <c>c:t1</c>, üye <c>t1</c>); birleştirme
    /// onu yeniden anahtarlamaz. t1 t2'ye birleştirildikten sonra PC-B'nin begin'i kaydı yine bulur ve 409
    /// <c>already-requested</c> alır. İstek t2 ile ya da hâlâ t1 ile gelebilir (t1 t2'ye çözülür).
    /// </summary>
    [Fact]
    public async Task Birlesmeden_once_teslim_edilmis_istek_asil_kayitla_sorulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var t1 = await kit.SeedProjectionAsync(groupId: null);
        var t2 = await kit.SeedProjectionAsync(groupId: null);
        var pcA = Guid.NewGuid();
        var lease = await Kit.ReadAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), pcA, t1, [t1])));
        var finish = await kit.FinishAsync(lease.RecordId, Kit.FinishBody(pcA, "delivered", netAmount: 550m));
        finish.StatusCode.Should().Be(HttpStatusCode.OK, await finish.Content.ReadAsStringAsync());
        await kit.MergeAsync(t1, into: t2);

        var viaCanonical = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), t2, [t2]));
        var viaCopy = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), t1, [t1]));

        foreach (var resp in new[] { viaCanonical, viaCopy })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
            var problem = await SalesKit.ProblemAsync(resp);
            problem.GetProperty("title").GetString().Should().Be("already-requested");
            Priors(problem).Select(p => p.Id).Should().Equal(lease.RecordId);
        }
        (await kit.RecordsAsync()).Should().ContainSingle("ret kira yazmaz");
    }

    /// <summary>§6.4-d: birleştirmeden önce açılmış canlı kira, asıl kayıtla gelen başka kopyanın begin'inde 409
    /// <c>in-progress-elsewhere</c> olur.</summary>
    [Fact]
    public async Task Birlesmeden_once_acilmis_canli_kira_asil_kayitla_in_progress_elsewhere()
    {
        var kit = await Kit.CreateAsync(_factory);
        var t1 = await kit.SeedProjectionAsync(groupId: null);
        var t2 = await kit.SeedProjectionAsync(groupId: null);
        var lease = await Kit.ReadAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), t1, [t1])));
        await kit.MergeAsync(t1, into: t2);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), t2, [t2]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("recordId").GetGuid().Should().Be(lease.RecordId);
        (await kit.RecordsAsync()).Should().ContainSingle();
    }

    // ── (e) onay özeti (priorsDigest) ────────────────────────────────────────

    /// <summary>
    /// §6.4-e, §12-16: kişi×kapsamda 600 öncül. Id listesi en çok 500 Id taşıdığı için onları onaylayamaz; kapsam kalıcı
    /// olarak kilitlenirdi. 409 öncül kümesinin özetini (<c>priorsDigest</c>) taşır; onu boş Id listesiyle geri yollayan
    /// istek kirayı alır.
    /// </summary>
    [Fact]
    public async Task Alti_yuz_oncul_ozetle_onaylanir_kira_yazilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var ids = new List<Guid>();
        for (var i = 0; i < 600; i++) ids.Add((await SeedDeliveredAsync(kit, owner, t0.AddHours(i))).Id);

        var problem = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])));
        Priors(problem).Should().HaveCount(600);
        PriorsDigest(problem).Should().Be(Kit.ExpectedPriorsDigest(ids));

        var lease = await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            confirmedPriorsDigest: PriorsDigest(problem)));
        (await kit.RecordAsync(lease.RecordId))!.State.Should().Be(PaymentRequestState.Sending);
    }

    /// <summary>
    /// §6.4-e: soru ile onay arasında yeni bir öncül göründü — yeni kapandı ya da kapanış anı gösterilenlerden eski (geç
    /// commit edilmiş geri doldurma; gerçek iç içe geçiş SQL Server'da, <see cref="PaymentRequestBeginRelationalTests"/>).
    /// Onay yalnız gösterilen kümeyi kapsar, saate bakmaz: özet tutmaz, istek yeniden sorulur — yeni öncül listede, özet
    /// farklı. Yeni özetle kira yazılır.
    /// </summary>
    [Theory]
    [InlineData("yeni")]
    [InlineData("erken-damgali")]
    public async Task Soru_ile_onay_arasinda_gorunen_oncul_yeniden_sorulur_yeni_ozetle_200(string @case)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var replica = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var first = await SeedDeliveredAsync(kit, owner, t0);
        var second = await SeedDeliveredAsync(kit, owner, t0.AddHours(1));
        var question = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner])));
        Priors(question).Select(p => p.Id).Should().Equal(first.Id, second.Id);

        var appeared = await SeedDeliveredAsync(kit, owner,
            @case == "yeni" ? DateTimeOffset.UtcNow : t0.AddMinutes(30));
        var stale = await AlreadyRequestedAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner],
            confirmedPriorsDigest: PriorsDigest(question))));

        Guid[] byFinishedAt = @case == "yeni"
            ? [first.Id, second.Id, appeared.Id]
            : [first.Id, appeared.Id, second.Id];
        Priors(stale).Select(p => p.Id).Should().Equal(byFinishedAt);
        PriorsDigest(stale).Should().NotBe(PriorsDigest(question))
            .And.Be(Kit.ExpectedPriorsDigest([first.Id, second.Id, appeared.Id]));
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), replica, owner, [owner],
            confirmedPriorsDigest: PriorsDigest(stale)));
    }

    /// <summary>
    /// §6.4-e: soru ile onay arasında kişinin kümesine ESKİ bir kayıt katıldı — kapanış anı gösterilen öncülden eski,
    /// soru anında başka bir satırın kaydıydı. Üç yoldan: birleştirmede (kimlik onarımı) kayıt kopyanın Id'siyle saklıdır
    /// ve üye kesişimi onu kopyalar üzerinden bulur (<c>WithCopiesAsync</c>); grup değişiminde istemci kişiyi yeniden
    /// kurar, öbür satır üye olur; geri doldurmanın aynı işi yeniden göndermesi kayda yeni üye ekler (<c>UnionMembers</c>;
    /// grup senkronu başka türlü görmüş bir bilgisayar). Küme değiştiği için özet tutmaz ve kayıt sorulur; bir zaman
    /// filigranı onu yutardı.
    /// </summary>
    [Theory]
    [InlineData("birlesme")]
    [InlineData("grup-degisimi")]
    [InlineData("geri-doldurma-uye-birlesimi")]
    public async Task Soru_ile_onay_arasinda_kumeye_katilan_eski_kayit_yutulmaz(string @case)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = await kit.SeedProjectionAsync(groupId: null);
        var other = await kit.SeedProjectionAsync(groupId: null);
        var replica = Guid.NewGuid();
        var oldAt = DateTimeOffset.UtcNow.AddDays(-3);
        // Geri doldurmanın yinelemesi yalnız aynı içerikli kayda üye ekler: o durumda eski kayıt da geri doldurmayla
        // yazılır (kapanış anı sunucunun alış anı, gösterilen öncülden önce).
        var old = @case == "geri-doldurma-uye-birlesimi"
            ? await BackfilledAsync(kit, Kit.BackfillItem(Guid.NewGuid(), other, [other], oldAt, netAmount: 40m))
            : await kit.SeedRecordAsync("c:" + other, [other], PaymentRequestKind.Record,
                PaymentRequestState.Delivered, oldAt.AddMinutes(-1), finishedAt: oldAt, netAmount: 40m);
        var shown = await SeedDeliveredAsync(kit, owner, DateTimeOffset.UtcNow);
        var question = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner])));
        Priors(question).Select(p => p.Id).Should().Equal(shown.Id);

        string[] members;
        switch (@case)
        {
            case "birlesme":
                await kit.MergeAsync(other, into: owner);
                members = [owner];
                break;
            case "grup-degisimi":
                await kit.GroupAsync($"grp-{Guid.NewGuid():N}", owner, other);
                members = [owner, other];
                break;
            case "geri-doldurma-uye-birlesimi":
                var replay = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
                    Kit.BackfillItem(old.Id, other, [other, owner], oldAt, netAmount: 40m)));
                replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
                (await Kit.ReadBackfillAsync(replay)).Records.Should().Equal(new Kit.BackfillResult(old.Id, "unchanged"));
                members = [owner];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(@case), @case, null);
        }
        var confirm = await AlreadyRequestedAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, members,
            confirmedPriorsDigest: PriorsDigest(question))));

        Priors(confirm).Select(p => p.Id).Should().Equal(old.Id, shown.Id);
        PriorsDigest(confirm).Should().Be(Kit.ExpectedPriorsDigest([old.Id, shown.Id]));
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), replica, owner, members,
            confirmedPriorsDigest: PriorsDigest(confirm)));
    }

    /// <summary>
    /// §6.4-e: soru ile onay arasında bir öncül kümeden ÇIKTI — sonucu bilinmeyen kira geç <c>abandoned</c> ile kapandı.
    /// Onay küme eşitliğidir: sorunun özeti tutmaz ve istek kalan öncülle yeniden sorulur. Id listesi bunu sormazdı;
    /// özet bilerek daha katıdır, fazladan bir soru güvenli yöndedir. Yeni özetle kira yazılır.
    /// </summary>
    [Fact]
    public async Task Soru_ile_onay_arasinda_kumeden_cikan_oncul_yeniden_sorulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var pcA = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var delivered = await SeedDeliveredAsync(kit, owner, t0);
        var lease = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Unconfirmed, t0.AddHours(1), finishedAt: t0.AddHours(1).AddMinutes(2), replicaId: pcA,
            machineName: "PC-A");
        var question = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])));
        Priors(question).Select(p => p.Id).Should().Equal(delivered.Id, lease.Id);

        var abandon = await kit.FinishAsync(lease.Id, Kit.FinishBody(pcA, "abandoned"));
        abandon.StatusCode.Should().Be(HttpStatusCode.OK, await abandon.Content.ReadAsStringAsync());
        var confirm = await AlreadyRequestedAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner,
            [owner], confirmedPriorsDigest: PriorsDigest(question))));

        Priors(confirm).Select(p => p.Id).Should().Equal(delivered.Id);
        PriorsDigest(confirm).Should().NotBe(PriorsDigest(question))
            .And.Be(Kit.ExpectedPriorsDigest([delivered.Id]));
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            confirmedPriorsDigest: PriorsDigest(confirm)));
    }

    /// <summary>
    /// §6.4-e: özet Id listesinden bağımsızdır — Id listesiyle onaylananlar dahil bütün öncül kümesinin özetidir. İki onay
    /// "ya da" ile birleşir: Id listesinin dışında öncül kalmadıysa geçer; kaldıysa özet bütün kümeyle tutmalıdır. Onay
    /// turunda Id listesi değişse de (başka bir alt küme, 500'de kesilmiş birikimli liste ya da hiç) tutan özet geçer:
    /// soru döngüye girmez. Bütün öncülleri kapsayan Id listesinde özete bakılmaz (bugünkü davranış).
    /// </summary>
    [Theory]
    [InlineData("baska-alt-kume")]
    [InlineData("id-yok")]
    [InlineData("hepsi-idli-ilgisiz-ozet")]
    public async Task Id_listesi_ve_ozet_birlikte_dongusuz(string @case)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var p1 = await SeedDeliveredAsync(kit, owner, t0);
        var p2 = await SeedDeliveredAsync(kit, owner, t0.AddHours(1));
        var p3 = await SeedDeliveredAsync(kit, owner, t0.AddHours(2));

        var withIds = await AlreadyRequestedAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner,
            [owner], confirmedPriorIds: [p3.Id])));
        var withoutIds = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])));
        Priors(withIds).Select(p => p.Id).Should().Equal(p1.Id, p2.Id);
        PriorsDigest(withIds).Should().Be(PriorsDigest(withoutIds))
            .And.Be(Kit.ExpectedPriorsDigest([p1.Id, p2.Id, p3.Id]));

        var (ids, digest) = @case switch
        {
            "baska-alt-kume" => (new[] { p1.Id }, PriorsDigest(withIds)),
            "id-yok" => (Array.Empty<Guid>(), PriorsDigest(withIds)),
            "hepsi-idli-ilgisiz-ozet" => (new[] { p1.Id, p2.Id, p3.Id }, Kit.UnrelatedPriorsDigest()),
            _ => throw new ArgumentOutOfRangeException(nameof(@case), @case, null),
        };
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner], confirmedPriorIds: ids,
            confirmedPriorsDigest: digest));
    }

    /// <summary>
    /// §6.4-e: özet Id kümesinindir; okuma ve gösterim sırasından bağımsız, deterministiktir. Öncüller kapanış anına göre
    /// gösterilir; burada bu sıra Id'lerin kanonik sırasının (küçük harf "N", ordinal) tersidir. Sonucu bilinmeyen kiraya
    /// geç <c>delivered</c> gelir: kapanış anı yenilenir, kira listenin sonuna geçer. Id kümesi aynı kaldığı için özet
    /// değişmez (Id listesinin anlamıyla eşdeğer: o da durum değişiminde yeniden sormaz) ve ilk sorunun özeti hâlâ geçer.
    /// </summary>
    [Fact]
    public async Task Ozet_siradan_bagimsiz_gec_finish_ozeti_degistirmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var pcA = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        var ids = Enumerable.Range(0, 3)
            .Select(_ => Guid.NewGuid())
            .OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < ids.Count; i++)
            await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record, PaymentRequestState.Delivered,
                t0.AddHours(i).AddMinutes(-1), finishedAt: t0.AddHours(i), netAmount: 10m, id: ids[i]);
        var lease = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Unconfirmed, t0.AddHours(-2), finishedAt: t0.AddHours(-1), replicaId: pcA,
            machineName: "PC-A");

        var first = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])));
        var late = await kit.FinishAsync(lease.Id, Kit.FinishBody(pcA, "delivered", netAmount: 10m));
        late.StatusCode.Should().Be(HttpStatusCode.OK, await late.Content.ReadAsStringAsync());
        var second = await AlreadyRequestedAsync(
            await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])));

        Priors(first).Select(p => p.Id).Should().Equal([lease.Id, .. ids]);
        Priors(second).Select(p => p.Id).Should().Equal([.. ids, lease.Id]);
        Priors(second)[^1].Unconfirmed.Should().BeFalse();
        PriorsDigest(second).Should().Be(PriorsDigest(first)).And.Be(Kit.ExpectedPriorsDigest([.. ids, lease.Id]));
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            confirmedPriorsDigest: PriorsDigest(first)));
    }

    // ── (c) süre dolumu ──────────────────────────────────────────────────────

    /// <summary>
    /// §6.4-c, §10.1-15: başka kopyanın kirası teslim kaydı olmadan doldu — kayıt Unconfirmed olur (Version + 1) ve
    /// <c>already-requested</c>'da sonucu bilinmeyen olarak döner; yazım retten sonra da kalır. Kapanış anı kiranın
    /// dolduğu andır (listenin bildirdiği an), onu yazan begin'in anı değil. Öncül istek anını ve istenen tutarı taşır
    /// (§6.8: "PC-A'dan 550 ₺ istenmiş olabilir"); finish gelmediği için <c>netAmount</c> yok.
    /// </summary>
    [Fact]
    public async Task Suresi_dolmus_kira_Unconfirmed_olur_already_requested_unconfirmed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var expired = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow.AddMinutes(-30), machineName: "PC-A",
            productTotal: 550m, totalBeforeBalance: 550m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        var saved = await kit.RecordAsync(expired.Id);
        saved!.State.Should().Be(PaymentRequestState.Unconfirmed);
        saved.Version.Should().Be(1);
        saved.FinishedAt.Should().Be(expired.LeaseUntil);
        Priors(problem).Should().Equal(
            new Prior(expired.Id, "PC-A", null, 550m, expired.StartedAt, expired.LeaseUntil, true));
    }

    /// <summary>
    /// Süresi dolmuş kiranın kapanış anı, satır yazılmadan önce ve sonra aynıdır: liste onu kira sonuyla bildirir,
    /// başka kopyanın begin'i (c) satırı aynı anla yazar. Kaydın sırası, begin'in ne zaman çağrıldığına göre kaymaz.
    /// </summary>
    [Fact]
    public async Task Suresi_dolmus_kiranin_kapanis_ani_yazimdan_once_ve_sonra_ayni()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var expired = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow.AddMinutes(-30), machineName: "PC-A");
        var before = (await Kit.ReadListAsync(await kit.ListAsync(Kit.Cumulative, [owner]))).Records.Single();

        (await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var after = (await Kit.ReadListAsync(await kit.ListAsync(Kit.Cumulative, [owner]))).Records.Single();

        (await kit.RecordAsync(expired.Id))!.State.Should().Be(PaymentRequestState.Unconfirmed);
        before.State.Should().Be("unconfirmed");
        before.FinishedAt.Should().Be(expired.LeaseUntil);
        after.State.Should().Be("unconfirmed");
        after.FinishedAt.Should().Be(before.FinishedAt);
    }

    /// <summary>
    /// §6.4-c, §10.1-16: aynı kopya aynı kişi×kapsamda yeni bir <c>recordId</c> ile başlar (süreç çöktü, mesaj gitmiş
    /// olabilir) — eski canlı kirası Unconfirmed olur, <c>already-requested</c> sorulur; onaylanınca yeni kira yazılır.
    /// </summary>
    [Fact]
    public async Task Ayni_kopyanin_yeni_istegi_eski_kirayi_Unconfirmed_yapar_onaylaninca_200()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var replica = Guid.NewGuid();
        var old = await Kit.ReadAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner])));

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        Priors(problem).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = old.RecordId, Unconfirmed = true });
        var closed = await kit.RecordAsync(old.RecordId);
        closed!.State.Should().Be(PaymentRequestState.Unconfirmed);
        closed.FinishedAt.Should().BeBefore(closed.LeaseUntil!.Value, "süresi dolmamış kira yeniden başlama anında kapanır");

        var confirmed = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner],
            confirmedPriorIds: [old.RecordId]));

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        (await kit.RecordsAsync()).Where(r => r.State == PaymentRequestState.Sending).Should().ContainSingle()
            .Which.Id.Should().Be((await Kit.ReadAsync(confirmed)).RecordId);
    }

    // ── (f) sunucu toplamı ───────────────────────────────────────────────────

    /// <summary>§6.4-f, M11: küme aynı, toplam farklı (bir etiketin fiyatı bu bilgisayarda eski) — 409 <c>stale-total</c>
    /// sunucu toplamıyla; fark başka kopyanın yayınında değil, <c>liveElsewhere</c> boş. Kira yazılmaz.</summary>
    [Fact]
    public async Task Farkli_toplam_409_stale_total_sunucu_toplamiyla()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var first = await kit.SeedOrderAsync(owner, 100m);
        var second = await kit.SeedOrderAsync(owner, 150m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [first.Id, second.Id], productTotal: 200m));

        var problem = await StaleTotalAsync(resp, 250m);
        LiveElsewhere(problem).Should().BeNull();
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>§6.4-f, §10.1-18: toplamlar eşit, kümeler farklı — istemcinin saydığı etiket sunucuda iptal edilmiş,
    /// sunucuda istemcinin görmediği yeni bir etiket var. Küme karşılaştırması bunu yakalar: 409
    /// <c>stale-total</c>.</summary>
    [Fact]
    public async Task Esit_toplam_farkli_kume_409_stale_total()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var kept = await kit.SeedOrderAsync(owner, 100m);
        var cancelled = await kit.SeedOrderAsync(owner, 100m, shape: o => o.CancelledAt = SalesKit.StartedAt.AddHours(1));
        await kit.SeedOrderAsync(owner, 100m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [kept.Id, cancelled.Id], productTotal: 200m));

        await StaleTotalAsync(resp, 200m);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>§4.11 <c>Contribution</c>: basılmamış, iptal edilmiş ve geçici yedek etiket sunucu toplamına girmez —
    /// onları sayan istek 409 <c>stale-total</c>, saymayan 200.</summary>
    [Fact]
    public async Task Basilmamis_iptal_ve_gecici_yedek_sayilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var printed = await kit.SeedOrderAsync(owner, 100m);
        var unprinted = await kit.SeedOrderAsync(owner, 50m, shape: o => o.PrintedAt = null);
        var cancelled = await kit.SeedOrderAsync(owner, 30m, shape: o => o.CancelledAt = SalesKit.StartedAt.AddHours(1));
        var tentative = await kit.SeedOrderAsync(owner, 20m, shape: o => o.IsTentativeBackup = true);

        var all = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [printed.Id, unprinted.Id, cancelled.Id, tentative.Id], productTotal: 200m));
        await StaleTotalAsync(all, 100m);

        var lease = await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [printed.Id], productTotal: 100m));
        (await kit.RecordAsync(lease.RecordId))!.RecordOrders.Select(o => o.OrderId).Should().Equal(printed.Id);
    }

    /// <summary>§6.4-f, §6.3: <c>session:{id}</c> yalnız o yayının siparişlerini sayar; <c>cumulative</c> bütün
    /// yayınlarınkini.</summary>
    [Fact]
    public async Task Yayin_kapsami_baska_yayini_saymaz_kumulatif_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var s1 = await kit.SeedReleasedSessionAsync();
        var inS1 = await kit.SeedOrderAsync(owner, 100m, s1);
        var inS2 = await kit.SeedOrderAsync(owner, 50m);
        var session = Kit.SessionScope(s1);

        await StaleTotalAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner], session,
            orderIds: [inS1.Id, inS2.Id], productTotal: 150m)), 100m);
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner], session,
            orderIds: [inS1.Id], productTotal: 100m));

        await StaleTotalAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [inS1.Id], productTotal: 100m)), 150m);
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [inS1.Id, inS2.Id], productTotal: 150m));
    }

    /// <summary>§6.4-f: birleştirmeden önce kopyanın (ve kopyanın kopyasının) Id'siyle saklanmış siparişler asıl kayıt
    /// üzerinden sayılır.</summary>
    [Fact]
    public async Task Kopya_Idsiyle_saklanan_siparis_asil_kayitla_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = await kit.SeedProjectionAsync(null);
        var copy = await kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(owner, "N"));
        var copyOfCopy = await kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(copy, "N"));
        var viaCopy = await kit.SeedOrderAsync(copy, 80m);
        var viaChain = await kit.SeedOrderAsync(copyOfCopy, 20m);

        await StaleTotalAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner])), 100m);

        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [viaCopy.Id, viaChain.Id], productTotal: 100m));
    }

    /// <summary>§6.4-f: yayınsız (<c>SessionId</c> NULL) sipariş hiçbir istemciye inmez; sayılsaydı <c>stale-total</c>
    /// kalıcı olurdu. Sayılmaz.</summary>
    [Fact]
    public async Task Yayinsiz_siparis_sayilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var inSession = await kit.SeedOrderAsync(owner, 40m);
        var orphan = await kit.Sales.SeedOrderAsync(null, o =>
        {
            o.CustomerId = owner;
            o.Price = 100m;
        });

        await StaleTotalAsync(await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [orphan.Id, inSession.Id], productTotal: 140m)), 40m);
        await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [inSession.Id], productTotal: 40m));
    }

    /// <summary>
    /// §6.4-f, §6.8: fark başka kopyanın devredilmemiş (canlı ya da devrediliyor) yayınındaki siparişten — PC-B'nin
    /// kümülatif isteği 350 ₺ görür, sunucu 470 ₺. 409 <c>stale-total {470, liveElsewhere: [{yayın, PC-A}]}</c>.
    /// </summary>
    [Theory]
    [InlineData(SalesKit.Phase.Live)]
    [InlineData(SalesKit.Phase.HandingOver)]
    public async Task Fark_baska_kopyanin_devredilmemis_yayinindaysa_liveElsewhere_dolu(SalesKit.Phase phase)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var seen = await kit.SeedOrderAsync(owner, 350m);
        var live = await kit.Sales.SeedSessionAsync(phase);
        await kit.SeedOrderAsync(owner, 120m, live);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [seen.Id], productTotal: 350m));

        var problem = await StaleTotalAsync(resp, 470m);
        LiveElsewhere(problem).Should().Equal(new LiveSession(live, "PC-A"));
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// §6.4-f: <c>liveElsewhere</c> yalnız farkın TAMAMI başka kopyanın devredilmemiş yayınındaysa dolar. Fark devredilmiş
    /// bir yayında, çağıranın kendi canlı yayınında ya da kısmen devredilmiş yayındaysa boş: istemci bir akış turuyla
    /// yetişebilir.
    /// </summary>
    [Theory]
    [InlineData("devredilmis")]
    [InlineData("kendi-canli-yayini")]
    [InlineData("karisik")]
    public async Task Fark_devredilmis_ya_da_kendi_yayinindaysa_liveElsewhere_bos(string @case)
    {
        var kit = await Kit.CreateAsync(_factory);
        var owner = Kit.NewId();
        var caller = Guid.NewGuid();
        var seen = await kit.SeedOrderAsync(owner, 350m);
        switch (@case)
        {
            case "devredilmis":
                await kit.SeedOrderAsync(owner, 120m);
                break;
            case "kendi-canli-yayini":
                await kit.SeedOrderAsync(owner, 120m, await kit.Sales.SeedSessionAsync(SalesKit.Phase.Live, owner: caller));
                break;
            case "karisik":
                await kit.SeedOrderAsync(owner, 70m, await kit.Sales.SeedSessionAsync(SalesKit.Phase.Live));
                await kit.SeedOrderAsync(owner, 50m);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(@case), @case, null);
        }

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), caller, owner, [owner],
            orderIds: [seen.Id], productTotal: 350m));

        LiveElsewhere(await StaleTotalAsync(resp, 470m)).Should().BeNull();
    }

    // ── (g) kargo ayarı ──────────────────────────────────────────────────────

    /// <summary>§6.2, §6.4-g: istemcinin kargo ayarı sürümü güncel değil (ya da ayar hiç yazılmamışken sürüm 0 değil) —
    /// 409 <c>settings-stale</c>, güncel ayarla; kira yazılmaz.</summary>
    [Fact]
    public async Task Kargo_ayari_surumu_eskiyse_409_settings_stale_guncel_ayarla()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedShippingAsync(3, threshold: 500m, fee: 60m);
        var owner = Kit.NewId();
        var order = await kit.SeedOrderAsync(owner, 490m);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [order.Id], productTotal: 490m, totalBeforeBalance: 550m, shippingSettingsVersion: 2));

        var current = (await SettingsStaleAsync(resp)).GetProperty("current");
        current.GetProperty("version").GetInt32().Should().Be(3);
        current.GetProperty("freeShippingThreshold").GetDecimal().Should().Be(500m);
        current.GetProperty("fee").GetDecimal().Should().Be(60m);
        (await kit.RecordsAsync()).Should().BeEmpty();

        var unset = await Kit.CreateAsync(_factory);
        var missing = await unset.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            shippingSettingsVersion: 1));
        var none = (await SettingsStaleAsync(missing)).GetProperty("current");
        none.GetProperty("version").GetInt32().Should().Be(0);
        none.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
        (await unset.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>Kargo testlerinin durumları: ayar açıksa (eşik 500, ücret 60) sürüm 2 ile yazılır; kapalıysa satır yok,
    /// sürüm 0.</summary>
    public static TheoryData<string, bool, decimal, decimal, bool> WrongShippingTotals => new()
    {
        { "esik-alti-kargosuz", true, 490m, 490m, false },
        { "esikte-kargolu", true, 500m, 560m, false },
        { "esik-ustu-kargolu", true, 550m, 610m, false },
        { "alici-oder-kargolu", true, 490m, 550m, true },
        { "ayar-kapali-kargolu", false, 490m, 550m, false },
    };

    /// <summary>§6.2, §6.4-g: sürüm doğru ama <c>totalBeforeBalance ≠ productTotal + kargo</c> (yerel ayarla
    /// hesaplanmış tutar) — 409 <c>settings-stale</c>, güncel ayarla; kira yazılmaz.</summary>
    [Theory]
    [MemberData(nameof(WrongShippingTotals))]
    public async Task Kargo_tutari_ayarla_tutmuyorsa_409_settings_stale(
        string @case, bool shippingOn, decimal productTotal, decimal totalBeforeBalance, bool recipientPays)
    {
        var (kit, owner, order, version) = await ShippingCaseAsync(shippingOn, productTotal);

        var resp = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [order], productTotal: productTotal, totalBeforeBalance: totalBeforeBalance,
            recipientPays: recipientPays, shippingSettingsVersion: version));

        (await SettingsStaleAsync(resp)).GetProperty("current").GetProperty("version").GetInt32()
            .Should().Be(version, @case);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    public static TheoryData<string, bool, decimal, decimal, bool> RightShippingTotals => new()
    {
        { "esik-alti-kargolu", true, 490m, 550m, false },
        { "esikte-ucretsiz", true, 500m, 500m, false },
        { "esik-ustu-ucretsiz", true, 550m, 550m, false },
        { "alici-oder-kargosuz", true, 490m, 490m, true },
        { "ayar-kapali", false, 490m, 490m, false },
    };

    /// <summary>§6.2, §6.4-g: kargo <c>ComputeShipping</c> kuralıyla — alıcı ödemeli, ayar kapalı ya da eşik ve üstü
    /// ise 0, değilse ücret. Tutan tutar 200 alır.</summary>
    [Theory]
    [MemberData(nameof(RightShippingTotals))]
    public async Task Kargo_tutari_ayarla_tutuyorsa_200(
        string @case, bool shippingOn, decimal productTotal, decimal totalBeforeBalance, bool recipientPays)
    {
        var (kit, owner, order, version) = await ShippingCaseAsync(shippingOn, productTotal);

        var lease = await BeginOkAsync(kit, Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner],
            orderIds: [order], productTotal: productTotal, totalBeforeBalance: totalBeforeBalance,
            recipientPays: recipientPays, shippingSettingsVersion: version));

        var record = await kit.RecordAsync(lease.RecordId);
        record!.TotalBeforeBalance.Should().Be(totalBeforeBalance, @case);
        record.ShippingSettingsVersion.Should().Be(version);
    }

    // ── Denetim sırası ───────────────────────────────────────────────────────

    /// <summary>
    /// §6.4 sırası b, c, d, e, f, g, h: toplam da kargo ayarı da yanlışken önceki adımın reddi döner —
    /// <c>members-stale</c>, <c>in-progress-elsewhere</c> ve <c>already-requested</c> <c>stale-total</c>'dan,
    /// <c>stale-total</c> <c>settings-stale</c>'dan önce gelir. İstek kira yazmaz.
    /// </summary>
    [Theory]
    [InlineData("members-stale")]
    [InlineData("in-progress-elsewhere")]
    [InlineData("already-requested")]
    [InlineData("stale-total")]
    public async Task Onceki_adimin_reddi_toplam_denetiminden_once_gelir(string expected)
    {
        var kit = await Kit.CreateAsync(_factory);
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(expected == "members-stale" ? group : null);
        await kit.SeedShippingAsync(2, threshold: 500m, fee: 60m);
        await kit.SeedOrderAsync(owner, 100m);
        switch (expected)
        {
            case "members-stale":
                await kit.SeedProjectionAsync(group);
                break;
            case "in-progress-elsewhere":
                await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease, PaymentRequestState.Sending,
                    DateTimeOffset.UtcNow);
                break;
            case "already-requested":
                await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record, PaymentRequestState.Delivered,
                    DateTimeOffset.UtcNow.AddHours(-1), netAmount: 100m);
                break;
        }
        var recordId = Guid.NewGuid();

        // Sipariş kümesi boş ve toplam 0 (sunucu: bir sipariş, 100), kargo ayarı sürümü 1 (güncel: 2).
        var resp = await kit.BeginAsync(Kit.Body(recordId, Guid.NewGuid(), owner, [owner], shippingSettingsVersion: 1));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(expected);
        (await kit.RecordAsync(recordId)).Should().BeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Prior(
        Guid Id,
        string? Machine,
        decimal? NetAmount,
        decimal? TotalBeforeBalance,
        DateTimeOffset At,
        DateTimeOffset? FinishedAt,
        bool Unconfirmed);

    private sealed record LiveSession(Guid SessionId, string? Machine);

    private static async Task<Kit.BeginResponse> BeginOkAsync(Kit kit, object body)
    {
        var resp = await kit.BeginAsync(body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return await Kit.ReadAsync(resp);
    }

    /// <summary>409 <c>stale-total</c>, verilen sunucu toplamıyla; ProblemDetails gövdesi.</summary>
    private static async Task<JsonElement> StaleTotalAsync(HttpResponseMessage resp, decimal serverTotal)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("stale-total");
        problem.GetProperty("serverTotal").GetDecimal().Should().Be(serverTotal);
        return problem;
    }

    /// <summary>409 <c>already-requested</c>; ProblemDetails gövdesi.</summary>
    private static async Task<JsonElement> AlreadyRequestedAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        return problem;
    }

    /// <summary><c>already-requested</c>'ın öncül kümesi özeti.</summary>
    private static string PriorsDigest(JsonElement problem) => problem.GetProperty("priorsDigest").GetString()!;

    /// <summary>Teslim edilmiş kirasız bir öncül; istek anı kapanıştan bir dakika önce.</summary>
    private static Task<PaymentRequestRecord> SeedDeliveredAsync(Kit kit, string owner, DateTimeOffset finishedAt) =>
        kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record, PaymentRequestState.Delivered,
            finishedAt.AddMinutes(-1), finishedAt: finishedAt, netAmount: 10m);

    /// <summary>Geri doldurmayla yazılmış kirasız bir öncül (öğe <c>applied</c>); kaydın yazılmış hâli.</summary>
    private static async Task<PaymentRequestRecord> BackfilledAsync(Kit kit, Dictionary<string, object?> item)
    {
        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(), item));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var result = (await Kit.ReadBackfillAsync(resp)).Records.Should().ContainSingle().Subject;
        result.Status.Should().Be("applied");
        return (await kit.RecordAsync(result.RecordId))!;
    }

    /// <summary>409 <c>settings-stale</c>; ProblemDetails gövdesi.</summary>
    private static async Task<JsonElement> SettingsStaleAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("settings-stale");
        return problem;
    }

    private static List<LiveSession>? LiveElsewhere(JsonElement problem)
    {
        var value = problem.GetProperty("liveElsewhere");
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.EnumerateArray()
                .Select(s => new LiveSession(
                    s.GetProperty("sessionId").GetGuid(),
                    s.GetProperty("machine").ValueKind == JsonValueKind.Null ? null : s.GetProperty("machine").GetString()))
                .ToList();
    }

    /// <summary>Kargo testlerinin kurulumu: ayar açıksa sürüm 2 (eşik 500, ücret 60), değilse satır yok (sürüm 0);
    /// sahibin toplamı <paramref name="productTotal"/> olan tek sipariş.</summary>
    private async Task<(Kit Kit, string Owner, Guid Order, int Version)> ShippingCaseAsync(
        bool shippingOn, decimal productTotal)
    {
        var kit = await Kit.CreateAsync(_factory);
        if (shippingOn) await kit.SeedShippingAsync(2, threshold: 500m, fee: 60m);
        var owner = Kit.NewId();
        var order = await kit.SeedOrderAsync(owner, productTotal);
        return (kit, owner, order.Id, shippingOn ? 2 : 0);
    }

    private static List<Prior> Priors(JsonElement problem) =>
        problem.GetProperty("priors").EnumerateArray()
            .Select(p => new Prior(
                p.GetProperty("id").GetGuid(),
                p.GetProperty("machine").ValueKind == JsonValueKind.Null ? null : p.GetProperty("machine").GetString(),
                p.GetProperty("netAmount").ValueKind == JsonValueKind.Null ? null : p.GetProperty("netAmount").GetDecimal(),
                p.GetProperty("totalBeforeBalance").ValueKind == JsonValueKind.Null
                    ? null
                    : p.GetProperty("totalBeforeBalance").GetDecimal(),
                p.GetProperty("at").GetDateTimeOffset(),
                p.GetProperty("finishedAt").ValueKind == JsonValueKind.Null
                    ? null
                    : p.GetProperty("finishedAt").GetDateTimeOffset(),
                p.GetProperty("unconfirmed").GetBoolean()))
            .ToList();
}
