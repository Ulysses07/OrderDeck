using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Gates;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate katmanı gerçekten çiziliyor mu? (Faz 3'teki
/// MainShellViewCompositionTests kalıbı.)
///
/// NEDEN TEK [Fact]: her Fact kendi STA thread'ini açıyor. Hepsini tek
/// thread'de kurmak hem hızlı hem de "process başına tek Application"
/// kuralını en az zorlayan yol.
/// </summary>
public class GateCompositionTests
{
    [Fact]
    public void Gate_layer_composes()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var gates = new AppGateStack();
            var root = new AppRootView(gates);

            // Binding'ler DataBind önceliğinde dispatcher kuyruğuna giriyor;
            // Pump() ile boşaltıyoruz ki ilk değerlendirme tamamlansın.
            ThemeTestHost.Pump();

            // Gate yokken katman Collapsed ve shell yuvası boş.
            Assert.Equal(Visibility.Collapsed, root.GateHost.Visibility);
            Assert.False(root.IsShellMounted);

            // Shell yuvası doldurulabiliyor.
            root.MountShell(new Border());
            Assert.True(root.IsShellMounted);

            // Gate açılınca: katman görünür, içerik doğru nesne,
            // ShellHost Tab navigasyonuna kapalı.
            var content = new Border();
            _ = gates.ShowAsync(_ => content);

            // ShowAsync → PropertyChanged → binding kuyruğa girdi; Pump() boşaltır.
            ThemeTestHost.Pump();

            Assert.Equal(Visibility.Visible, root.GateHost.Visibility);
            Assert.Same(content, root.GateContent.Content);
            Assert.False(root.ShellHost.IsEnabled);

            // Açık Border gate'ini kapat; yığın temizlendikten sonra
            // BootGate bloğu boş bir yığından başlasın.
            gates.Top!.Close(false);
            ThemeTestHost.Pump();
            Assert.False(gates.IsOpen);
            Assert.Equal(Visibility.Collapsed, root.GateHost.Visibility);
            Assert.True(root.ShellHost.IsEnabled);

            // BootGate'i örneklemek ThemeTestHost altında tüm StaticResource
            // anahtarlarını çözüyor; hangi anahtar eksikse burada XamlParseException
            // atar — kaynak doğrulamasının asıl değeri bu.
            // Pump() ile binding kuyruğu boşaltıp katmanın görünürlüğünü ve
            // içeriğini GateHost/GateContent üzerinden doğruluyoruz.
            var pending = gates.ShowAsync(_ => new BootGate());
            ThemeTestHost.Pump();
            Assert.True(gates.IsOpen);
            Assert.IsType<BootGate>(root.GateContent.Content);
            Assert.Equal(Visibility.Visible, root.GateHost.Visibility);

            gates.Top!.Close(false);
            Assert.False(gates.IsOpen);
            Assert.True(pending.IsCompleted);

            // LoginGate DataContext'siz de çizilebilmeli: bu testin ölçtüğü şey
            // kaynak çözümlemesi. StaticResource anahtarlarından biri yanlışsa
            // XamlParseException atar; binding'ler sessizce boş kalır.
            var loginPending = gates.ShowAsync(g => LoginGate.Create(g, vm: null, isStartupGate: true));
            ThemeTestHost.Pump();
            var startupLogin = Assert.IsType<LoginGate>(root.GateContent.Content);

            // isStartupGate view'daki tek dal: açılışta iptal uygulamadan
            // çıkmak demek, o yüzden düğme "Çıkış" diyor.
            Assert.Equal("Çıkış", startupLogin.ExitButton.Content);

            gates.Top!.Close(false);
            Assert.True(loginPending.IsCompleted);

            // Aynı view çalışırken açıldığında shell altta duruyor: iptal
            // sadece bu ekranı kapatıyor, düğme bu yüzden "Vazgeç".
            var switchPending = gates.ShowAsync(g => LoginGate.Create(g, vm: null, isStartupGate: false));
            ThemeTestHost.Pump();
            var switchLogin = Assert.IsType<LoginGate>(root.GateContent.Content);
            Assert.Equal("Vazgeç", switchLogin.ExitButton.Content);

            gates.Top!.Close(false);
            Assert.True(switchPending.IsCompleted);

            // RestoreGate de servissiz çiziliyor. MEKANİZMA: StaticResource
            // anahtarları InitializeComponent() sırasında (BAML yüklenirken)
            // çözülüyor — Visibility'den bağımsız, yani iki durumun (seçim /
            // tamamlandı) gövdesindeki anahtarlar DataContext olmasa da
            // doğrulanıyor. AMA bir DataTemplate'in gövdesi ertelenmiş bir
            // akış: ancak bir öğe gerçekleştiğinde ayrıştırılıyor. vm=null
            // iken AvailableBackups boş kalıyor, ListBox sıfır öğe üretiyor
            // ve öğe şablonundaki anahtarlar HİÇ doğrulanmıyor (ölçüldü:
            // şablonun içindeki bir anahtarı bozmak testi yeşil bırakıyor,
            // dışındaki bir anahtarı bozmak XamlParseException atıyor).
            // Bu yüzden aşağıda view'a küçük bir sahte DataContext veriliyor.
            // NOT: LoginGate'in lisans listesindeki DataTemplate (OD.Text.Mono)
            // aynı kör noktada — o dosyaya bu turda dokunulmuyor.
            var restorePending = gates.ShowAsync(g => RestoreGate.Create(g, vm: null));
            ThemeTestHost.Pump();
            var restore = Assert.IsType<RestoreGate>(root.GateContent.Content);
            Assert.Equal(Visibility.Visible, root.GateHost.Visibility);

            // Seçim durumu: bir yedek besleyip öğe şablonunu gerçekleştiriyoruz.
            // IsMonthlyMilestone=true, "Aylık" çipinin dalı da ayrıştırılsın.
            restore.DataContext = new RestoreGateStub
            {
                RestoreCompleted = false,
                AvailableBackups =
                [
                    new BackupMetadata(Guid.NewGuid(), 1024, DateTimeOffset.Now, true, "TEZGAH-01")
                ]
            };
            ThemeTestHost.Pump();
            // Kapsayıcı üretimi ölçüm sırasında oluyor; gate hiçbir pencerede
            // olmadığı için yerleşimi elle tetiklemek gerekiyor.
            restore.Measure(new Size(1200, 900));
            restore.Arrange(new Rect(0, 0, 1200, 900));
            ThemeTestHost.Pump();

            Assert.Equal(Visibility.Visible, restore.SelectionState.Visibility);
            Assert.Equal(Visibility.Collapsed, restore.CompletedState.Visibility);
            Assert.NotNull(restore.BackupList.ItemContainerGenerator.ContainerFromIndex(0));

            // Tamamlandı durumu: taze bir stub atamak PropertyChanged
            // gerektirmiyor, DataContext'in kendisi değişiyor.
            restore.DataContext = new RestoreGateStub { RestoreCompleted = true };
            ThemeTestHost.Pump();
            restore.Measure(new Size(1200, 900));
            restore.Arrange(new Rect(0, 0, 1200, 900));
            ThemeTestHost.Pump();

            Assert.Equal(Visibility.Collapsed, restore.SelectionState.Visibility);
            Assert.Equal(Visibility.Visible, restore.CompletedState.Visibility);

            gates.Top!.Close(false);
            ThemeTestHost.Pump();
            Assert.True(restorePending.IsCompleted);
            Assert.False(gates.IsOpen);
            Assert.Equal(Visibility.Collapsed, root.GateHost.Visibility);
        });
        Assert.Null(error);
    }

    /// <summary>
    /// RestoreGate'in iki görünürlük binding'inin ihtiyacı olan asgari
    /// DataContext. Gerçek <c>RestoreDialogViewModel</c> servis istiyor;
    /// bu test kaynak/şablon çözümlemesini ölçtüğü için o kadarı gereksiz.
    /// PropertyChanged yok: durum değiştirmek için taze bir örnek atanıyor.
    /// </summary>
    private sealed class RestoreGateStub
    {
        public bool RestoreCompleted { get; init; }
        public IReadOnlyList<BackupMetadata> AvailableBackups { get; init; } = [];
        public string? StatusMessage { get; init; }
    }
}
