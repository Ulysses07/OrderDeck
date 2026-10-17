using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sessions;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Devir kapısı (spec 2026-10-13 §5.2; W1-07): oturum başına süreç içi kilit — sıfır beklemeli deneme, sırası gelince
/// alma, iptal, oturumların birbirinden bağımsızlığı, bırakılan kilidin bellekte kalmaması ve DI'da tek örnek
/// (kapanış gönderimi ile W2'nin yönlendiricisi aynı kilitleri görmeli).
/// </summary>
public sealed class HandoverGateTests
{
    [Fact]
    public void TryEnter_bos_kilidi_alir_tutulurken_ikinci_deneme_reddedilir_birakinca_acilir()
    {
        var gate = new HandoverGate();

        gate.TryEnter("s1", out var first).Should().BeTrue();
        gate.TryEnter("s1", out var second).Should().BeFalse("sıfır bekleme: kilit başkasında");
        second.Should().BeNull();
        gate.TryEnter("s2", out var other).Should().BeTrue("başka oturum etkilenmez");

        first!.Dispose();
        gate.TryEnter("s1", out var again).Should().BeTrue();
        again!.Dispose();
        other!.Dispose();
        gate.EntryCount.Should().Be(0, "bırakılan kilit bellekte kalmaz");
    }

    [Fact]
    public async Task EnterAsync_sirasi_gelene_kadar_bekler()
    {
        var gate = new HandoverGate();
        var held = await gate.EnterAsync("s1");

        var waiting = gate.EnterAsync("s1");
        await Task.Delay(100);
        waiting.IsCompleted.Should().BeFalse();
        (await gate.EnterAsync("s2")).Dispose();

        held.Dispose();
        using var acquired = await waiting.WaitAsync(TestPatience.Upper);
        gate.TryEnter("s1", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Beklerken_iptal_edilen_giris_kilidi_almaz_ve_iz_birakmaz()
    {
        var gate = new HandoverGate();
        var held = await gate.EnterAsync("s1");
        using var cts = new CancellationTokenSource();

        var waiting = gate.EnterAsync("s1", cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();
        gate.EntryCount.Should().Be(0);
        gate.TryEnter("s1", out var lease).Should().BeTrue();
        lease!.Dispose();
    }

    [Fact]
    public void Iki_kez_birakmak_kilidi_iki_kez_acmaz()
    {
        var gate = new HandoverGate();
        gate.TryEnter("s1", out var first).Should().BeTrue();
        first!.Dispose();
        gate.TryEnter("s1", out var second).Should().BeTrue();

        first.Dispose();                                                  // ikinci bırakma etkisiz

        gate.TryEnter("s1", out _).Should().BeFalse("ikinci tutanın kilidi açılmamalı");
        second!.Dispose();
    }

    [Fact]
    public void DIda_tek_ornek_ve_gonderim_servisi_ayni_ornegi_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var gate = host.Services.GetRequiredService<HandoverGate>();

        host.Services.GetRequiredService<HandoverGate>().Should().BeSameAs(gate);
        typeof(SessionOrderSyncService).GetField("_handover", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host.Services.GetRequiredService<SessionOrderSyncService>())
            .Should().BeSameAs(gate, "kapanış gönderimi ile W2'nin düzenleme yönlendiricisi aynı kilitleri görmeli");
    }
}
