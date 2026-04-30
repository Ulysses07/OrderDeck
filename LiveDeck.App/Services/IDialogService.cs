namespace LiveDeck.App.Services;

public interface IDialogService
{
    bool ShowPhoneEntryDialog(string customerId);
    void ShowError(string message);
}
