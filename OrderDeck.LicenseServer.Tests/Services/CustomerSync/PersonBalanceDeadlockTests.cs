using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalance.IsDeadlock"/> — grup geri almasının kilitlenme kurbanını (1205) yeniden deneme kararı
/// (spec 2026-10-11 §4.5; saf). Varsayılan yürütme stratejisi (<c>EnableRetryOnFailure</c> yok) 1205'i
/// <see cref="InvalidOperationException"/>'a sarar; süzgeç yalnız çıplak <see cref="DbUpdateException"/>'a bakarsa
/// kurban 500 alır. Okuma da kurban olabilir: sorgu komutu çalışırken gelen 1205'i strateji sarar, satırlar okunurken
/// gelen ise (<c>ReadAsync</c>, stratejinin dışı) çıplak <see cref="Microsoft.Data.SqlClient.SqlException"/> olarak
/// fırlar. Paralel yarış testleri (§8.2-3) bunları yalnız olasılıkla yakalar; bu sınıf her koşuda yakalar. EF
/// hattındaki gerçek sarma biçimi <c>PersonBalanceReversalDeadlockTests</c>'te (Testcontainers).
/// </summary>
public sealed class PersonBalanceDeadlockTests
{
    private static Microsoft.Data.SqlClient.SqlException Sql(int number) =>
        SqlExceptionFactory.Create(number, "Transaction was deadlocked");

    private static DbUpdateException Save(int sqlNumber) => new("kayıt düştü", Sql(sqlNumber));

    /// <summary>Varsayılan stratejinin sarması: geçici hata → "likely due to a transient failure".</summary>
    private static InvalidOperationException Transient(Exception inner) => new("geçici hata", inner);

    [Fact]
    public void Kayitta_sarili_ve_ciplak_1205_kilitlenme_sayilir()
    {
        PersonBalance.IsDeadlock(Transient(Save(1205))).Should().BeTrue("varsayılan stratejinin kayıttaki biçimi");
        PersonBalance.IsDeadlock(Save(1205)).Should().BeTrue("sarmalamayan bir stratejiyle gelen biçim");
    }

    [Fact]
    public void Okumada_sarili_ve_ciplak_1205_kilitlenme_sayilir()
    {
        PersonBalance.IsDeadlock(Transient(Sql(1205))).Should().BeTrue("sorgu komutu çalışırken: strateji sarar");
        PersonBalance.IsDeadlock(Sql(1205)).Should().BeTrue("satırlar okunurken: stratejinin dışı, çıplak");
    }

    [Fact]
    public void Kilitlenme_olmayan_hatalar_kilitlenme_sayilmaz()
    {
        PersonBalance.IsDeadlock(Transient(Save(2601))).Should().BeFalse("tekil indeks ihlali yeniden denenmez");
        PersonBalance.IsDeadlock(Sql(2601)).Should().BeFalse();
        PersonBalance.IsDeadlock(new InvalidOperationException("iç istisna yok")).Should().BeFalse();
        PersonBalance.IsDeadlock(new DbUpdateConcurrencyException("jeton çakışması")).Should().BeFalse(
            "jeton çakışmasının kendi dalı var");
    }
}
