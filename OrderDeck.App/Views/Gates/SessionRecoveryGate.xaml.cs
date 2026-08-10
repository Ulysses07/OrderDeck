using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Startup;
using OrderDeck.Core.Sessions;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// Yarım kalmış yayın oturumu için üç yollu karar ekranı.
///
/// <see cref="AppGate.Close(bool)"/> ikili olduğu için sonuç burada
/// tutuluyor; akış gate kapandıktan sonra <see cref="Choice"/>'ı okuyor.
/// </summary>
public partial class SessionRecoveryGate : UserControl
{
    private readonly AppGate _gate;

    /// <summary>Operatörün kararı. Seçim yapılmadan kapanırsa
    /// <see cref="SessionRecoveryChoice.Exit"/> kalır — yarım bir shell'e
    /// düşmektense açılmamak daha güvenli.</summary>
    public SessionRecoveryChoice Choice { get; private set; } = SessionRecoveryChoice.Exit;

    private SessionRecoveryGate(AppGate gate, StreamSession? session)
    {
        InitializeComponent();
        _gate = gate;

        SessionTitle.Text = string.IsNullOrWhiteSpace(session?.Title)
            ? "Adsız yayın"
            : session!.Title;

        // BİLİNÇLİ DEĞİŞİKLİK: App.xaml.cs'teki MessageBox "dd MMM HH:mm"
        // (kısaltılmış ay) kullanıyordu, çünkü metin başlık satırına
        // sıkışıyordu. Gate'te sütun genişliği var, tam ay adı okunuyor.
        SessionStarted.Text = session is null
            ? "—"
            : "Başlangıç: " + DateTimeOffset
                .FromUnixTimeSeconds(session.StartedAt)
                .LocalDateTime
                .ToString("dd MMMM HH:mm", new CultureInfo("tr-TR"));
    }

    /// <summary>session null geçilebiliyor: GateCompositionTests ekranı
    /// hiçbir servis kurmadan render ediyor.</summary>
    public static SessionRecoveryGate Create(AppGate gate, StreamSession? session)
        => new(gate, session);

    private void OnContinue(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.Continue);

    private void OnEndSession(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.EndSession);

    private void OnExit(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.Exit);

    private void Decide(SessionRecoveryChoice choice)
    {
        Choice = choice;
        _gate.Close(choice != SessionRecoveryChoice.Exit);
    }
}
