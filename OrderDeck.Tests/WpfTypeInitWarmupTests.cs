using System.Windows.Controls;
using FluentAssertions;

namespace OrderDeck.Tests;

/// <summary>
/// <see cref="WpfTypeInitWarmup"/>'ın korumasının koruması. Kilitlenmenin kendisi süreç başına bir
/// kez çalışan tip başlatıcılarına bağlı olduğu için süreç içi bir testle yeniden üretilemez; burada
/// sabitlenen, onu imkânsız kılan düzeneğin yerinde durduğu: ısınma testlerden önce koşmuş olmalı ve
/// olaya karışan tipleri kapsamalı. Özniteliği silen ya da kümeyi daraltan değişiklik burada düşer.
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
}
