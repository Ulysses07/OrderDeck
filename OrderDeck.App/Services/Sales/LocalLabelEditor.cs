using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Services.Sales;

/// <summary>
/// Yalnız yerel düzenleyici: bugünkü <see cref="LabelService"/> çağrıları, kip okunmadan. Düzenleyici verilmeyen
/// ViewModel'ler (testlerin çoğu) bunu kullanır; uygulamada DI <see cref="SalesEditRouter"/>'ı verir. Davranış
/// <see cref="SalesEditRouter"/>'ın mod Off yoluyla aynı.
/// </summary>
public sealed class LocalLabelEditor(LabelService labels) : ILabelEditor
{
    public IReadOnlyList<SalesEditDecision> Route(IReadOnlyCollection<string> labelIds) =>
        labelIds.Select(id => new SalesEditDecision(id, null, SalesEditRoute.Local, null)).ToList();

    public string? ServerUnavailableReason => null;

    public string? EditBlockedReason(IReadOnlyCollection<string> labelIds) => null;

    public Task<SalesEditResult> CancelAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default)
    {
        if (labelIds.Count == 0) return Task.FromResult(SalesEditResult.Empty);
        labels.Cancel(labelIds, reason);
        return Task.FromResult(SalesEditResult.AllLocal(labelIds));
    }

    public Task<SalesEditResult> CancelQueueAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default) =>
        CancelAsync(labelIds, reason, ct);

    public Task<SalesPrintMarkResult> MarkPrintedAsync(IReadOnlyList<string> paperOrder, CancellationToken ct = default)
    {
        if (paperOrder.Count == 0) return Task.FromResult(SalesPrintMarkResult.Empty);
        labels.MarkPrintedAndRecord(paperOrder);
        return Task.FromResult(SalesPrintMarkResult.AllLocal(paperOrder));
    }

    public Task<SalesEditResult> UncancelAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default)
    {
        if (labelIds.Count == 0) return Task.FromResult(SalesEditResult.Empty);
        labels.Uncancel(labelIds);
        return Task.FromResult(SalesEditResult.AllLocal(labelIds));
    }

    public Task<SalesEditResult> ConfirmBackupAsync(string backupLabelId, decimal? newPrice, CancellationToken ct = default)
    {
        labels.ConfirmBackup(backupLabelId, newPrice);
        return Task.FromResult(SalesEditResult.AllLocal([backupLabelId]));
    }

    public Task<SalesReprintPlan> PrepareReprintAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default) =>
        Task.FromResult(SalesReprintPlan.From(labels.PrepareReprint(labelIds)));

    public Task<SalesCodePriceResult> CorrectCodePricesAsync(
        SalesCodePriceRequest request,
        Func<IReadOnlyList<string>, PriceCorrectionResult> writeLocal,
        CancellationToken ct = default) =>
        Task.FromResult(SalesCodePriceResult.AllLocal(writeLocal(request.LabelIds)));

    public Task<SalesBackupResult> AddBackupAsync(
        SalesBackupRequest request, SalesCommandIntent? resend = null, CancellationToken ct = default) =>
        Task.FromResult(SalesBackupResult.Added(labels.AddBackup(
            request.ParentLabelId, request.Platform, request.Username, request.DisplayName, request.MessageText)));
}
