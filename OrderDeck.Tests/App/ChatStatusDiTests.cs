using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderDeck.App.ViewModels;
using OrderDeck.Chat.Bridge;
using OrderDeck.Chat.Ingestors.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Chat.Ingestors.YouTube;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;

namespace OrderDeck.Tests.App;

/// <summary>
/// "Sohbeti yenile" sinyalinin ve sohbet kaynağı durum izleyicisinin DI kaydı (tasarım: Sohbeti yenile §5.2, §5.4).
///
/// NEDEN: bu iki nesne servislerde ve kabukta İSTEĞE BAĞLI parametre; kayıt eksikse sessizce null düşer. Sinyal
/// tekil değilse düğme kimsenin dinlemediği bir örneğe basar; izleyici tekil değilse servisler birine yazar, şerit
/// hiç yazılmayan öbürünü okur. İkisinde de ekranda hata yoktur: düğme "çalışır", şerit boş kalır. AppHost geçici
/// kökte kurulur (TestAssemblyInit).
/// </summary>
public sealed class ChatStatusDiTests
{
    /// <summary>Nesnenin, verilen tipteki tek örnek alanının değeri.</summary>
    private static object? Field<T>(object holder) => holder.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Where(f => f.FieldType == typeof(T))
        .Select(f => f.GetValue(holder))
        .Should().ContainSingle($"{holder.GetType().Name} bir {typeof(T).Name} tutmalı").Subject;

    [Fact]
    public void Shell_bridge_and_services_share_one_signal_and_one_monitor()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var signal = host.Services.GetRequiredService<ChatReconnectSignal>();
        var monitor = host.Services.GetRequiredService<ChatSourceMonitor>();
        host.Services.GetRequiredService<ChatReconnectSignal>().Should().BeSameAs(signal);
        host.Services.GetRequiredService<ChatSourceMonitor>().Should().BeSameAs(monitor);

        var hosted = host.Services.GetServices<IHostedService>().ToArray();
        object[] writers =
        [
            host.Services.GetRequiredService<ExtensionBridgeServer>(),
            hosted.OfType<YouTubeOfficialChatHostedService>().Single(),
            hosted.OfType<FacebookChatHostedService>().Single(),
        ];
        foreach (var writer in writers)
        {
            Field<ChatReconnectSignal>(writer).Should().BeSameAs(signal, "düğmenin bastığı sinyali dinlemeli");
            Field<ChatSourceMonitor>(writer).Should().BeSameAs(monitor, "şeridin okuduğu izleyiciye yazmalı");
        }

        // Instagram'ın kendi (hesap başına) izleyicisi var; ortak izleyiciye yazmaz, yalnız sinyali dinler.
        Field<ChatReconnectSignal>(hosted.OfType<InstagramChatHostedService>().Single()).Should().BeSameAs(signal);

        // Kabuk ikisini de isteğe bağlı parametreyle alır: kayıtlı oldukları için dolu gelir.
        var parameters = typeof(MainShellViewModel).GetConstructors().Single().GetParameters();
        parameters.Should().ContainSingle(p => p.IsOptional && p.ParameterType == typeof(ChatSourceMonitor));
        parameters.Should().ContainSingle(p => p.IsOptional && p.ParameterType == typeof(ChatReconnectSignal));
    }

    [Fact]
    public void Session_start_resets_session_liveness_in_the_monitor()
    {
        // "Bu oturumda bağlıydı" bilgisi yeni oturuma taşınırsa, dün gece yayın yapılan platform bu gece hiç
        // açılmadığı hâlde şeritte "yayın kesildi" diye sararır.
        using var host = new global::OrderDeck.App.AppHost();
        var monitor = host.Services.GetRequiredService<ChatSourceMonitor>();
        monitor.Report("youtube", ChatSourceState.Live);
        monitor.Report("youtube", ChatSourceState.NoBroadcast);
        monitor.Snapshot().Single().WasLiveThisSession.Should().BeTrue("ön koşul");

        // Oturum servisinin olayı doğrudan yükseltilir: gerçek oturum açmak paylaşılan test veritabanına yazardı.
        var sessions = host.Services.GetRequiredService<StreamSessionService>();
        var started = (EventHandler<SessionStartedEventArgs>?)typeof(StreamSessionService)
            .GetField(nameof(StreamSessionService.SessionStarted), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sessions);
        started.Should().NotBeNull("izleyici oturum başlangıcını dinlemeli");
        started!.Invoke(sessions, new SessionStartedEventArgs(Guid.NewGuid().ToString("N"), 0));

        monitor.Snapshot().Single().WasLiveThisSession.Should().BeFalse();
    }
}
