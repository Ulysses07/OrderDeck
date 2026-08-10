using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Formatting;
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
    /// düşmektense açılmamak daha güvenli.
    ///
    /// <see cref="AppGate.Close(bool)"/>'a verilen bool yalnız "Exit mi
    /// değil mi" ayrımını taşır; kararın TEK KAYNAĞI bu özelliktir — çağıran
    /// taraf bool'a değil <see cref="Choice"/>'a bakmalı.</summary>
    public SessionRecoveryChoice Choice { get; private set; } = SessionRecoveryChoice.Exit;

    private SessionRecoveryGate(AppGate gate, StreamSession? session)
    {
        InitializeComponent();
        _gate = gate;

        SessionTitle.Text = string.IsNullOrWhiteSpace(session?.Title)
            ? "Adsız yayın"
            : session!.Title;

        // BİLİNÇLİ DEĞİŞİKLİK: App.xaml.cs'teki MessageBox "dd MMM HH:mm"
        // yazıyordu — yılsız. Aylar önce çökmüş bir oturumda "10 Ağustos
        // 23:00" belirsiz; TrFormats.DateTimeLong ("d MMMM yyyy HH:mm") yılı
        // da veriyor ve paylaşılan tr-TR kültürünü kullanıyor.
        SessionStarted.Text = session is null
            ? "—"
            : "Başlangıç: " + TrFormats.DateTimeLong(session.StartedAt);
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
        // AppGate.Close idempotent ama Choice değil: kapandıktan sonra gelen
        // ikinci bir tıklama Choice'u değiştirir, gate'in sonucu ise ilk
        // kararda donmuş olur — ikisi ayrışırdı. Kapıyı burada da kapatıyoruz.
        if (_gate.Completion.IsCompleted) return;

        Choice = choice;
        _gate.Close(choice != SessionRecoveryChoice.Exit);
    }
}
