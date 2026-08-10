namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Tam-ekran açılış durumu açmanın tek yolu. Spec §6: "hiçbir şey pop-up
/// değil" — açılıştaki üç modal pencere bu katmana taşındı.
///
/// Üretimdeki uygulaması <see cref="AppGateStack"/> (aynı nesne hem servis hem
/// de GateHost'un bağlandığı yığın — arada üçüncü bir sınıf yok).
/// </summary>
public interface IAppGateService
{
    /// <summary>
    /// Gate'i açar ve kapanmasını bekler. true = onaylanarak kapandı.
    ///
    /// Başlık parametresi YOK: gate'in şeridi yok, başlığı içeriğin kendisi
    /// taşıyor.
    /// </summary>
    Task<bool> ShowAsync(Func<AppGate, object> buildContent);
}
