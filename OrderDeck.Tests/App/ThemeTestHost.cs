using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// WPF kaynak sözlüğü STA thread + kayıtlı "pack:" şeması ister. Tema
/// testlerinin ortak koşum düzeneği.
/// </summary>
internal static class ThemeTestHost
{
    /// <summary>Tek bir tema sözlüğünü yükleyip doğrulamayı çalıştırır.</summary>
    /// <returns>Hata varsa metni, yoksa null.</returns>
    internal static string? Run(Action<ResourceDictionary> assert, string fileName)
        => RunOnSta(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
            };

            assert(dict);
        });

    /// <summary>Gövdeyi hazır bir WPF uygulaması olan STA thread'inde çalıştırır.</summary>
    /// <returns>Hata varsa metni, yoksa null.</returns>
    private static readonly Lock AppGate = new();

    internal static string? RunOnSta(Action body)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;  // "pack:" şemasını kaydet

                // Düz Application değil, uygulamanın kendi App tipi: App.xaml'in
                // x:Class'ı OrderDeck.App.App olduğu için Application.LoadComponent
                // örneğin tipiyle BAML kökünün eşleşmesini istiyor. Süreç başına
                // tek Application yaratılabildiğinden bu tip her yerde aynı olmalı
                // — WPF'e dokunan HER test bu düzenekten geçmeli, yoksa hangisi
                // önce koşarsa Application.Current'ın tipini o belirler.
                // Kilit: xUnit koleksiyonları paralel koşuyor, iki thread aynı
                // anda "Current is null" görüp ikinci Application'ı yaratmasın.
                // InitializeComponent'i BURADA, örneği yaratan thread'de ve aynı
                // kilit altında çağırıyoruz: App.xaml'in merged dictionary'leri
                // (OD.* token'ları) olmadan view örneklemek her StaticResource'ta
                // XamlParseException atar. Application bir DispatcherObject —
                // onu başka bir thread'den yüklemeye kalkmak (her [Fact] kendi STA
                // thread'ini açtığı için kaçınılmaz olurdu) InvalidOperationException
                // riski taşır. Bir kere, doğru thread'de.
                lock (AppGate)
                {
                    if (Application.Current is null)
                    {
                        var app = new OrderDeck.App.App();
                        app.InitializeComponent();
                    }
                }

                body();
            }
            catch (Exception ex) { error = ex.ToString(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error;
    }

    /// <summary>
    /// Bekleyen dispatcher işlerini boşaltır. Gerekçe ölçümle bulundu: bir
    /// öğenin <c>DataContext</c>'i yerleşimden SONRA atanınca binding
    /// güncellemesi kuyruğa giriyor, <c>UpdateLayout()</c> onu boşaltmıyor.
    /// Sonuç sessiz: <c>ItemsControl</c> ölçülü ve görünür oluyor ama
    /// <c>ItemsSource</c> null kalıyor, yani test hiçbir şeyi doğrulamadan
    /// geçiyor. Uygulamada dispatcher zaten dönüyor, sorun teste özgü.
    /// </summary>
    internal static void Pump()
        => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
}
