using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Chat;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// UI-side wrapper around <see cref="ChatMessage"/> that adds <see cref="IsSenderBlacklisted"/>
/// for red-highlight binding in the live chat panel.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private bool _isSenderBlacklisted;

    public string Platform => Message.Platform;
    public string Username => Message.Username;
    public string Text     => Message.Text;
    public string Display  => Message.DisplayName ?? Message.Username;

    public ChatMessageViewModel(ChatMessage message, bool isSenderBlacklisted)
    {
        Message = message;
        IsSenderBlacklisted = isSenderBlacklisted;
    }
}
