using System.Globalization;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Chat;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Sohbet satırının hesap etiketi ve ipucu (spec 2026-11-16 §8.6.2, §8.4; Ö1, TK11): etiket
/// <see cref="ChatMessage.SourceAccount"/>'un kendisidir ("@" kulvarda eklenir, arayüzde değil), ipucu Meta
/// damgasının yerel saat karşılığını verir. Hesap adları üretilir.
/// </summary>
public class ChatMessageViewModelTests
{
    private static string NewTag() => $"@hesap-{Guid.NewGuid():N}";
    private static ChatMessageViewModel Vm(string? sourceAccount) => new(
        new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, $"izleyici-{Guid.NewGuid():N}", null, null,
            "A12", 1_800_000_000, Array.Empty<string>(), sourceAccount), isSenderBlacklisted: false);

    [Fact]
    public void Kayitli_isareti_kurucudan_gelir_varsayilan_kapali()
    {
        var mesaj = Vm(null).Message;
        new ChatMessageViewModel(mesaj, isSenderBlacklisted: false).IsSenderRegistered.Should().BeFalse();
        new ChatMessageViewModel(mesaj, isSenderBlacklisted: false, isSenderRegistered: true)
            .IsSenderRegistered.Should().BeTrue();
    }

    [Theory, InlineData(null), InlineData(""), InlineData("  ")]
    public void SourceAccount_bosken_etiket_ve_ipucu_yok(string? source) =>
        (Vm(source).AccountTag, Vm(source).AccountTooltip).Should().Be(((string?)null, (string?)null));

    [Fact]
    public void Etiket_aynen_gosterilir_ikinci_at_eklenmez_Sayfa_adi_at_almaz()
    {
        var (tag, pageName) = (NewTag(), $"Sayfa {Guid.NewGuid():N}");
        (Vm(tag).AccountTag, Vm(pageName).AccountTag).Should().Be((tag, pageName));
    }

    [Fact]                                                             // UTC de geliştirme makinesinin +03:00'ü de olmayan sabit ofset: çevrilmeyen saat iki ortamda da düşer
    public void Ipucu_Meta_saatini_verilen_ofsette_yazar()
    {
        var tag = NewTag();
        var meta = new DateTimeOffset(2026, 11, 17, 18, 14, 7, TimeSpan.Zero).ToUnixTimeSeconds();
        ChatMessageViewModel.FormatAccountTooltip(tag, meta, TimeSpan.FromHours(5.5)).Should().Be($"Instagram: {tag} · 23:44:07");
    }

    [Fact]
    public void Ipucu_mesajin_damgasini_o_anin_yerel_ofsetiyle_verir()
    {
        var vm = Vm(NewTag());
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(vm.Message.ReceivedAt));
        vm.AccountTooltip.Should().Be(ChatMessageViewModel.FormatAccountTooltip(vm.AccountTag!, vm.Message.ReceivedAt, offset));
    }

    // ── Ek sabitlemeler: kuralların, yukarıdaki testlerin ayırt edemediği iki cümlesi ──

    /// <summary>
    /// Ofset mesajın ANINDAKİ yerel ofsettir, bugünkü ofset ya da sabit bir sayı değil. Kış ve yaz ortasından iki
    /// damga: yaz saati uygulayan (ya da uygulamış) bir saat diliminde ikisinin ofseti farklıdır, bugünün ofsetiyle
    /// çevirmek birinde yanlış saati yazar. 2015: Türkiye'nin de yaz saati uyguladığı bir yıl; geliştirme
    /// makinesinin bugünkü sabit +03:00'ü o kışı tutmaz. Yaz saati olmayan dilimde (CI: UTC) iki durum aynı
    /// ofsetle geçer.
    /// </summary>
    [Theory, InlineData(1), InlineData(7)]
    public void Ipucu_ofseti_mesajin_anindan_alir_bugunden_degil(int month)
    {
        var stamp = new DateTimeOffset(2015, month, 15, 12, 0, 0, TimeSpan.Zero);
        var vm = new ChatMessageViewModel(Vm(NewTag()).Message with { ReceivedAt = stamp.ToUnixTimeSeconds() },
            isSenderBlacklisted: false);
        var local = stamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        vm.AccountTooltip.Should().Be($"Instagram: {vm.AccountTag} · {local}");
    }

    /// <summary>
    /// Saat her makinede <c>HH:mm:ss</c> yazılır. Özel biçimde ":" o anki kültürün saat ayracıdır; ayracı nokta
    /// olan bir bölgesel ayarda kültüre bırakılan biçim noktayla yazardı.
    /// </summary>
    [Fact]
    public void Ipucu_saati_makinenin_bolgesel_ayarindan_bagimsiz_iki_noktayla_yazilir()
    {
        var dotted = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        dotted.DateTimeFormat.TimeSeparator = ".";
        var (tag, before) = (NewTag(), CultureInfo.CurrentCulture);
        var stamp = new DateTimeOffset(2026, 11, 17, 18, 14, 7, TimeSpan.Zero);
        CultureInfo.CurrentCulture = dotted;
        try
        {
            stamp.ToString("HH:mm:ss").Should().Be("18.14.07", "ön koşul: bu kültürde saat ayracı nokta");
            ChatMessageViewModel.FormatAccountTooltip(tag, stamp.ToUnixTimeSeconds(), TimeSpan.Zero)
                .Should().Be($"Instagram: {tag} · 18:14:07");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
