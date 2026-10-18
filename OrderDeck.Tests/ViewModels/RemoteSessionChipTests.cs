using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.Sync;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Uzak yayın çipleri, "Yayını bitir…" akışı ve kendi yayınının bandı (W2-09; spec 2026-10-13 §5.7, §4.6
/// <c>force-end</c>, §5.3, Y7, Y8, M18) — sabit saatle, UTC'de. Eşik sunucunun
/// <c>sales/state.forceEndSilenceMinutes</c>'ı: 7 ve 20 ile koşar, 15 sabit değil. Komutlar gerçek
/// <see cref="SalesCommandClient"/> ve <see cref="FakeSalesServer"/> üzerinden.
/// </summary>
public sealed class RemoteSessionChipTests : IDisposable
{
    /// <summary>Sabit "şimdi": 2026-10-13 21:31 UTC.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 21, 31, 0, TimeSpan.Zero);

    private sealed class Clock : IClock
    {
        public DateTimeOffset At { get; set; } = Now;
        public long UnixNow() => At.ToUnixTimeSeconds();
    }

    private readonly SalesEditTestBed _bed = new();
    private readonly Clock _clock = new();
    private readonly RemoteLiveSessionRepository _mirror;
    private int _threshold = 15;
    private bool _stateKnown = true;
    private int _tours;

    public RemoteSessionChipTests()
    {
        _mirror = new RemoteLiveSessionRepository(_bed.Db);
        _bed.ModeOn();
    }

    public void Dispose() => _bed.Dispose();

    private RemoteSessionsViewModel Vm(Func<CancellationToken, Task>? tour = null, IClock? clock = null) => new(
        _mirror, new SalesSyncStateRepository(_bed.Db), _bed.Sessions, _bed.Commands, _bed.Connectivity,
        () => _stateKnown
            ? new SalesStateSnapshot(_bed.Server.Lisans, 1, null, null, [], _threshold, [], DateTimeOffset.UtcNow)
            : null,
        () => _bed.Server.Lisans,
        tour ?? (_ =>
        {
            Interlocked.Increment(ref _tours);
            return Task.CompletedTask;
        }),
        clock ?? _clock, zone: TimeZoneInfo.Utc);

    private static string N(Guid id) => id.ToString("N");

    /// <summary>Aynaya bir uzak yayın (sahibin son sinyali "şimdi"den <paramref name="silentMinutes"/> önce).</summary>
    private static RemoteLiveSessionRow Row(Guid id, string state, double silentMinutes, string? machine = "PC-A",
        int? unsent = null) =>
        new(N(id), "Akşam yayını", Now.AddHours(-2).ToUnixTimeSeconds(), state == "live" ? null : Now.AddMinutes(-60).ToUnixTimeSeconds(),
            machine, Now.AddMinutes(-silentMinutes).ToUnixTimeSeconds(), state, unsent);

    [Fact]
    public void Canli_ve_devrediliyor_cip_metinleri()
    {
        var live = Guid.NewGuid();
        var handing = Guid.NewGuid();
        var unnamed = Guid.NewGuid();
        _mirror.Mirror([Row(live, "live", 17), Row(handing, "handing-over", 17, unsent: 3), Row(unnamed, "live", 17, machine: null)]);
        var vm = Vm();

        vm.Refresh();

        vm.HasChips.Should().BeTrue();
        vm.Chips.Single(c => c.SessionId == N(live)).Text.Should().Be("PC-A'da canlı · son sinyal 21:14");
        vm.Chips.Single(c => c.SessionId == N(handing)).Text.Should()
            .Be("PC-A'da yayın bitti, 3 etiket gönderilmeyi bekliyor · son sinyal 21:14");
        vm.Chips.Single(c => c.SessionId == N(unnamed)).Text.Should().Be("Başka bir bilgisayarda canlı · son sinyal 21:14");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(20)]
    public void Eylem_yalniz_canli_ve_sahibi_sunucu_esigi_kadar_sessiz_yayinda(int threshold)
    {
        _threshold = threshold;
        var justBelow = Guid.NewGuid();
        var atThreshold = Guid.NewGuid();
        var fifteen = Guid.NewGuid();
        var handing = Guid.NewGuid();
        _mirror.Mirror([
            Row(justBelow, "live", threshold - 1),
            Row(atThreshold, "live", threshold),
            Row(fifteen, "live", 15),
            Row(handing, "handing-over", threshold + 600, unsent: 2),
        ]);
        var vm = Vm();

        vm.Refresh();

        vm.Chips.Single(c => c.SessionId == N(justBelow)).CanForceEnd.Should().BeFalse();
        vm.Chips.Single(c => c.SessionId == N(atThreshold)).CanForceEnd.Should().BeTrue();
        vm.Chips.Single(c => c.SessionId == N(fifteen)).CanForceEnd.Should().Be(15 >= threshold,
            "eşik sunucunun forceEndSilenceMinutes'ı; 15 sabit yazılmaz");
        vm.Chips.Single(c => c.SessionId == N(handing)).CanForceEnd.Should().BeFalse("Y8: devrediliyor yayında eylem yok");
    }

    [Fact]
    public void Devrediliyor_cip_saatlerdir_sessiz_sahipte_eylem_sunmaz_ek_satir_gosterir()
    {
        _threshold = 7;
        var silent = Guid.NewGuid();
        var recent = Guid.NewGuid();
        _mirror.Mirror([Row(silent, "handing-over", 5 * 60, unsent: 4), Row(recent, "handing-over", 2, unsent: 4)]);
        var vm = Vm();

        vm.Refresh();

        var chip = vm.Chips.Single(c => c.SessionId == N(silent));
        chip.CanForceEnd.Should().BeFalse();
        chip.SilentNote.Should().Be("PC-A açılıp internete bağlanınca yayın buradan düzenlenebilir olur.");
        vm.Chips.Single(c => c.SessionId == N(recent)).SilentNote.Should().BeNull("sahip eşikten yeni sinyal verdi");

        vm.BeginForceEndCommand.Execute(chip);
        vm.ForceEndPrompt.Should().BeNull("devrediliyor çipinden onay şeridi açılmaz");
    }

    [Fact]
    public void Esik_bilinmiyorsa_ya_da_cevrimdisiysa_eylem_sunulmaz()
    {
        var id = Guid.NewGuid();
        _mirror.Mirror([Row(id, "live", 120)]);
        var vm = Vm();

        _stateKnown = false;
        vm.Refresh();
        vm.Chips.Single().CanForceEnd.Should().BeFalse("eşik bilinmiyor: 15 varsayılmaz");

        _stateKnown = true;
        _bed.Online = false;
        vm.Refresh();
        vm.Chips.Single().CanForceEnd.Should().BeFalse("bayat aynada 'sessiz' görünen, bu bilgisayarın bağlantısızlığı");

        _bed.Online = true;
        vm.Refresh();
        vm.Chips.Single().CanForceEnd.Should().BeTrue();
    }

    [Fact]
    public void Dakikalik_tazeleme_sessizligi_ve_uygunlugu_ilerletir()
    {
        _threshold = 20;
        var id = Guid.NewGuid();
        _mirror.Mirror([Row(id, "live", 19)]);
        var vm = Vm();
        vm.Refresh();
        vm.Chips.Single().CanForceEnd.Should().BeFalse();

        _clock.At = Now.AddMinutes(1);
        vm.Refresh();

        vm.Chips.Single().CanForceEnd.Should().BeTrue();
        vm.Chips.Single().SilentMinutes.Should().Be(20);
    }

    [Fact]
    public void Onay_seridi_cipin_dakikasi_ve_makine_adiyla_spec_metni()
    {
        _threshold = 7;
        var id = Guid.NewGuid();
        _mirror.Mirror([Row(id, "live", 17)]);
        var vm = Vm();
        vm.Refresh();

        vm.BeginForceEndCommand.Execute(vm.Chips.Single());

        vm.ForceEndPrompt.Should().Be(
            "PC-A 17 dakikadır sessiz. Yayın gerçekten bitti mi? Bitirirsen yayın buradan düzenlenebilir olur. "
          + "PC-A aslında çalışıyorsa, bağlantısı gelince gönderdiği yeni etiketler ve değişiklikler de işlenir; "
          + "aynı etikette burada yaptığın değişiklik önceliklidir.");
        _bed.Server.CommandCalls.Should().BeEmpty("şerit açılınca istek gitmez");

        vm.CancelForceEndCommand.Execute(null);
        vm.ForceEndPrompt.Should().BeNull();
        _bed.Server.CommandCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Basari_cipi_ve_aynadaki_satiri_kaldirir_ve_akis_turu_baslatir()
    {
        _threshold = 7;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        _bed.Server.SeedSession(id, Now.AddHours(-2), null, owner: owner, title: "Akşam yayını", ownerMachineName: "PC-A");
        _bed.Server.SetReplicaLastSeen(owner, DateTimeOffset.UtcNow.AddMinutes(-30));
        _mirror.Mirror([Row(id, "live", 17)]);
        var vm = Vm();
        vm.Refresh();
        vm.BeginForceEndCommand.Execute(vm.Chips.Single());

        await vm.ConfirmForceEndCommand.ExecuteAsync(null);
        await vm.LastFeedTour!;

        vm.Chips.Should().BeEmpty();
        vm.HasChips.Should().BeFalse();
        _mirror.GetAll().Should().BeEmpty("aynadaki satır da kalkar");
        vm.ForceEndPrompt.Should().BeNull();
        vm.ForceEndProblem.Should().BeNull();
        _tours.Should().Be(1, "başarı bir satış akışı turu başlatır");
        var call = _bed.Server.CommandCalls.Single().Command;
        call.Kind.Should().Be("force-end");
        call.Payload!.Value.GetProperty("sessionId").GetGuid().Should().Be(id);
        call.Payload.Value.GetProperty("confirm").GetBoolean().Should().BeTrue();
        _bed.Server.Session(id)!.ForceEndedBy.Should().Be(_bed.Envelope.ReplicaId!.Value.ToString());
        _bed.Sessions.GetById(N(id))!.Origin.Should().Be(StreamSessionOrigin.Remote,
            "yanıtın oturumu akışla aynı fonksiyonla yazıldı (devredilmiş uzak yayın)");
        _bed.Server.Faults.Should().BeEmpty();

        vm.Refresh();
        vm.Chips.Should().BeEmpty();
    }

    [Fact]
    public async Task Owner_active_spec_metniyle_seritte_kalir_cip_durur_tur_yok()
    {
        _threshold = 7;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        _bed.Server.SeedSession(id, Now.AddHours(-2), null, owner: owner, ownerMachineName: "PC-A");
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-2);
        _bed.Server.SetReplicaLastSeen(owner, lastSeen);
        _mirror.Mirror([Row(id, "live", 17)]);                       // ayna bayat: sahip o arada döndü
        var vm = Vm();
        vm.Refresh();
        vm.BeginForceEndCommand.Execute(vm.Chips.Single());

        await vm.ConfirmForceEndCommand.ExecuteAsync(null);

        var local = TimeZoneInfo.ConvertTime(lastSeen, TimeZoneInfo.Local).ToString("HH:mm");
        vm.ForceEndProblem.Should().Be($"PC-A hâlâ sinyal veriyor (son {local}); yayın buradan bitirilemez.");
        vm.CanRetryForceEnd.Should().BeFalse("kesin ret: aynı komut yeniden denenmez");
        vm.ForceEndPrompt.Should().BeNull();
        vm.Chips.Should().ContainSingle(c => c.SessionId == N(id));
        _mirror.GetAll().Should().ContainSingle();
        vm.LastFeedTour.Should().BeNull();
        _tours.Should().Be(0);
        _bed.Server.Session(id)!.EndedAt.Should().BeNull();

        vm.DismissForceEndProblemCommand.Execute(null);
        vm.ForceEndProblem.Should().BeNull();
    }

    [Fact]
    public async Task Handing_over_spec_metniyle_seritte_kalir()
    {
        _threshold = 7;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        _bed.Server.SeedSession(id, Now.AddHours(-2), Now.AddHours(-1), owner: owner, ownerMachineName: "PC-A");
        _bed.Server.SetUnsentLabelCount(id, 4);
        _bed.Server.SetReplicaLastSeen(owner, DateTimeOffset.UtcNow.AddHours(-3));
        _mirror.Mirror([Row(id, "live", 17)]);                       // ayna bayat: yayın o arada bitti
        var vm = Vm();
        vm.Refresh();
        vm.BeginForceEndCommand.Execute(vm.Chips.Single());

        await vm.ConfirmForceEndCommand.ExecuteAsync(null);

        vm.ForceEndProblem.Should().Be(
            "PC-A'da yayın bitti ama 4 etiket henüz gönderilmedi. PC-A açılıp internete bağlanınca yayın buradan "
          + "düzenlenebilir olur.");
        vm.CanRetryForceEnd.Should().BeFalse();
        vm.Chips.Should().ContainSingle();
        _tours.Should().Be(0);
        _bed.Server.Session(id)!.ReleasedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sonucu_bilinmeyen_gonderim_ayni_komutla_yeniden_denenir()
    {
        _threshold = 7;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        _bed.Server.SeedSession(id, Now.AddHours(-2), null, owner: owner, ownerMachineName: "PC-A");
        _mirror.Mirror([Row(id, "live", 17)]);
        var vm = Vm();
        vm.Refresh();
        vm.BeginForceEndCommand.Execute(vm.Chips.Single());
        _bed.Server.LoseNextResponse(FakeSalesServer.Commands);

        await vm.ConfirmForceEndCommand.ExecuteAsync(null);

        vm.ForceEndProblem.Should().Be(SalesCommandMessages.Offline);
        vm.CanRetryForceEnd.Should().BeTrue();
        vm.Chips.Should().ContainSingle("sonuç bilinmiyor: çip kalır");

        await vm.RetryForceEndCommand.ExecuteAsync(null);
        await vm.LastFeedTour!;

        var calls = _bed.Server.CommandCalls;
        calls.Should().HaveCount(2);
        calls[1].Command.CommandId.Should().Be(calls[0].Command.CommandId!.Value, "yeniden deneme aynı komut");
        _bed.Server.CommandLog(calls[0].Command.CommandId!.Value)!.Applications.Should().Be(1);
        vm.Chips.Should().BeEmpty();
        vm.ForceEndProblem.Should().BeNull();
        _tours.Should().Be(1);
    }

    [Fact]
    public async Task Basari_sonrasi_gercek_akis_turu_yayini_indirir_cip_geri_gelmez()
    {
        // Uçtan uca: gerçek satış akışı turu (state + ayna + akış), sunucunun canlı yayın listesi kendi oturumlarından.
        _bed.Server.DeriveLiveSessions = true;
        _bed.Server.ForceEndSilenceMinutes = 7;
        _threshold = 7;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        _bed.Server.SeedSession(id, DateTimeOffset.UtcNow.AddHours(-2), null, owner: owner, ownerMachineName: "PC-A");
        _bed.Server.SetReplicaLastSeen(owner, DateTimeOffset.UtcNow.AddMinutes(-30));
        var order = _bed.RemoteOrder(id, _bed.RemoteCustomer());
        var pull = _bed.CreatePull();
        (await pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.CaughtUp);
        var vm = Vm(tour: ct => pull.TourOnceAsync(ct), clock: new SystemClock());
        vm.Refresh();
        vm.Chips.Should().ContainSingle(c => c.SessionId == N(id) && c.CanForceEnd);
        _bed.LabelRepo.GetById(N(order)).Should().BeNull("canlı yayının etiketi inmez (Y1)");

        vm.BeginForceEndCommand.Execute(vm.Chips.Single());
        await vm.ConfirmForceEndCommand.ExecuteAsync(null);
        await vm.LastFeedTour!;
        vm.Refresh();

        vm.Chips.Should().BeEmpty("sunucunun listesi devredilmiş yayını artık vermez");
        _bed.LabelRepo.GetById(N(order)).Should().NotBeNull("devirden sonra yayının etiketleri akıştan iner");
        _bed.Server.Faults.Should().BeEmpty();
    }

    [Fact]
    public void Kendi_acik_yayini_uzaktan_bitirildiyse_band()
    {
        var releasedAt = new DateTimeOffset(2026, 10, 13, 21, 14, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var session = _bed.LocalSession(ended: false, releasedAt: releasedAt);
        var vm = Vm();

        vm.Refresh();

        vm.OwnSessionBand.Should().Be(
            "Bu yayın başka bir bilgisayardan bitirildi olarak işaretlendi (21:14). Yayına devam edebilirsin; "
          + "bitirdiğinde değişiklikler eşitlenir. Diğer bilgisayarın aynı etikette yaptığı değişiklik önceliklidir.");
        _bed.Sessions.GetActive()!.Id.Should().Be(session, "yayın açık kalır (M18)");

        _bed.Exec("UPDATE StreamSession SET ReleasedAt = NULL WHERE Id = @session", new { session });
        vm.Refresh();
        vm.OwnSessionBand.Should().BeNull();
    }

    [Fact]
    public void Mod_Offta_cip_band_ve_serit_yok_istek_yok()
    {
        _threshold = 7;
        _mirror.Mirror([Row(Guid.NewGuid(), "live", 120), Row(Guid.NewGuid(), "handing-over", 120, unsent: 1)]);
        _bed.LocalSession(ended: false, releasedAt: Now.ToUnixTimeSeconds());
        var vm = Vm();
        vm.Refresh();
        vm.BeginForceEndCommand.Execute(vm.Chips.First(c => c.CanForceEnd));
        vm.ForceEndPrompt.Should().NotBeNull("ön koşul: mod On'da şerit açıktı");

        _bed.ModeOff();
        vm.Refresh();

        vm.Chips.Should().BeEmpty();
        vm.HasChips.Should().BeFalse();
        vm.OwnSessionBand.Should().BeNull();
        vm.ForceEndPrompt.Should().BeNull();
        _bed.HttpCount.Should().Be(0);
    }
}
