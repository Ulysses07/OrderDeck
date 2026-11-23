using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Canlı sohbet satırının "Kayıtlı" işareti kabukta: mesaj listeye girerken gönderenin kaydına bakılır
/// ve yayın sırasında form dolduran kişinin ekrandaki satırları, form kayıtları eşitlenince döner.
/// Mesajlar gerçek yoldan geçirilir (veri yolu, sohbet kuyruğu, arayüz boşaltması); adlar ve telefonlar üretilir.
/// </summary>
public class MainShellChatRegisteredTests
{
    private static string Name() => $"izleyici_{Guid.NewGuid():N}"[..20];

    private static Customer Seed(MainShellTestHarness.Harness h, string username,
        string? phone = null, bool blacklisted = false)
    {
        var c = new Customer(Guid.NewGuid().ToString("N"), "instagram", username, username, null,
            100, 100, blacklisted, null, null, 0, 0m, null, null, phone);
        h.CustomerRepo.Insert(c);
        return c;
    }

    private static void Receive(MainShellTestHarness.Harness h, string username)
    {
        h.Bus.Publish(new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null,
            username, username, null, $"mesaj {Guid.NewGuid():N}", 1000, Array.Empty<string>()));
        h.Vm.DrainPendingChat();
    }

    [Fact]
    public void Gelen_mesajin_satiri_kayitli_musteride_isaretli_dogar()
    {
        using var h = MainShellTestHarness.Build();
        var ad = Name();
        Seed(h, ad, phone: TestPhone.NewE164());

        Receive(h, ad);

        h.Vm.ChatMessages.Should().ContainSingle().Which.IsSenderRegistered.Should().BeTrue();
    }

    [Fact]
    public void Taninmayan_gonderenin_satiri_isaretsizdir()
    {
        using var h = MainShellTestHarness.Build();

        Receive(h, Name());

        h.Vm.ChatMessages.Should().ContainSingle().Which.IsSenderRegistered.Should().BeFalse();
    }

    [Fact]
    public void Kara_liste_vurgusu_gelen_mesajda_calismaya_devam_eder()
    {
        // İki işaret aynı depo çağrısından geliyor; vurgu yolu bu değişiklikle bozulmamalı.
        using var h = MainShellTestHarness.Build();
        var ad = Name();
        Seed(h, ad, blacklisted: true);

        Receive(h, ad);

        var satir = h.Vm.ChatMessages.Should().ContainSingle().Subject;
        satir.IsSenderBlacklisted.Should().BeTrue();
        satir.IsSenderRegistered.Should().BeFalse();
    }

    [Fact]
    public void Yeni_form_kaydi_gelince_ayni_gonderenin_butun_satirlari_kayitliya_doner()
    {
        // Yayın sırasında formu dolduran kişi: ekrandaki eski mesajları da "Kayıtlı" görünmeli,
        // yoksa operatör aynı kişiyi bir satırda kayıtlı, öbüründe kayıtsız görür.
        using var h = MainShellTestHarness.Build();
        var ad = Name();
        var musteri = Seed(h, ad);
        Receive(h, ad);
        Receive(h, ad);
        h.Vm.ChatMessages.Should().OnlyContain(m => !m.IsSenderRegistered, "ön koşul: henüz kayıt yok");

        h.CustomerRepo.UpdatePhone(musteri.Id, TestPhone.NewE164()).Should().Be(1);
        h.Vm.NewIntakeSubmissionsCount += 1;            // form eşitlemesinin kabuğa haber verdiği yer

        h.Vm.ChatMessages.Should().HaveCount(2).And.OnlyContain(m => m.IsSenderRegistered);
    }

    [Fact]
    public void Sayac_sifirlaninca_satirlar_yeniden_sorgulanmaz()
    {
        // Sayaç, kayıt çekmecesi açılınca sıfırlanır; bu yeni bir kayıt değildir. Ekrandaki 500 satıra
        // kadar olan listeyi o anda yeniden sorgulamak arayüzü boşuna meşgul ederdi.
        using var h = MainShellTestHarness.Build();
        var ad = Name();
        var musteri = Seed(h, ad);
        Receive(h, ad);
        h.Vm.NewIntakeSubmissionsCount = 1;
        h.Vm.ChatMessages.Should().ContainSingle().Which.IsSenderRegistered.Should().BeFalse("ön koşul");
        h.CustomerRepo.UpdatePhone(musteri.Id, TestPhone.NewE164()).Should().Be(1);

        h.Vm.NewIntakeSubmissionsCount = 0;

        h.Vm.ChatMessages.Should().ContainSingle().Which.IsSenderRegistered.Should().BeFalse();
    }
}
