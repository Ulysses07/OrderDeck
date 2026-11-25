using FluentAssertions;
using OrderDeck.App.Services.Chat;

namespace OrderDeck.Tests.Services.Chat;

/// <summary>
/// "Sohbeti yenile" düğmesinin evreleri (tasarım: Sohbeti yenile §5.5).
///
/// NEDEN: düğme ne kadar meşgul kalacağını ve sonunda onay işareti gösterip göstermeyeceğini
/// satırların durumundan türetir. Kural saf bir sınıfta: en az 1 sn meşgul (titremesin), en çok 15 sn
/// (bağlanamayan kaynak düğmeyi kilitlemesin), onay işareti yalnız sorun kalmadıysa (rengi soluk olan
/// sorun da sorundur). Saat parametredir: testler beklemez.
/// </summary>
public sealed class ChatRefreshTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 21, 14, 0, TimeSpan.FromHours(3));

    private readonly ChatRefreshTracker _tracker = new(
        minimum: TimeSpan.FromSeconds(1), maximum: TimeSpan.FromSeconds(15), doneFor: TimeSpan.FromSeconds(1.5));

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void Starts_idle()
    {
        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle);
    }

    [Fact]
    public void Stays_busy_for_at_least_the_minimum_even_when_nothing_is_connecting()
    {
        _tracker.Start(At(0));

        _tracker.Observe(At(0.5), anyConnecting: false, anyProblem: false);

        _tracker.Phase.Should().Be(ChatRefreshPhase.Busy, "yarım saniyede biten yenileme düğmeyi titretirdi");
    }

    [Fact]
    public void Stays_busy_while_a_row_is_still_connecting()
    {
        _tracker.Start(At(0));

        _tracker.Observe(At(5), anyConnecting: true, anyProblem: false);

        _tracker.Phase.Should().Be(ChatRefreshPhase.Busy);
    }

    [Fact]
    public void Ends_done_when_everything_settled_without_problems()
    {
        _tracker.Start(At(0));

        _tracker.Observe(At(1), anyConnecting: false, anyProblem: false);

        _tracker.Phase.Should().Be(ChatRefreshPhase.Done);
    }

    [Fact]
    public void Ends_idle_without_a_check_mark_when_a_problem_remains()
    {
        _tracker.Start(At(0));

        _tracker.Observe(At(2), anyConnecting: false, anyProblem: true);

        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle, "sorun sürerken onay işareti yalan söylerdi");
    }

    [Fact]
    public void Gives_up_at_the_maximum_without_a_check_mark()
    {
        _tracker.Start(At(0));
        _tracker.Observe(At(14.9), anyConnecting: true, anyProblem: false);
        _tracker.Phase.Should().Be(ChatRefreshPhase.Busy);

        _tracker.Observe(At(15), anyConnecting: true, anyProblem: false);

        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle, "bağlanamayan kaynak düğmeyi kilitlemez; kendi döngüsünde dener");
    }

    [Fact]
    public void Done_returns_to_idle_after_its_display_time()
    {
        _tracker.Start(At(0));
        _tracker.Observe(At(1), anyConnecting: false, anyProblem: false);

        _tracker.Observe(At(2.4), anyConnecting: false, anyProblem: false);
        _tracker.Phase.Should().Be(ChatRefreshPhase.Done);

        _tracker.Observe(At(2.5), anyConnecting: false, anyProblem: false);
        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle);
    }

    [Fact]
    public void Cancel_returns_to_idle_at_once()
    {
        _tracker.Start(At(0));

        _tracker.Cancel();

        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle);
        _tracker.Observe(At(1), anyConnecting: false, anyProblem: false);
        _tracker.Phase.Should().Be(ChatRefreshPhase.Idle, "iptal edilen yenileme sonradan onay işareti göstermez");
    }
}
