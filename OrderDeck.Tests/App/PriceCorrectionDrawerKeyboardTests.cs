using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluentAssertions;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Drawers;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>"Fiyatı düzelt" çekmecesinin "yeniden basılsın mı?" sorusu (K5, varsayılan EVET) gerçek WPF
/// klavye yolunda: çekmece modal değil, Enter yalnız odak çekmecedeyken Evet demek.</summary>
public class PriceCorrectionDrawerKeyboardTests
{
    [Fact]
    public void Enter_in_another_box_does_not_reprint_and_the_question_focuses_yes()
    {
        // "Evet, yeniden bas" IsDefault idi: IsDefault Enter'ı bütün pencereye kaydeder. Soru açıkken sohbet
        // aramasında (ya da kod kutusunda) basılan Enter 18 etiketi istenmeden "DÜZELTME" ile yeniden basıyordu.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var clock = new Mock<IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
            var labels = new LabelRepository(db);
            var svc = new LabelService(labels,
                new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock.Object),
                db, clock.Object);
            var a = svc.Add("s1", new ChatMessage(System.Guid.NewGuid().ToString("N"), "instagram", null, "@a", "@a",
                null, "aldım", 1000, System.Array.Empty<string>()), 250m, "A12");
            svc.MarkPrintedAndRecord(new[] { a.Id });
            var reprints = 0;
            var vm = new PriceCorrectionViewModel(svc, payments: null, "s1", new[] { a.Id },
                PriceCorrectionMode.Printed, System.DateTime.Now, inFlight: () => null, applied: _ => { },
                reprint: _ =>
                {
                    reprints++;
                    return Task.FromResult<string?>(null);
                },
                cloudApiEnabled: true);

            var drawer = PriceCorrectionDrawer.Create(vm);
            // Pencerenin başka bir yerindeki kutu (sohbet araması gibi).
            var outside = new TextBox { Style = (Style)Application.Current.FindResource("OD.TextBox") };
            var root = new DockPanel();
            DockPanel.SetDock(outside, Dock.Top);
            root.Children.Add(outside);
            root.Children.Add(drawer);
            var window = new Window
            {
                Content = root, Width = 420, Height = 800, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                ThemeTestHost.Pump();
                vm.NewPriceText = "205";
                vm.ConfirmCommand.Execute(null);
                vm.IsReprintAsked.Should().BeTrue();
                PumpAll();

                var yes = drawer.ResultView.ReprintYesButton;   // sonuç görünümü çekmece ile FİYAT kutusunda ortak
                yes.IsFocused.Should().BeTrue("varsayılan Evet: soru açılınca odak Evet'te");

                // Operatör yayına döndü: sohbet aramasında Enter.
                outside.Focus();
                PressEnter(outside);
                ThemeTestHost.Pump();
                reprints.Should().Be(0, "Enter başka kutuda — yeniden baskı istenmedi");
                vm.IsReprintAsked.Should().BeTrue();

                // Odak Evet'teyken Enter = Evet.
                yes.Focus();
                PressEnter(yes);
                ThemeTestHost.Pump();
                reprints.Should().Be(1);
            }
            finally
            {
                window.Close();
            }
        });

        error.Should().BeNull();
    }

    /// <summary>Girdi önceliğindeki işler dahil kuyruğu boşaltır (<see cref="ThemeTestHost.Pump"/> Loaded'da
    /// durur; odak taşıma Input önceliğinde).</summary>
    private static void PumpAll()
        => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Gerçek klavye yolundan (InputManager) Enter: erişim tuşu yöneticisi (IsDefault) de görür.</summary>
    private static void PressEnter(UIElement target)
    {
        var source = PresentationSource.FromVisual(target)!;
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = target,
        });
    }
}
