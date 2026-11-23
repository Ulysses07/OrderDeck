using System.Windows.Controls;
using FluentAssertions;

namespace OrderDeck.Tests;

/// <summary>
/// <see cref="WpfTypeInitWarmup"/>'ın korumasının koruması. Kilitlenmenin kendisi süreç başına bir
/// kez çalışan tip başlatıcılarına bağlı olduğu için süreç içi bir testle yeniden üretilemez; burada
/// sabitlenen, onu imkânsız kılan düzeneğin yerinde durduğu: ısınma testlerden önce koşmuş, olaya
/// karışan tipleri kapsamış ve hiçbir statik kurucuyu düşürmemiş olmalı. Özniteliği silen ya da o
/// tipleri kümeden çıkaran değişiklik burada düşer.
/// </summary>
public class WpfTypeInitWarmupTests
{
    [Fact]
    public void Isinma_testlerden_once_kosmus_olur()
    {
        WpfTypeInitWarmup.Completed.Should().BeTrue(
            "WPF tip başlatıcıları modül başlatıcısında, ilk testten önce ve tek iş parçacığında bitmeli; " +
            "yoksa STA'sız bir test ile XAML yükleyen bir STA testi yeniden birbirini kilitleyebilir");
    }

    [Fact]
    public void Isinma_kumesi_kilitlenmeye_karisan_tipleri_kapsar()
    {
        WpfTypeInitWarmup.DependencyPropertyOwners().Should().Contain(
            [typeof(ItemsControl), typeof(ContentPresenter), typeof(ScrollViewer)]);
    }

    [Fact]
    public void Isinmada_hicbir_statik_kurucu_dusmedi()
    {
        // Düşen kurucu koşuyu durdurmaz (aşağıdaki test); görünür olduğu yer burası.
        WpfTypeInitWarmup.Failures
            .Select(f => $"{f.Type.FullName}: {f.Error.GetBaseException().Message}")
            .Should().BeEmpty(
                "ısınmada statik kurucusu düşen WPF tipi bu süreçte artık kullanılamaz; tip ve nedeni listede");
    }

    [Fact]
    public void Dusen_statik_kurucu_isinmayi_durdurmaz_hata_kaydedilir()
    {
        // Düşen tip ÖNCE: ısınma ilk hatada dursaydı ikinci tipin kurucusu hiç koşmazdı.
        var failures = WpfTypeInitWarmup.RunInitializers([typeof(KurucusuDusen), typeof(KurucusuSaglam)]);

        failures.Should().ContainSingle().Which.Type.Should().Be(typeof(KurucusuDusen));
        KurucuIzi.SaglamKostu.Should().BeTrue("düşen bir kurucu kalan tiplerin ısınmasını engellememeli");
    }

    private static class KurucuIzi
    {
        internal static bool SaglamKostu;
    }

    private static class KurucusuDusen
    {
        // Statik alan başlatıcısı tip başlatıcısının parçasıdır; bilerek düşer.
        internal static readonly bool Deger = Dus();

        private static bool Dus() => throw new InvalidOperationException("bilerek düşen statik kurucu");
    }

    private static class KurucusuSaglam
    {
        internal static readonly bool Deger = Isaretle();

        private static bool Isaretle() => KurucuIzi.SaglamKostu = true;
    }
}
