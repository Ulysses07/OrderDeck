using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>
/// Kapının tuttuğu değer, sunucunun tepe belleğindeki çarpan. Blob başına tavan
/// tek başına bir şey garanti etmiyor: eşzamanlı yükleme sayısı sınırsızsa
/// tepe bellek de sınırsız. Oran sınırı bu boşluğu kapatmıyor çünkü müşteri
/// kimliğine bölünmüş durumda — tek müşterinin sıklığını sınırlıyor, aynı anda
/// kaç müşterinin yüklediğini değil.
/// </summary>
public class BackupUploadThrottleTests
{
    private static BackupUploadThrottle Build(int permits, int waitSeconds) =>
        new(Options.Create(new BackupOptions
        {
            MaxConcurrentUploads = permits,
            UploadQueueWaitSeconds = waitSeconds
        }));

    [Fact]
    public async Task Yapilandirilan_sayidan_fazlasi_ayni_anda_giremez()
    {
        using var throttle = Build(permits: 2, waitSeconds: 0);

        (await throttle.TryEnterAsync(default)).Should().BeTrue();
        (await throttle.TryEnterAsync(default)).Should().BeTrue();
        (await throttle.TryEnterAsync(default)).Should().BeFalse(
            "üçüncü istek yapılandırılan eşzamanlılığı aşıyor");
    }

    [Fact]
    public async Task Cikan_istek_sirayi_bir_sonrakine_birakiyor()
    {
        using var throttle = Build(permits: 1, waitSeconds: 0);

        (await throttle.TryEnterAsync(default)).Should().BeTrue();
        (await throttle.TryEnterAsync(default)).Should().BeFalse();

        throttle.Exit();

        (await throttle.TryEnterAsync(default)).Should().BeTrue(
            "kapı geçici olmalı; bir kez dolduğunda kalıcı kapanmamalı");
    }

    /// <summary>
    /// Sıfır ve negatif yapılandırma yüklemeyi tamamen kilitlerdi. Sessizce
    /// kilitlemek, yedek almayı yapılandırma hatasıyla durdurmanın en sinsi
    /// yolu olurdu — hata sadece bir daha yedek gerektiğinde görülürdü.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Gecersiz_esZamanlilik_yuklemeyi_kilitlemiyor(int permits)
    {
        using var throttle = Build(permits, waitSeconds: 0);

        (await throttle.TryEnterAsync(default)).Should().BeTrue();
    }
}
