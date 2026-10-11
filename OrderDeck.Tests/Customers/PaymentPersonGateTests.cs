using System;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// Kişi başına özel ödeme kilidi: aynı kişinin (ortak üyeli) iki ödeme akışı sıraya girer, başka
/// kişilerinki girmez; bekleme süresi sınırlıdır; hiçbir yazma kapsamı açıkken alınmaz (U17).
/// </summary>
public sealed class PaymentPersonGateTests
{
    private static readonly TimeSpan Uzun = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Ortak_uyeli_ikinci_alim_birinci_birakilana_kadar_bekler()
    {
        var gate = new PaymentPersonGate();
        var birinci = await gate.TryEnterAsync(new[] { "a", "b" }, Uzun);
        birinci.Should().NotBeNull();

        var ikinci = gate.TryEnterAsync(new[] { "b", "c" }, Uzun);
        (await Task.WhenAny(ikinci, Task.Delay(200))).Should().NotBeSameAs(ikinci, "b ortak üye — sıraya girer");

        birinci!.Dispose();
        var alinan = await ikinci.WaitAsync(Uzun);
        alinan.Should().NotBeNull();
        gate.IsHeld("a").Should().BeFalse();
        gate.IsHeld("c").Should().BeTrue("hepsi birlikte alındı");
        alinan!.Dispose();
        gate.IsHeld("b").Should().BeFalse();
    }

    [Fact]
    public async Task Ortak_uyesi_olmayan_kisiler_birbirini_beklemez()
    {
        var gate = new PaymentPersonGate();
        using var birinci = await gate.TryEnterAsync(new[] { "a" }, Uzun);

        var ikinci = await gate.TryEnterAsync(new[] { "b" }, TimeSpan.Zero);

        ikinci.Should().NotBeNull("başka kişinin akışı bekletilmez — genel kapı değil");
        ikinci!.Dispose();
    }

    [Fact]
    public async Task Sure_dolarsa_null_doner_hicbir_uye_alinmaz()
    {
        var gate = new PaymentPersonGate();
        using var birinci = await gate.TryEnterAsync(new[] { "a" }, Uzun);

        var ikinci = await gate.TryEnterAsync(new[] { "a", "z" }, TimeSpan.FromMilliseconds(50));

        ikinci.Should().BeNull("başka ödeme akışı sürüyor");
        gate.IsHeld("z").Should().BeFalse("kısmi alım yok");
    }

    [Fact]
    public async Task Birakma_iki_kez_cagrilabilir_baskasinin_alimini_bozmaz()
    {
        var gate = new PaymentPersonGate();
        var birinci = await gate.TryEnterAsync(new[] { "a" }, Uzun);
        birinci!.Dispose();
        using var ikinci = await gate.TryEnterAsync(new[] { "a" }, Uzun);

        birinci.Dispose();

        gate.IsHeld("a").Should().BeTrue("ikinci Dispose sonraki akışın kilidini bırakmaz");
    }

    [Fact]
    public async Task Yazma_kapsami_acikken_alinmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var gate = new PaymentPersonGate();
        using var write = DbWrite.Begin(db);

        var al = () => gate.TryEnterAsync(new[] { "a" }, Uzun);

        await al.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PaymentPersonGate*");
    }
}
