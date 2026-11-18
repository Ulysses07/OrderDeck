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

    [Theory, InlineData(null), InlineData(""), InlineData("  ")]
    public void SourceAccount_bosken_etiket_ve_ipucu_yok(string? source) =>
        (Vm(source).AccountTag, Vm(source).AccountTooltip).Should().Be(((string?)null, (string?)null));

    [Fact]
    public void Etiket_aynen_gosterilir_ikinci_at_eklenmez_Sayfa_adi_at_almaz()
    {
        var (tag, pageName) = (NewTag(), $"Sayfa {Guid.NewGuid():N}");
        (Vm(tag).AccountTag, Vm(pageName).AccountTag).Should().Be((tag, pageName));
    }

    [Fact]                                                             // Review Focus 4: UTC de geliştirme makinesinin +03:00'ü de olmayan sabit ofset
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
}
