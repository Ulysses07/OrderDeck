using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

/// <summary>
/// Kuralların doğru olması yetmez, <b>bağlı</b> da olmaları gerekir. Saf
/// doğrulayıcı testleri (<see cref="JwtOptionsValidatorTests"/>) bir sonraki
/// birleştirmede <c>IValidateOptions</c> kaydı ya da <c>ValidateOnStart</c>
/// çağrısı düşse bile yeşil yanmaya devam ederdi — yani düzeltmenin tümüyle
/// etkisiz hâle geldiği durumu hiçbir test görmezdi. Burada gerçek host'u
/// ayağa kaldırıyoruz: geçersiz anahtarla <b>açılmamalı</b>.
/// </summary>
public class JwtStartupValidationTests
{
    /// <summary>
    /// Compose'daki <c>Jwt__SecretKey: "${JWT_SECRET}"</c> ifadesi, değişken
    /// tanımlı değilse boş string'e çözülür ve appsettings'teki değeri de ezer.
    /// Sunucunun bu yapılandırmayla açılmaması gereken hâli tam olarak bu.
    /// </summary>
    private sealed class EmptySecretApiFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["Jwt:SecretKey"] = "" };
    }

    [Fact]
    public void Bos_imzalama_anahtariyla_sunucu_acilmaz()
    {
        using var factory = new EmptySecretApiFactory();

        // İstemci oluşturmak host'u kurar ve başlatır.
        var act = () => factory.CreateClient();

        var failure = act.Should().Throw<Exception>().Which;
        FindOptionsValidationException(failure)
            .Should().NotBeNull("JWT doğrulaması ValidateOnStart ile bağlı olmalı")
            .And.Subject.As<OptionsValidationException>()
            .Failures.Should().ContainMatch("*Jwt:SecretKey*");
    }

    /// <summary>
    /// <c>ValidateOnStart</c> hatayı doğrudan da fırlatabilir, birden fazla
    /// doğrulayıcı varsa <c>AggregateException</c> içine de sarabilir; test
    /// bu ayrıntıya bağlanmasın diye zinciri tarıyoruz.
    /// </summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OptionsValidationException ove) return ove;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    var found = FindOptionsValidationException(inner);
                    if (found is not null) return found;
                }
                return null;
            }
            ex = ex.InnerException;
        }
        return null;
    }
}
