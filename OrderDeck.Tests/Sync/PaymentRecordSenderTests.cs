using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Bekleyen ödeme kayıtlarının gönderimi (spec 2026-10-13 §3.2, §4.7, §6.4 "Akışın sonu" ve "Ödeme geçmişi"; W3-05),
/// betikli HTTP ile: <c>finish</c> 200 satırı siler; çevrimdışı satırı tutar, deneme sayılır ve son hata yazılır;
/// <c>record-not-found</c> ve <c>content-conflict</c> son durumdur (silinir); kirasız kayıtlar 200'lük partilerle gider,
/// <c>applied</c>/<c>unchanged</c> siler; zarf hazır değilse ya da kopya kimliği yoksa hiç istek gitmez; tek kapı:
/// eşzamanlı iki gönderim her kaydı bir kez yollar. Testler mod Off'ta koşar (bugün her lisans): kuyruk iki kipte gider.
/// </summary>
public sealed class PaymentRecordSenderTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Clock : IClock
    {
        private long _now = 1_760_000_000L;
        public long UnixNow() => Interlocked.Increment(ref _now);
    }

    private sealed record Call(string Kind, Guid? RecordId, JsonElement Body);

    private readonly string _lisans = $"lisans-{Guid.NewGuid():N}";
    private readonly Guid _licenseId = Guid.NewGuid();
    private readonly InMemorySqlite _db = new();
    private readonly License _license;
    private readonly PendingPaymentRecordRepository _repo;
    private readonly SalesSyncEnvelope _envelope;
    private readonly PaymentRecordSender _sender;
    private readonly CapturingLogger<PaymentRecordSender> _log = new();
    private readonly List<Call> _calls = new();
    private int _httpCount;

    /// <summary>Bir <c>finish</c>'in yanıtı (testin senaryosu); null = 200 yankı.</summary>
    private Func<Guid, Task<HttpResponseMessage>?>? _finish;

    /// <summary>Kirasız kaydın öğe sonucu; listede yoksa <c>applied</c>.</summary>
    private readonly Dictionary<Guid, string> _itemStatus = new();

    /// <summary>Geri doldurma partisinin bütün yanıtı (testin senaryosu); null = öğe başına sonuç.</summary>
    private Func<HttpResponseMessage?>? _backfill;

    public PaymentRecordSenderTests()
    {
        new MigrationRunner(_db).Run();
        _license = new License(_lisans);
        _repo = new PendingPaymentRecordRepository(_db);
        _envelope = TestSalesEnvelope.For(_db, _license);
        var api = new LicenseApiClient(
            new HttpClient(new FakeHttpMessageHandler(RespondAsync)) { BaseAddress = new Uri("https://test.local") },
            new LicenseTokenStore());
        _sender = new PaymentRecordSender(api, _repo, _envelope, _license, new Clock(), _log);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    private IReadOnlyList<Call> Calls(string kind)
    {
        lock (_calls) return _calls.Where(c => c.Kind == kind).ToList();
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage req)
    {
        Interlocked.Increment(ref _httpCount);
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/v1/me/licenses")
            return FakeHttpMessageHandler.Json(200,
                $$"""[{"licenseKey":"{{_lisans}}","skuCode":"standart","expiresAt":"2027-01-01T00:00:00+00:00","revokedAt":null,"id":"{{_licenseId}}"}]""");

        JsonElement body;
        using (var doc = JsonDocument.Parse(await req.Content!.ReadAsStringAsync())) body = doc.RootElement.Clone();
        var finish = Regex.Match(path, $@"^/api/v1/licenses/{_licenseId}/payment-requests/([0-9a-f-]{{36}})/finish$");
        if (finish.Success && req.Method == HttpMethod.Post)
        {
            var recordId = Guid.Parse(finish.Groups[1].Value);
            lock (_calls) _calls.Add(new Call("finish", recordId, body));
            if (_finish?.Invoke(recordId) is { } scripted) return await scripted;
            return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
            {
                recordId,
                state = body.GetProperty("outcome").GetString(),
                finishedAt = DateTimeOffset.UtcNow,
            }, Web));
        }

        if (path == $"/api/v1/licenses/{_licenseId}/payment-requests/backfill" && req.Method == HttpMethod.Post)
        {
            lock (_calls) _calls.Add(new Call("backfill", null, body));
            if (_backfill?.Invoke() is { } whole) return whole;
            var results = body.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("recordId").GetGuid())
                .Select(id => new { recordId = id, status = _itemStatus.GetValueOrDefault(id, "applied") });
            return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new { records = results }, Web));
        }

        throw new InvalidOperationException($"beklenmeyen istek: {req.Method} {path}");
    }

    private static Task<HttpResponseMessage> Respond(HttpResponseMessage response) => Task.FromResult(response);

    private static Task<HttpResponseMessage> Offline() =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("fake offline"));

    private PendingPaymentFinish QueueFinish(string outcome = PaymentRequestStates.Delivered, decimal net = 550m,
        decimal? applied = 50m, bool fullyFromBalance = false)
    {
        var finish = new PendingPaymentFinish(Guid.NewGuid(), outcome, net, applied, fullyFromBalance);
        _sender.EnqueueFinish(finish).Should().BeTrue();
        return finish;
    }

    private PaymentRequestBackfillItem QueueRecord(string state = PaymentRequestStates.Delivered)
    {
        var owner = Guid.NewGuid().ToString("N");
        var item = new PaymentRequestBackfillItem(Guid.NewGuid(), owner, [owner, Guid.NewGuid().ToString("N")],
            "cumulative", [Guid.NewGuid(), Guid.NewGuid()], state, 120m, null, false,
            DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L));
        _sender.EnqueueRecord(item).Should().BeTrue();
        return item;
    }

    private PendingPaymentRecordRow? Row(Guid recordId) => _repo.Get(PendingPaymentRecordCodec.Key(recordId));

    // ── finish ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finish_basarisi_satiri_siler_govde_sonuc_tutarlar_ve_v2_zarfi()
    {
        var finish = QueueFinish(PaymentRequestStates.Unconfirmed, net: 550m, applied: 50m, fullyFromBalance: false);

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 1, Kept: 0, Dropped: 0, Remaining: 0));
        _repo.Count().Should().Be(0);
        var call = Calls("finish").Should().ContainSingle().Subject;
        call.RecordId.Should().Be(finish.RecordId);
        call.Body.GetProperty("outcome").GetString().Should().Be("unconfirmed");
        call.Body.GetProperty("netAmount").GetDecimal().Should().Be(550m);
        call.Body.GetProperty("appliedAmount").GetDecimal().Should().Be(50m);
        call.Body.GetProperty("fullyFromBalance").GetBoolean().Should().BeFalse();
        call.Body.GetProperty("replicaId").GetGuid().Should().Be(ReplicaId, "zarf gönderim anından (§8.7)");
        call.Body.GetProperty("protocol").GetInt32().Should().Be(2);
        call.Body.GetProperty("seenMode").GetInt32().Should().Be(0, "mod Off: kuyruk iki kipte gider (§4.7)");
    }

    [Fact]
    public async Task Finish_cevrimdisi_kalir_deneme_sayilir_son_hata_yazilir_baglaninca_ayni_icerik_gider()
    {
        var finish = QueueFinish();
        _finish = _ => Offline();

        var offline = await _sender.FlushAsync(CancellationToken.None);

        offline.Should().Be(new PaymentRecordFlush(Sent: 0, Kept: 1, Dropped: 0, Remaining: 1));
        var row = Row(finish.RecordId)!;
        row.Attempts.Should().Be(1);
        row.LastError.Should().Be("Offline");

        _finish = null;
        var online = await _sender.FlushAsync(CancellationToken.None);

        online.Should().Be(new PaymentRecordFlush(Sent: 1, Kept: 0, Dropped: 0, Remaining: 0));
        var calls = Calls("finish");
        calls.Should().HaveCount(2).And.OnlyContain(c => c.RecordId == finish.RecordId);
        calls[1].Body.GetRawText().Should().Be(calls[0].Body.GetRawText(), "yineleme aynı içerik (sunucu Id ve içerikle tanır)");
    }

    [Fact]
    public async Task Sunucu_hatasi_ve_uc_yok_satiri_tutar_gonderimi_durdurur()
    {
        var first = QueueFinish();
        var second = QueueFinish();
        _finish = _ => Respond(FakeHttpMessageHandler.Problem(503, "fake-fault"));

        var failed = await _sender.FlushAsync(CancellationToken.None);

        failed.Should().Be(new PaymentRecordFlush(Sent: 0, Kept: 1, Dropped: 0, Remaining: 2));
        Row(first.RecordId)!.LastError.Should().Be("Failed HTTP 503");
        Row(second.RecordId)!.Attempts.Should().Be(0, "sunucu hatasında gönderim durur, sonraki kayıt denenmez");

        _finish = _ => Respond(FakeHttpMessageHandler.Empty(404));
        var missing = await _sender.FlushAsync(CancellationToken.None);

        missing.Kept.Should().Be(1);
        Row(first.RecordId)!.Attempts.Should().Be(2);
        Row(first.RecordId)!.LastError.Should().Be("EndpointMissing HTTP 404", "uç yok (S3 öncesi imaj, §8.6)");
        Row(second.RecordId)!.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Record_not_found_ve_content_conflict_son_durum_siler_sayi_ve_Id_gunlukte()
    {
        var notFound = QueueFinish();
        var conflict = QueueFinish();
        var record = QueueRecord();
        _itemStatus[record.RecordId] = PaymentRequestBackfillResult.StatusContentConflict;
        _finish = id => id == notFound.RecordId
            ? Respond(FakeHttpMessageHandler.Problem(404, PaymentRequestProblemCodes.RecordNotFound))
            : Respond(FakeHttpMessageHandler.Problem(409, PaymentRequestProblemCodes.ContentConflict));

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 0, Kept: 0, Dropped: 3, Remaining: 0));
        _repo.Count().Should().Be(0, "son durum: yeniden gönderilmez (§4.7)");
        var warning = _log.Warnings.Should().ContainSingle().Subject.Message;
        warning.Should().Contain("3 kayıt").And.Contain(PendingPaymentRecordCodec.Key(notFound.RecordId))
            .And.Contain(PendingPaymentRecordCodec.Key(conflict.RecordId))
            .And.Contain(PendingPaymentRecordCodec.Key(record.RecordId));

        (await _sender.FlushAsync(CancellationToken.None)).Should().Be(PaymentRecordFlush.Empty);
        Calls("finish").Should().HaveCount(2);
    }

    [Fact]
    public async Task Gecersiz_istek_400_satiri_silmez_deneme_sayilir_kayit_basina_bir_uyari_gonderim_surer()
    {
        var invalid = QueueFinish();
        var ok = QueueFinish();
        _finish = id => id == invalid.RecordId
            ? Respond(FakeHttpMessageHandler.Problem(400, PaymentRequestProblemCodes.InvalidAmount))
            : null;

        var flush = await _sender.FlushAsync(CancellationToken.None);
        await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 1, Kept: 1, Dropped: 0, Remaining: 1));
        Row(ok.RecordId).Should().BeNull("istemci hatası yalnız kendi kaydını tutar");
        var row = Row(invalid.RecordId)!;
        row.Attempts.Should().Be(2);
        row.LastError.Should().Be(PaymentRequestProblemCodes.InvalidAmount);
        _log.Warnings.Should().ContainSingle(w => w.Message.Contains(PaymentRequestProblemCodes.InvalidAmount),
            "60 sn'lik tur her seferinde uyarmaz");
    }

    // ── kirasız kayıtlar ──────────────────────────────────────────────────

    [Fact]
    public async Task Kirasiz_kayitlar_200luk_partilerle_gider_450_kayit_3_parti()
    {
        var items = Enumerable.Range(0, 450).Select(_ => QueueRecord()).ToList();

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 450, Kept: 0, Dropped: 0, Remaining: 0));
        var batches = Calls("backfill");
        batches.Select(b => b.Body.GetProperty("records").GetArrayLength()).Should().Equal(200, 200, 50);
        batches.SelectMany(b => b.Body.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("recordId").GetGuid()))
            .Should().Equal(items.Select(i => i.RecordId), "eskiden yeniye");
        batches.Should().OnlyContain(b => b.Body.GetProperty("replicaId").GetGuid() == ReplicaId
                                          && b.Body.GetProperty("protocol").GetInt32() == 2
                                          && b.Body.GetProperty("seenMode").GetInt32() == 0);
        var first = batches[0].Body.GetProperty("records")[0];
        first.GetProperty("ownerId").GetString().Should().Be(items[0].OwnerId);
        first.GetProperty("scopeKey").GetString().Should().Be("cumulative");
        first.GetProperty("state").GetString().Should().Be("delivered");
        first.GetProperty("orderIds").GetArrayLength().Should().Be(2);
        first.GetProperty("memberIds").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Unchanged_ve_applied_siler_taninmayan_oge_sonucu_kalir()
    {
        var unchanged = QueueRecord();
        var applied = QueueRecord(PaymentRequestStates.Unconfirmed);
        var unknown = QueueRecord();
        _itemStatus[unchanged.RecordId] = PaymentRequestBackfillResult.StatusUnchanged;
        _itemStatus[unknown.RecordId] = "future-status";

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 2, Kept: 1, Dropped: 0, Remaining: 1));
        Row(unchanged.RecordId).Should().BeNull("aynı içerik zaten kayıtlı (§4.7)");
        Row(applied.RecordId).Should().BeNull();
        Row(unknown.RecordId)!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Parti_cevrimdisi_ya_da_sunucu_hatasinda_butun_partinin_denemesi_sayilir_satirlar_kalir()
    {
        var items = Enumerable.Range(0, 3).Select(_ => QueueRecord()).ToList();
        _backfill = () => throw new HttpRequestException("fake offline");

        var offline = await _sender.FlushAsync(CancellationToken.None);

        offline.Should().Be(new PaymentRecordFlush(Sent: 0, Kept: 3, Dropped: 0, Remaining: 3));
        items.Select(i => Row(i.RecordId)!).Should().OnlyContain(r => r.Attempts == 1 && r.LastError == "Offline");

        _backfill = () => FakeHttpMessageHandler.Problem(500, "fake-fault");
        (await _sender.FlushAsync(CancellationToken.None)).Kept.Should().Be(3);
        items.Select(i => Row(i.RecordId)!).Should().OnlyContain(r => r.Attempts == 2 && r.LastError == "Failed HTTP 500");

        _backfill = null;
        (await _sender.FlushAsync(CancellationToken.None)).Sent.Should().Be(3);
        _repo.Count().Should().Be(0);
    }

    [Fact]
    public async Task Karisik_kuyruk_sirayla_finish_tek_tek_ardisik_kayitlar_tek_partide()
    {
        var r1 = QueueRecord();
        var r2 = QueueRecord();
        var f1 = QueueFinish();
        var r3 = QueueRecord();

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Sent.Should().Be(4);
        Calls("finish").Should().ContainSingle().Which.RecordId.Should().Be(f1.RecordId);
        Calls("backfill").Select(b => b.Body.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("recordId").GetGuid()).ToList())
            .Should().SatisfyRespectively(
                first => first.Should().Equal(r1.RecordId, r2.RecordId),
                second => second.Should().Equal(r3.RecordId));
    }

    [Fact]
    public async Task Gonderim_istek_butcesiyle_sinirli_kalan_sonraki_gonderime()
    {
        var finishes = Enumerable.Range(0, 12).Select(_ => QueueFinish()).ToList();

        var first = await _sender.FlushAsync(CancellationToken.None);

        first.Sent.Should().Be(PaymentRecordSender.MaxRequestsPerFlush - 1, "lisans çözümü de bütçeden düşer");
        first.Remaining.Should().Be(12 - (PaymentRecordSender.MaxRequestsPerFlush - 1));
        _httpCount.Should().Be(PaymentRecordSender.MaxRequestsPerFlush);

        var second = await _sender.FlushAsync(CancellationToken.None);

        second.Should().Be(new PaymentRecordFlush(Sent: 12 - (PaymentRecordSender.MaxRequestsPerFlush - 1), Kept: 0,
            Dropped: 0, Remaining: 0));
        Calls("finish").Select(c => c.RecordId).Should().Equal(finishes.Select(f => (Guid?)f.RecordId), "eskiden yeniye");
    }

    [Fact]
    public async Task Cozulemeyen_yuk_istek_gitmeden_duser()
    {
        var broken = Guid.NewGuid();
        _repo.Enqueue(PendingPaymentRecordCodec.Key(broken), PendingPaymentRecordKinds.Finish, "{bozuk", 1).Should().BeTrue();
        var noMembers = Guid.NewGuid();
        _repo.Enqueue(PendingPaymentRecordCodec.Key(noMembers), PendingPaymentRecordKinds.Record,
            JsonSerializer.Serialize(new { recordId = noMembers, ownerId = "x", memberIds = Array.Empty<string>(),
                scopeKey = "cumulative", orderIds = Array.Empty<Guid>(), state = "delivered", netAmount = 1m,
                fullyFromBalance = false, at = DateTimeOffset.UtcNow }, Web), 2).Should().BeTrue();
        var good = QueueRecord();

        var flush = await _sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(new PaymentRecordFlush(Sent: 1, Kept: 0, Dropped: 2, Remaining: 0));
        Calls("finish").Should().BeEmpty();
        Calls("backfill").Should().ContainSingle().Which.Body.GetProperty("records").EnumerateArray()
            .Select(r => r.GetProperty("recordId").GetGuid()).Should().Equal(good.RecordId);
    }

    // ── zarf ve kapı ──────────────────────────────────────────────────────

    [Fact]
    public async Task Bos_kuyrukta_istek_yok()
    {
        (await _sender.FlushAsync(CancellationToken.None)).Should().Be(PaymentRecordFlush.Empty);

        _httpCount.Should().Be(0);
    }

    [Fact]
    public async Task Zarf_hazir_degilse_hic_istek_gitmez_deneme_sayilmaz()
    {
        var finish = QueueFinish();
        var record = QueueRecord();
        _envelope.NoteRestored();                       // geri yüklenmiş süreç: hello, state ve kip adımı yok (§8.7)

        var restored = await _sender.FlushAsync(CancellationToken.None);

        restored.Should().Be(new PaymentRecordFlush(Sent: 0, Kept: 0, Dropped: 0, Remaining: 2));
        _httpCount.Should().Be(0);
        Row(finish.RecordId)!.Attempts.Should().Be(0);
        Row(record.RecordId)!.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Surec_kapaliysa_ya_da_kopya_kimligi_yoksa_hic_istek_gitmez()
    {
        QueueFinish();
        QueueRecord();
        string replica;
        using (var c = _db.Open())
        {
            replica = c.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1")!;
            c.Execute("UPDATE _meta SET ReplicaId = NULL WHERE Id = 1");
        }

        (await _sender.FlushAsync(CancellationToken.None)).Remaining.Should().Be(2);
        _httpCount.Should().Be(0, "uçlar yalnız v2 konuşur: zarfsız (v1) gönderim yok");

        using (var c = _db.Open()) c.Execute("UPDATE _meta SET ReplicaId = @replica WHERE Id = 1", new { replica });
        _envelope.HoldUntilRestart();                   // geri yükleme kimlik işareti uygulanamadı (§8.7)
        (await _sender.FlushAsync(CancellationToken.None)).Remaining.Should().Be(2);
        _httpCount.Should().Be(0);
        _repo.List().Should().OnlyContain(r => r.Attempts == 0);
    }

    [Fact]
    public async Task Lisans_yoksa_ya_da_cozulemezse_kayit_istegi_gitmez_deneme_sayilmaz()
    {
        var finish = QueueFinish();
        _license.CurrentLicenseKey = null;

        (await _sender.FlushAsync(CancellationToken.None)).Remaining.Should().Be(1);
        _httpCount.Should().Be(0);

        _license.CurrentLicenseKey = $"lisans-{Guid.NewGuid():N}";   // hesabın lisansları arasında yok
        (await _sender.FlushAsync(CancellationToken.None)).Remaining.Should().Be(1);

        _httpCount.Should().Be(1, "yalnız lisans çözümü");
        Calls("finish").Should().BeEmpty();
        Row(finish.RecordId)!.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Eszamanli_iki_gonderim_her_kaydi_bir_kez_yollar()
    {
        var finishes = Enumerable.Range(0, 3).Select(_ => QueueFinish()).ToList();
        var records = Enumerable.Range(0, 5).Select(_ => QueueRecord()).ToList();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // İlk gönderim ilk finish'te bekletilir: ikinci gönderim o sırada başlar.
        _finish = id => id == finishes[0].RecordId && entered.TrySetResult() ? HoldThenOk(id) : null;

        async Task<HttpResponseMessage> HoldThenOk(Guid id)
        {
            await release.Task;
            return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
            {
                recordId = id, state = "delivered", finishedAt = DateTimeOffset.UtcNow,
            }, Web));
        }

        // Bekleme sınırları tam takım yükünde de yetsin (iş parçacığı havuzu doyabilir); doğrulama gevşemez: ikinci
        // gönderimin kapıda beklediği zamanlamayla değil kapının sayacıyla görülür.
        var a = _sender.FlushAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var requestsWhileHeld = Volatile.Read(ref _httpCount);
        var b = _sender.FlushAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (_sender.GateWaiters != 1)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "ikinci gönderim kapıya varmalı");
            await Task.Delay(10);
        }
        b.IsCompleted.Should().BeFalse("ikinci gönderim kapıda bekler");
        Volatile.Read(ref _httpCount).Should().Be(requestsWhileHeld, "kapıdaki gönderim istek atmadı");
        release.SetResult();
        var results = await Task.WhenAll(a, b);

        results.Sum(r => r.Sent).Should().Be(8);
        Calls("finish").Select(c => c.RecordId).Should().OnlyHaveUniqueItems().And.HaveCount(3);
        Calls("backfill").SelectMany(c => c.Body.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("recordId").GetGuid()))
            .Should().OnlyHaveUniqueItems().And.BeEquivalentTo(records.Select(r => r.RecordId));
        _repo.Count().Should().Be(0);
    }

    // ── kodlayıcı ─────────────────────────────────────────────────────────

    [Fact]
    public void Kodlayici_finish_ve_kaydi_gidip_donusturur_anahtar_uyusmazligini_reddeder()
    {
        var finish = new PendingPaymentFinish(Guid.NewGuid(), PaymentRequestStates.Abandoned, 0m, null, false);
        var row = new PendingPaymentRecordRow(PendingPaymentRecordCodec.Key(finish.RecordId),
            PendingPaymentRecordKinds.Finish, PendingPaymentRecordCodec.EncodeFinish(finish), 1, 0, null);
        PendingPaymentRecordCodec.DecodeFinish(row).Should().Be(finish);
        row.PayloadJson.Should().NotContain("replicaId", "zarf gönderim anından");

        var other = row with { RecordId = Guid.NewGuid().ToString("N") };
        var mismatch = () => PendingPaymentRecordCodec.DecodeFinish(other);
        mismatch.Should().Throw<JsonException>();

        var badOutcome = row with { PayloadJson = row.PayloadJson.Replace("abandoned", "sending", StringComparison.Ordinal) };
        var invalid = () => PendingPaymentRecordCodec.DecodeFinish(badOutcome);
        invalid.Should().Throw<JsonException>();

        var owner = Guid.NewGuid().ToString("N");
        var item = new PaymentRequestBackfillItem(Guid.NewGuid(), owner, [owner], "session:" + Guid.NewGuid().ToString("N"),
            [Guid.NewGuid()], PaymentRequestStates.Unconfirmed, 99.5m, 10m, true, DateTimeOffset.FromUnixTimeSeconds(5));
        var recordRow = new PendingPaymentRecordRow(PendingPaymentRecordCodec.Key(item.RecordId),
            PendingPaymentRecordKinds.Record, PendingPaymentRecordCodec.EncodeRecord(item), 1, 0, null);
        PendingPaymentRecordCodec.DecodeRecord(recordRow).Should().BeEquivalentTo(item);
    }
}
