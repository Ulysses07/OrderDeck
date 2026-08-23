using System.Linq;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.App;

public class MainShellChatFilterTests
{
    private static int VisibleCount(MainShellViewModel vm) =>
        vm.ChatView.Cast<ChatMessageViewModel>().Count();

    [Fact]
    public void No_filter_shows_every_message()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "merhaba"));

        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Search_matches_username_and_text_case_insensitively()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "merhaba"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "AYSE'ye selam"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("zeynep", "başka"));

        h.Vm.ChatSearchText = "ayse";

        // Kullanıcı adı ya da metin — operatör hangisini hatırlarsa.
        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Only_active_code_filters_by_the_hero_code()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "a100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200 alıyorum"));
        h.Vm.ActiveCode = "A100";

        h.Vm.OnlyActiveCode = true;

        VisibleCount(h.Vm).Should().Be(1);
    }

    [Fact]
    public void Only_active_code_is_inert_while_no_code_is_active()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "a100"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200"));
        h.Vm.ActiveCode = "";

        h.Vm.OnlyActiveCode = true;

        // Kod yokken her şeyi gizlemek sohbeti boşaltır ve operatör paneli
        // bozuldu sanır — filtre kendini devre dışı bırakır.
        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Filters_combine()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "A100 alıyorum"));
        h.Vm.ActiveCode = "A100";

        h.Vm.OnlyActiveCode = true;
        h.Vm.ChatSearchText = "fatma";

        VisibleCount(h.Vm).Should().Be(1);
    }

    [Fact]
    public void Changing_the_active_code_refreshes_the_view()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200"));
        h.Vm.ActiveCode = "A100";
        h.Vm.OnlyActiveCode = true;

        h.Vm.ActiveCode = "B200";

        // Filtre delegesi ActiveCode'u okuyor ama CollectionView bunu
        // kendiliğinden bilmez — Refresh() tetiklenmeli.
        VisibleCount(h.Vm).Should().Be(1);
        h.Vm.ChatView.Cast<ChatMessageViewModel>().Single().Username.Should().Be("fatma");
    }
}
