using System;
using System.Collections.Generic;
using LiveDeck.App.Services;

namespace LiveDeck.Tests.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public List<string> PhoneEntryShownFor { get; } = new();
    public List<string> ErrorsShown { get; } = new();
    public Func<string, bool> PhoneEntryResult { get; set; } = _ => false;

    public bool ShowPhoneEntryDialog(string customerId)
    {
        PhoneEntryShownFor.Add(customerId);
        return PhoneEntryResult(customerId);
    }

    public void ShowError(string message) => ErrorsShown.Add(message);
}
