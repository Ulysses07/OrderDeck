using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Instagram döngülerinin uyandırılabilir beklemesi.
///
/// NEDEN: oturum başlangıcı ve "Sohbeti yenile", o an BEKLEYEN bütün döngüleri (üst döngü ve hesap
/// döngüleri) birden uyandırmalı. İki tuzak var: sinyal hatırlanırsa döngü ileride ilgisiz bir
/// beklemeyi boş yere keser; sinyal, döngü "bekleyeceğim" kararını verdikten ama beklemeye girmeden
/// önce gelirse kaybolur ve döngü uzun bekler. İkincisine karşı bekleyen, kararını vermeden ÖNCE
/// okuduğu sıra numarasını verir.
/// </summary>
public sealed class InstagramWakeTests
{
    /// <summary>Dolması beklenmeyen süre.</summary>
    private static readonly TimeSpan Long = TestPatience.Upper * 4;

    private readonly InstagramWake _wake = new();
    private readonly TimeProvider _time = TimeProvider.System;

    [Fact]
    public async Task Delay_returns_false_when_the_time_runs_out()
    {
        (await _wake.DelayAsync(TimeSpan.FromMilliseconds(20), _time, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Signal_wakes_every_waiting_delay_and_reports_how_many()
    {
        var first = _wake.DelayAsync(Long, _time, default);
        var second = _wake.DelayAsync(Long, _time, default);

        _wake.Signal().Should().Be(2);

        (await first.WaitAsync(TestPatience.Upper)).Should().BeTrue();
        (await second.WaitAsync(TestPatience.Upper)).Should().BeTrue();
        _wake.Signal().Should().Be(0, "uyananlar artık beklemiyor");
    }

    [Fact]
    public async Task Signal_with_nobody_waiting_is_not_remembered()
    {
        _wake.Signal();

        // Sıra numarası vermeyen bekleme eski sinyali görmez: süresini bekler.
        (await _wake.DelayAsync(TimeSpan.FromMilliseconds(20), _time, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Delay_returns_at_once_when_a_signal_came_after_the_caller_read_the_epoch()
    {
        // Döngü: sıra numarasını oku → kapıya bak ("oturum yok") → bekle. Sinyal araya girdiyse
        // bekleme hemen döner; yoksa oturum başlamışken döngü uzun beklemeye girerdi.
        var epoch = _wake.Epoch;
        _wake.Signal();

        (await _wake.DelayAsync(Long, _time, default, epoch).WaitAsync(TestPatience.Upper)).Should().BeTrue();
        // Sıra numarası güncelse bekleme olağan biçimde sürer.
        (await _wake.DelayAsync(TimeSpan.FromMilliseconds(20), _time, default, _wake.Epoch)).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        using var cancel = new CancellationTokenSource();
        var waiting = _wake.DelayAsync(Long, _time, cancel.Token);

        cancel.Cancel();

        var act = () => waiting.WaitAsync(TestPatience.Upper);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _wake.Signal().Should().Be(0, "iptal edilen bekleme sayılmaz");
    }
}
