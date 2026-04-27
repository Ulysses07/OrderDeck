using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using LiveDeck.Core.Chat;

namespace LiveDeck.App.ViewModels;

public sealed class ChatPanelViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private const int MaxMessages = 200;
    private readonly IDisposable _sub;
    private readonly Dispatcher _dispatcher;

    public ChatPanelViewModel(IChatBus bus)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sub = bus.Subscribe(OnMessage);
    }

    private void OnMessage(ChatMessage m)
    {
        // Marshal to UI thread (chat ingestors run on background threads)
        _dispatcher.BeginInvoke(() =>
        {
            Messages.Add(m);
            while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
        });
    }

    public void Dispose() => _sub.Dispose();
}
