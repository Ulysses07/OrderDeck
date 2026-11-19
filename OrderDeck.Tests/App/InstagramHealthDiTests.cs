using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderDeck.App.ViewModels;
using OrderDeck.Chat.Ingestors.Instagram;

namespace OrderDeck.Tests.App;

/// <summary>
/// Instagram sağlık izleyicisinin DI kaydı (spec 2026-11-16 §8.6.3): kabuğun kurucu parametresi isteğe bağlıdır,
/// kayıt eksikse sessizce null düşer ve şerit, üst nokta ve kenar çubuğu boş kalır. Kayıt tekil değil de iki ayrı
/// örnek üretirse sonuç yine boş şerittir: hosted service birine yazar, kabuk hiç yazılmayan öbürünü okur. AppHost
/// geçici kökte kurulur (TestAssemblyInit).
/// </summary>
public sealed class InstagramHealthDiTests
{
    [Fact]                                                              // eksik kayıtta isteğe bağlı parametre sessizce null düşer: kabuk ve hosted service AYNI örneği almalı
    public void Izleyici_tekil_hosted_service_ayni_ornegi_tutar_kabuk_istege_bagli_parametreyle_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var monitor = host.Services.GetRequiredService<InstagramHealthMonitor>();
        var hosted = host.Services.GetServices<IHostedService>().OfType<InstagramChatHostedService>().Single();
        host.Services.GetRequiredService<InstagramHealthMonitor>().Should().BeSameAs(monitor);
        hosted.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(InstagramHealthMonitor)).Select(f => f.GetValue(hosted))
            .Should().ContainSingle().Which.Should().BeSameAs(monitor, "şerit, hosted service'in yazdığı AYNI izleyiciyi okumalı");
        typeof(MainShellViewModel).GetConstructors().Single().GetParameters()
            .Should().ContainSingle(p => p.IsOptional && p.ParameterType == typeof(InstagramHealthMonitor));
    }
}
