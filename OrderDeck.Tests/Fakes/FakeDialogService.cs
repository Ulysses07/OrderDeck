using System;
using System.Collections.Generic;
using OrderDeck.App.Services;

namespace OrderDeck.Tests.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public List<string> PhoneEntryShownFor { get; } = new();
    public List<string> ErrorsShown { get; } = new();
    public List<string> InfosShown { get; } = new();
    public Func<string, bool> PhoneEntryResult { get; set; } = _ => false;

    /// <summary>Başlıklı bildirimler: (başlık, mesaj, önem).</summary>
    public List<(string Title, string Message, DialogSeverity Severity)> Shown { get; } = new();

    /// <summary>Sorulan onaylar: (başlık, mesaj).</summary>
    public List<(string Title, string Message)> Confirmations { get; } = new();

    /// <summary>Onayın cevabı. Varsayılan HAYIR: bir test farkında olmadan
    /// yıkıcı bir dala (yayını bitir, kuyruğu temizle, ban) girmesin.</summary>
    public Func<string, bool> ConfirmResult { get; set; } = _ => false;

    public bool ShowPhoneEntryDialog(string customerId)
    {
        PhoneEntryShownFor.Add(customerId);
        return PhoneEntryResult(customerId);
    }

    public void ShowError(string message) => ErrorsShown.Add(message);

    public void ShowInfo(string message) => InfosShown.Add(message);

    public void Show(string message, string title, DialogSeverity severity = DialogSeverity.Info)
        => Shown.Add((title, message, severity));

    public bool Confirm(string message, string title)
    {
        Confirmations.Add((title, message));
        return ConfirmResult(title);
    }
}
