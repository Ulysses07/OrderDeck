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

    /// <summary>Yalnız asılmaya karşı üst sınır: bir gerileme testi asmasın, düşsün. Doğruluk hiçbir
    /// adımda süreye bağlı değil; yükte (iş parçacığı havuzu doluyken) yanlış alarm vermesin diye
    /// cömert.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

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
        //
        // Zamanlamaya dayanmaz (yükte bir kez düştü): adımlar sinyalle sıralanır; gövde havuz dışı
        // kendi iş parçacığında koşar (havuz doluyken başlaması gecikmesin); "kira bekliyor"
        // denetimi bir bekleme süresiyle değil EnterAsync'in eşzamanlı dönüşüyle yapılır. Süre
        // sınırları yalnız asılmaya karşı (HangGuard).
        var busy = new CustomerBusySet();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var body = Task.Factory.StartNew(() => busy.RunLocked(isBusy =>
        {
            inside.Set();
            release.Wait();
            return isBusy("c1");
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            inside.Wait(HangGuard).Should().BeTrue("gövde kilidi aldı");

            // EnterAsync ilk await'e kadar çağıranın iş parçacığında koşar: kilit boş olsaydı kira
            // burada TAMAMLANMIŞ dönerdi. Tutulan kilitte bekler — gecikme gerekmez.
            var lease = busy.EnterAsync("c1");
            lease.IsCompleted.Should().BeFalse("gövde kilidi tutuyor");

            release.Set();
            (await body.WaitAsync(HangGuard)).Should().BeFalse("kira gövde sürerken başlamadı");
            using (await lease.WaitAsync(HangGuard))
                busy.RunLocked(isBusy => isBusy("c1")).Should().BeTrue();
            busy.RunLocked(isBusy => isBusy("c1")).Should().BeFalse("kira bitti");
        }
        finally
        {
            release.Set();   // bir denetim düşerse gövde iş parçacığı asılı kalmasın
        }
    }
}
