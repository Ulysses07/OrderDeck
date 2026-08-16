using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Labeling;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Barkod etiketi basma çekmecesi. Kurucusu private + statik <c>Create</c>:
/// <c>VariantPickerDrawer</c> ile aynı kalıp — denetim yalnız bir
/// <see cref="Drawer"/> bağlamında var olabilir.
///
/// <para>Yazıcıya dokunan tek yer burası; görünüm modeli
/// <see cref="BarcodeLabelViewModel"/> yalnız yükü hazırlıyor.</para>
/// </summary>
public partial class BarcodeLabelDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly BarcodeLabelViewModel _vm;

    private BarcodeLabelDrawer(Drawer drawer, BarcodeLabelViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        _vm = vm;
        DataContext = vm;
    }

    public static BarcodeLabelDrawer Create(Drawer drawer, BarcodeLabelViewModel vm)
        => new(drawer, vm);

    /// <summary>
    /// Basım FIRLATABİLİR: <c>BarcodeLabelDocument.Build</c> etikete sığmayan
    /// barkodu <see cref="ArgumentException"/> ile reddediyor (sığmayanı
    /// kırpmak stop deseni ve checksum'ı kesilmiş, gözle kusursuz görünen bir
    /// etiket basardı) ve yazıcı sürücüsü de patlayabiliyor. Yakalanmayan bir
    /// istisna Click işleyicisinde uygulamayı çökertirdi — yayının ortasında.
    ///
    /// <para>Hatada çekmece KAPANMIYOR: operatör seçimini düzeltip yeniden
    /// deneyebilsin. Kapatsaydık kartı ve seçimi baştan kurması gerekirdi.</para>
    ///
    /// <para><b>Basım arka planda</b> (<c>Task.Run</c>): <c>PrintDocument.Print()</c>
    /// senkron ve sürücü/spooler asılırsa UI thread'i süresiz kilitler — yazıcı
    /// çevrimdışıysa ya da kâğıdı bittiyse kabuk komple donar, sohbet akmaz,
    /// kod girilemez, yayın durur. Müşteri etiketi tarafında bu ders bir kez
    /// alınmıştı (<c>MainShellViewModel.PrintAsync</c>, 2026-05-13); aynı
    /// <c>PrintDocument</c>, aynı spooler.</para>
    ///
    /// <para>Yük arka plana GEÇMEDEN önce toplanıyor: <see cref="BarcodeLabelViewModel.Rows"/>
    /// bir <c>ObservableCollection</c>, başka thread'den okumak yasak.</para>
    /// </summary>
    private async void Print_OnClick(object sender, RoutedEventArgs e)
    {
        var services = App.Host.Services;
        var labels = _vm.BuildLabels();
        var copies = _vm.Copies;
        try
        {
            var printer = services.GetRequiredService<BarcodeLabelPrinter>();
            await Task.Run(() => printer.Print(labels, copies));
        }
        catch (Exception ex)
        {
            // İstisna mesajı zaten Türkçe ve hangi barkodun sığmadığını
            // yazıyor; operatöre olduğu gibi gösteriliyor.
            await services.GetRequiredService<IDialogService>()
                .ShowAsync(ex.Message, "Etiket basılamadı", DialogSeverity.Error);
            return;
        }

        _drawer.Close(true);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
