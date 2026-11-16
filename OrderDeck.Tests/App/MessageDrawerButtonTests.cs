using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views.Drawers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Onay çekmecesinin düğme yazıları (ortak bakiye spec 2026-10-11 §6.1-e): Ayır onayı [Ayır] [Vazgeç] der; var olan
/// sorular [Evet] [Vazgeç], bildirim tek [Tamam] kalır. Sahte diyalog servisi yalnız parametreyi kaydeder — yazının
/// düğmeye gerçekten yazıldığını ancak görünümün kendisi gösterir. MessageDrawer yalnız yığın üzerinden kurulur
/// (Drawer'ın ctor'u internal; MainShellViewCompositionTests emsali).
/// </summary>
public class MessageDrawerButtonTests
{
    [Fact] // Tek Fact: her Fact kendi STA thread'ini açıyor
    public void Onay_dugmesi_yazisi_parametreden_gelir_varsayilan_Evet_bildirim_Tamam()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            static MessageDrawer Ac(Func<Drawer, MessageDrawer> create)
            {
                var stack = new DrawerStack();
                _ = stack.ShowAsync("Başlık", create);
                return Assert.IsType<MessageDrawer>(stack.Top!.Content);
            }

            static Button Dugme(MessageDrawer drawer, string name) => (Button)drawer.FindName(name);

            var varsayilan = Ac(d => MessageDrawer.ForConfirm(d, "Emin misin?"));
            Assert.Equal("Evet", Dugme(varsayilan, "ConfirmButton").Content);
            Assert.Equal("Vazgeç", Dugme(varsayilan, "CancelButton").Content);
            Assert.Equal(Visibility.Visible, Dugme(varsayilan, "CancelButton").Visibility);

            var ayir = Ac(d => MessageDrawer.ForConfirm(d, "Ayırınca her hesap kendi bakiyesiyle kalır.", "Ayır"));
            Assert.Equal("Ayır", Dugme(ayir, "ConfirmButton").Content);
            Assert.Equal("Vazgeç", Dugme(ayir, "CancelButton").Content);
            Assert.Equal(Visibility.Visible, Dugme(ayir, "CancelButton").Visibility);

            var bildirim = Ac(d => MessageDrawer.ForNotice(d, "Bilgi", DialogSeverity.Warning));
            Assert.Equal("Tamam", Dugme(bildirim, "ConfirmButton").Content);
            Assert.Equal(Visibility.Collapsed, Dugme(bildirim, "CancelButton").Visibility);
        });

        Assert.Null(error);
    }
}
