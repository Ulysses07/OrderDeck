using System;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Faz 0 (D2): izleyicinin yetişme ilerlemesi — durum satırı sayfa sınırına takılan
/// (<see cref="CustomerPullOutcome.MorePending"/>) bilgisayarı çevrimdışı göstermesin.</summary>
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
    public void Lisans_degisince_yetisme_ilerlemesi_silinir()
    {
        var tracker = new SyncStatusTracker();
        tracker.MarkCatchUpProgress(DateTimeOffset.UtcNow);

        tracker.ResetForLicenseChange();

        tracker.LastCatchUpProgressAt.Should().BeNull("önceki lisansın akışı yeni lisansı anlatmaz");
    }
}
