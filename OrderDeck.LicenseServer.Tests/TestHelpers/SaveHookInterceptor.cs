using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Test kancası: <c>SaveChangesAsync</c>'in HEMEN ÖNCESİNDE ya da HEMEN
/// SONRASINDA rastgele bir iş çalıştırır. Amacı, "tam o anda başka biri
/// yazdı" senaryosunu zamanlamaya değil, SIRAYA dayalı olarak kurmak —
/// <c>Task.Run</c> + <c>Delay</c> ile kurulan yarışlar CI'da flaky olur.
///
/// <para><b>Yeniden girmez.</b> Kanca gövdesi kendi <c>SaveChanges</c>'ini
/// atıyor (zaten bütün mesele o); bayrak olmasaydı kanca kendini sonsuz
/// tetiklerdi. Bayrak <b>örnek</b> düzeyinde, <c>static</c> değil: her test
/// sınıfı kendi fabrikasını kurduğu için paralel sınıflar birbirinin
/// kancasını kilitlemez.</para>
///
/// <para>Kanca <b>her</b> kaydetmede koşar; "yalnız bir kez" isteyen test
/// gövdenin ilk satırında alanı <c>null</c>'lar. İkisi de gerekiyor:
/// devam-ettirme yarışı tek seferlik, retry tükenmesi ise tur tur
/// çakışmak zorunda.</para>
///
/// <para>Boş bırakıldığında tamamen şeffaftır — bu interceptor'ı taşıyan
/// fabrikayı kullanan diğer testlerin davranışı değişmez.</para>
/// </summary>
public sealed class SaveHookInterceptor : SaveChangesInterceptor
{
    private bool _running;

    /// <summary>Yazım diske inmeden önce koşar.</summary>
    public Func<Task>? BeforeSave { get; set; }

    /// <summary>Yazım başarıyla indikten sonra koşar.</summary>
    public Func<Task>? AfterSave { get; set; }

    public void Reset()
    {
        BeforeSave = null;
        AfterSave = null;
        _running = false;
    }

    private async Task RunAsync(Func<Task>? hook)
    {
        if (hook is null || _running) return;
        _running = true;
        try { await hook(); }
        finally { _running = false; }
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(BeforeSave);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(AfterSave);
        return result;
    }
}
