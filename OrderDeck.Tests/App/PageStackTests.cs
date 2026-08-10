using OrderDeck.App.Services.Pages;

namespace OrderDeck.Tests.App;

/// <summary>
/// Sayfa yığınının sözleşmesi (Faz 3 altyapısı).
///
/// NEDEN: bu yığın on <c>Window.ShowDialog()</c>'un yerini alıyor. Kardeşi
/// <see cref="DrawerStackTests"/> ile aynı gerekçe, iki farkla: sonuç yok
/// (kapanış <c>Task</c> tamamlar, bool döndürmez) ve <see cref="Page.Key"/>
/// var — sol nav'ın vurgusu <c>CurrentKey</c>'e bağlı, yanlışsa operatör
/// hangi sayfada olduğunu göremez.
///
/// STA gerekmiyor: yığın saf CLR.
/// </summary>
public class PageStackTests
{
    private static object Content(Page _) => new object();

    [Fact]
    public void Show_puts_the_page_on_top_and_opens_the_layer()
    {
        var stack = new PageStack();

        stack.ShowAsync("blacklist", "Kara Liste", Content);

        Assert.True(stack.IsOpen);
        Assert.Single(stack.Items);
        Assert.Equal("Kara Liste", stack.Top!.Title);
        Assert.Equal("blacklist", stack.CurrentKey);
    }

    [Fact]
    public void Content_factory_receives_the_page_it_will_live_in()
    {
        // AccountPage bunu kullanıyor: ViewModel'in RequestClose'u sayfayı
        // kapatabilsin diye fabrikaya Page geçiliyor.
        var stack = new PageStack();
        Page? seen = null;

        stack.ShowAsync("account", "Hesap", p => { seen = p; return new object(); });

        Assert.Same(stack.Top, seen);
        Assert.NotNull(stack.Top!.Content);
    }

    [Fact]
    public async Task Closing_completes_the_task_and_empties_the_layer()
    {
        var stack = new PageStack();
        var pending = stack.ShowAsync("blacklist", "Kara Liste", Content);

        Assert.False(pending.IsCompleted);
        stack.Top!.Close();

        await pending;
        Assert.Empty(stack.Items);
        Assert.False(stack.IsOpen);
        Assert.Null(stack.CurrentKey);
    }

    [Fact]
    public async Task Second_close_is_ignored()
    {
        // ESC ile geri oku aynı karede gelebilir; ikinci çağrı
        // TaskCompletionSource'u patlatmamalı.
        var stack = new PageStack();
        var pending = stack.ShowAsync("blacklist", "Kara Liste", Content);
        var page = stack.Top!;

        page.Close();
        page.Close();

        await pending;
    }

    [Fact]
    public async Task Nested_pages_stack_and_unwind_in_order()
    {
        // Yayın Geçmişi → Yayın Raporu zinciri: yığının var olma sebebi.
        var stack = new PageStack();
        var history = stack.ShowAsync("history", "Yayın Geçmişi", Content);
        var report = stack.ShowAsync("stream-report", "Yayın Raporu", Content);

        Assert.Equal(2, stack.Items.Count);
        Assert.Equal("stream-report", stack.CurrentKey);

        Assert.True(stack.Back());

        await report;
        Assert.False(history.IsCompleted);
        // Vurgu geçmişe DÖNMELİ; dönmezse nav rapordayken kalır.
        Assert.Equal("history", stack.CurrentKey);
    }

    [Fact]
    public async Task Closing_a_lower_page_also_closes_the_ones_it_opened()
    {
        var stack = new PageStack();
        var history = stack.ShowAsync("history", "Yayın Geçmişi", Content);
        var historyPage = stack.Top!;
        var report = stack.ShowAsync("stream-report", "Yayın Raporu", Content);

        historyPage.Close();

        await report;
        await history;
        Assert.Empty(stack.Items);
    }

    [Fact]
    public void Back_returns_false_when_nothing_is_open()
    {
        // ESC işleyicisinin dayanağı: false ise tuş tüketilmez ve altındaki
        // davranışa (yedek seçim modundan çıkış) yol verilir.
        Assert.False(new PageStack().Back());
    }
}
