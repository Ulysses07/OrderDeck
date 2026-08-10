using OrderDeck.App.Services.Drawers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Çekmece yığınının sözleşmesi (Faz 2a altyapısı).
///
/// NEDEN: bu yığın <c>Window.ShowDialog()</c>'un yerini alıyor. Pencerenin
/// bloklamasını WPF garanti ediyordu; çekmecede o garantiyi bu sınıf veriyor —
/// kapanınca görev tamamlanmalı, sonuç doğru olmalı, çekmece listeden
/// düşmeli. Biri bozulursa uygulama kilitlenir ya da ekranda hayalet çekmece
/// kalır, ikisi de sessiz hatadır.
///
/// STA gerekmiyor: yığın saf CLR. Görsel katman (DrawerHost) ayrı test
/// ediliyor (MainShellViewCompositionTests).
/// </summary>
public class DrawerStackTests
{
    private static object Content(Drawer _) => new object();

    [Fact]
    public void Show_puts_the_drawer_on_top_and_opens_the_layer()
    {
        var stack = new DrawerStack();

        stack.ShowAsync("Müşteri", Content);

        Assert.True(stack.IsOpen);
        Assert.Single(stack.Items);
        Assert.Equal("Müşteri", stack.Top!.Title);
        Assert.True(stack.Top.IsTop);
    }

    [Fact]
    public void Content_factory_receives_the_drawer_it_will_live_in()
    {
        var stack = new DrawerStack();
        Drawer? seen = null;

        stack.ShowAsync("Müşteri", d => { seen = d; return new object(); });

        Assert.Same(stack.Top, seen);
        Assert.NotNull(stack.Top!.Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Closing_completes_the_task_with_the_given_result(bool confirmed)
    {
        var stack = new DrawerStack();
        var pending = stack.ShowAsync("Müşteri", Content);

        Assert.False(pending.IsCompleted);
        stack.Top!.Close(confirmed);

        Assert.Equal(confirmed, await pending);
        Assert.Empty(stack.Items);
        Assert.False(stack.IsOpen);
    }

    [Fact]
    public async Task Second_close_is_ignored()
    {
        // ESC ile başlıktaki çarpı aynı karede gelebilir; ikinci çağrı
        // TaskCompletionSource'u patlatmamalı.
        var stack = new DrawerStack();
        var pending = stack.ShowAsync("Müşteri", Content);
        var drawer = stack.Top!;

        drawer.Close(true);
        drawer.Close(false);

        Assert.True(await pending);
    }

    [Fact]
    public async Task Nested_drawers_stack_and_unwind_in_order()
    {
        // DekontEkle → KargoYönergesi zinciri: alttaki açıkken üstü kapanır ve
        // alttaki yeniden etkin olur.
        var stack = new DrawerStack();
        var outer = stack.ShowAsync("Dekont", Content);
        var outerDrawer = stack.Top!;
        var inner = stack.ShowAsync("Kargo", Content);
        var innerDrawer = stack.Top!;

        Assert.Equal(2, stack.Items.Count);
        Assert.False(outerDrawer.IsTop);
        Assert.True(innerDrawer.IsTop);

        innerDrawer.Close(true);

        Assert.True(await inner);
        Assert.False(outer.IsCompleted);
        Assert.Same(outerDrawer, stack.Top);
        Assert.True(outerDrawer.IsTop);
    }

    [Fact]
    public async Task Closing_a_lower_drawer_cancels_the_ones_it_opened()
    {
        // Alttaki kapanırsa üstündekiler ekranda kalamaz — ve onlar iptal
        // sayılır, çağıranları "onaylandı" sanmasın.
        var stack = new DrawerStack();
        var outer = stack.ShowAsync("Dekont", Content);
        var outerDrawer = stack.Top!;
        var inner = stack.ShowAsync("Kargo", Content);

        outerDrawer.Close(true);

        Assert.False(await inner);
        Assert.True(await outer);
        Assert.Empty(stack.Items);
    }

    [Fact]
    public async Task CloseTop_cancels_only_the_topmost()
    {
        var stack = new DrawerStack();
        var outer = stack.ShowAsync("Dekont", Content);
        var inner = stack.ShowAsync("Kargo", Content);

        Assert.True(stack.CloseTop());

        Assert.False(await inner);
        Assert.False(outer.IsCompleted);
        Assert.Single(stack.Items);
    }

    [Fact]
    public void CloseTop_returns_false_when_nothing_is_open()
    {
        // ESC işleyicisinin dayanağı: false ise tuş tüketilmez ve yedek
        // seçim modundan çıkış devreye girer.
        Assert.False(new DrawerStack().CloseTop());
    }
}
