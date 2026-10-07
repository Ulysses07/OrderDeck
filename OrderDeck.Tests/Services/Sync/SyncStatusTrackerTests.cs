using System;
using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Faz 0 (D2): izleyicinin durum satırına verdiği bilgiler — yetişme ilerlemesi
/// (<see cref="CustomerPullOutcome.MorePending"/> çevrimdışı görünmesin), izleme başlangıcı (hiç
/// yetişemeyen süreç de çevrimdışı görünsün) ve tek kilit altında anlık görüntü.</summary>
public sealed class SyncStatusTrackerTests
{
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";

    [Fact]
    public void Tam_yetisme_yetisme_ilerlemesini_kapatir()
    {
        var tracker = new SyncStatusTracker();
        var at = DateTimeOffset.UtcNow;

        tracker.MarkCatchUpProgress(at);
        tracker.LastCatchUpProgressAt.Should().Be(at);

        tracker.MarkPullSucceeded(at.AddSeconds(30), Lisans);
        tracker.LastCatchUpProgressAt.Should().BeNull("akış boş sayfaya ulaştı — artık geride değil");
    }

    [Fact]
    public void Izleme_kurulusta_baslar_lisans_degisince_yeniden_baslar()
    {
        var before = DateTimeOffset.UtcNow;
        var tracker = new SyncStatusTracker();
        var started = tracker.TrackingSince;
        started.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);

        tracker.MarkCatchUpProgress(DateTimeOffset.UtcNow);
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, Lisans);
        tracker.SetBlockedOn(new SyncBlock(Guid.NewGuid().ToString("N"), SyncBlockReason.Busy, before, before));
        var beforeReset = DateTimeOffset.UtcNow;
        tracker.ResetForLicenseChange();

        tracker.TrackingSince.Should().BeOnOrAfter(beforeReset, "yeni lisansın 'hiç yetişemedi' süresi şimdiden sayılır");
        tracker.LastCatchUpProgressAt.Should().BeNull("önceki lisansın akışı yeni lisansı anlatmaz");
        tracker.LastPullOkAt.Should().BeNull();
        tracker.BlockedOn.Should().BeNull();
    }

    [Fact]
    public void Anlik_goruntu_butun_alanlari_tasir()
    {
        var tracker = new SyncStatusTracker();
        var at = DateTimeOffset.UtcNow;
        var block = new SyncBlock(Guid.NewGuid().ToString("N"), SyncBlockReason.Stalled, at.AddMinutes(-3), at);
        tracker.MarkPullSucceeded(at.AddMinutes(-5), Lisans);
        tracker.MarkCatchUpProgress(at.AddMinutes(-1));
        tracker.SetBlockedOn(block);

        var s = tracker.Snapshot();

        s.LastPullOkAt.Should().Be(at.AddMinutes(-5));
        s.LastCatchUpProgressAt.Should().Be(at.AddMinutes(-1));
        s.BlockedOn.Should().Be(block);
        s.TrackingSince.Should().Be(tracker.TrackingSince);
    }

    [Fact]
    public void Gonderim_ilerlemesi_kayitla_baslar_basariyla_yazilir_lisans_degisince_sifirlanir()
    {
        var tracker = new SyncStatusTracker();
        tracker.RegisterPush("musteri");
        tracker.RegisterPush("odeme");
        var at = DateTimeOffset.UtcNow;

        tracker.MarkPushOk("musteri", at);
        tracker.RegisterPush("musteri");                      // ikinci kayıt ilerlemeyi silmez
        var s = tracker.Snapshot();

        s.PushOkAt.Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset?> { ["musteri"] = at, ["odeme"] = null },
            "kayıtlı ama hiç başarmamış gönderim 'hiç' olarak görünür — izleme başından ölçülür");

        tracker.MarkPushOk("odeme", at);
        s.PushOkAt!["odeme"].Should().BeNull("anlık görüntü sonraki yazımlardan etkilenmez");

        tracker.ResetForLicenseChange();
        tracker.Snapshot().PushOkAt.Should().BeEquivalentTo(
            new Dictionary<string, DateTimeOffset?> { ["musteri"] = null, ["odeme"] = null },
            "önceki lisansın gönderimi yeni lisansı anlatmaz; kayıtlar kalır");
    }
}
