using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Core.Customers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// U13 — ödeme akışı süren müşteriler kümesi. Kiralama ile taşıma aynı kilitten geçer; kilidi
/// tutan akış onu ikinci kez isteyemez (iç içe çağrı kilidi sonsuza dek beklerdi — Release'te de).
/// Yazma kapsamı denetimleri (U17) WriteScopeGuardTests'te.
/// </summary>
public sealed class CustomerBusySetTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Ic_ice_RunLocked_kilitlenmez_firlatir()
    {
        var busy = new CustomerBusySet();
        // Arka planda: bir gerileme testi sonsuza dek asmasın, zaman aşımıyla düşsün.
        var nested = Task.Run(() => busy.RunLocked(_ => busy.RunLocked(_ => 0)));

        var wait = () => nested.WaitAsync(Deadline);
        await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CustomerBusySet*");
        busy.RunLocked(_ => 42).Should().Be(42, "dış gövde düşerken kilidi bıraktı");
    }

    [Fact]
    public async Task RunLocked_govdesinden_kiralama_firlatir()
    {
        var busy = new CustomerBusySet();
        Task<IDisposable>? inner = null;
        busy.RunLocked(_ =>
        {
            inner = busy.EnterAsync("c1");
            return 0;
        });

        var lease = () => inner!.WaitAsync(Deadline);
        await lease.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CustomerBusySet*",
            "gövde kiralamayı bekleseydi kilit sonsuza dek beklenirdi");
        (await busy.EnterAsync("c1").WaitAsync(Deadline)).Dispose();
    }

    [Fact]
    public async Task Govdede_kuyruga_alinan_is_govde_bittikten_sonra_kiralayabilir()
    {
        // Gövdenin içinde kuyruğa alınan iş akışı (AsyncLocal) miras alır; gövde bittikten sonra
        // koşunca yanlış alarm vermemeli — işaret gövdenin sonunda kapanır.
        var busy = new CustomerBusySet();
        using var gate = new ManualResetEventSlim();
        Task queued = Task.CompletedTask;
        busy.RunLocked(_ =>
        {
            queued = Task.Run(async () =>
            {
                gate.Wait();
                (await busy.EnterAsync("c1")).Dispose();
            });
            return 0;
        });

        gate.Set();
        await queued.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Govde_surerken_baska_akistaki_kiralama_bekler()
    {
        // Taşıma gövdesi koşarken kiralama başlayamaz: ödeme akışı kiralayıp Id'yi yeniden
        // çözdüğünde (C9) taşıma ya hiç başlamamış ya da commit edilmiştir.
        var busy = new CustomerBusySet();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var body = Task.Run(() => busy.RunLocked(isBusy =>
        {
            inside.SetResult();
            release.Wait();
            return isBusy("c1");
        }));
        await inside.Task.WaitAsync(Deadline);

        var lease = busy.EnterAsync("c1");
        await Task.Delay(100);
        lease.IsCompleted.Should().BeFalse("gövde kilidi tutuyor");

        release.Set();
        (await body.WaitAsync(Deadline)).Should().BeFalse("kira gövde sürerken başlamadı");
        using (await lease.WaitAsync(Deadline))
            busy.RunLocked(isBusy => isBusy("c1")).Should().BeTrue();
        busy.RunLocked(isBusy => isBusy("c1")).Should().BeFalse("kira bitti");
    }
}
