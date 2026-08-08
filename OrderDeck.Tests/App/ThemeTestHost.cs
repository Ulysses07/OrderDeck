using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// WPF kaynak sözlüğü STA thread + kayıtlı "pack:" şeması ister. Üç tema
/// testinin ortak koşum düzeneği.
/// </summary>
internal static class ThemeTestHost
{
    /// <returns>Hata varsa metni, yoksa null.</returns>
    internal static string? Run(Action<ResourceDictionary> assert, string fileName)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = typeof(OrderDeck.App.App);                        // App assembly'sini yükle
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;  // "pack:" şemasını kaydet
                if (Application.Current is null) new Application();

                var dict = new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
                };

                assert(dict);
            }
            catch (Exception ex) { error = ex.ToString(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error;
    }
}
