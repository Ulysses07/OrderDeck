using OrderDeck.App.Services.Gates;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate yığınının sözleşmesi (Faz 4a altyapısı).
///
/// NEDEN: bu yığın açılıştaki üç <c>ShowDialog()</c>'un yerini alıyor.
/// Pencerenin bloklamasını WPF garanti ediyordu; burada o garantiyi bu sınıf
/// veriyor. Bozulursa açılış ya kilitlenir ya da shell hiç kurulmaz.
///
/// STA gerekmiyor: yığın saf CLR. Görsel katman ayrı test ediliyor
/// (GateCompositionTests).
/// </summary>
public class AppGateStackTests
{
    private static object Content(AppGate _) => new object();

    [Fact]
    public void Show_opens_the_layer_and_puts_the_gate_on_top()
    {
        var stack = new AppGateStack();

        stack.ShowAsync(Content);

        Assert.True(stack.IsOpen);
        Assert.Single(stack.Items);
        Assert.NotNull(stack.Top!.Content);
    }

    [Fact]
    public void Content_factory_receives_the_gate_it_will_live_in()
    {
        var stack = new AppGateStack();
        AppGate? seen = null;

        stack.ShowAsync(g => { seen = g; return new object(); });

        Assert.Same(stack.Top, seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Closing_completes_the_task_with_the_given_result(bool confirmed)
    {
        var stack = new AppGateStack();
        var pending = stack.ShowAsync(Content);

        Assert.False(pending.IsCompleted);
        stack.Top!.Close(confirmed);

        Assert.Equal(confirmed, await pending);
        Assert.Empty(stack.Items);
        Assert.False(stack.IsOpen);
    }

    [Fact]
    public async Task Second_close_is_ignored()
    {
        var stack = new AppGateStack();
        var pending = stack.ShowAsync(Content);
        var gate = stack.Top!;

        gate.Close(true);
        gate.Close(false);

        Assert.True(await pending);
    }

    [Fact]
    public async Task Nested_gates_stack_and_unwind_in_order()
    {
        // FirstRunGate → LoginGate zinciri: üstteki kapanınca sihirbaz geri gelir.
        var stack = new AppGateStack();
        var outer = stack.ShowAsync(Content);
        var outerGate = stack.Top!;
        var inner = stack.ShowAsync(Content);
        var innerGate = stack.Top!;

        Assert.Equal(2, stack.Items.Count);
        Assert.NotSame(outerGate, innerGate);

        innerGate.Close(true);

        Assert.True(await inner);
        Assert.False(outer.IsCompleted);
        Assert.Same(outerGate, stack.Top);
    }

    [Fact]
    public async Task Closing_a_lower_gate_cancels_the_ones_it_opened()
    {
        var stack = new AppGateStack();
        var outer = stack.ShowAsync(Content);
        var outerGate = stack.Top!;
        var inner = stack.ShowAsync(Content);

        outerGate.Close(true);

        Assert.False(await inner);
        Assert.True(await outer);
        Assert.Empty(stack.Items);
    }
}
