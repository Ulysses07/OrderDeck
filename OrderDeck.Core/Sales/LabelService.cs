using System;
using System.Collections.Generic;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.Core.Sales;

public sealed class LabelService
{
    private readonly LabelRepository _labels;
    private readonly CustomerService _customers;
    private readonly IClock _clock;

    public LabelService(
        LabelRepository labels,
        CustomerService customers,
        IClock clock)
    {
        _labels = labels;
        _customers = customers;
        _clock = clock;
    }

    /// <summary>
    /// Queues a new (unprinted) label for the given chat message + price snapshot.
    /// Auto-creates the Customer on first encounter.
    /// <paramref name="isBackupPromoted"/> drives the small "Y" stamp on the
    /// printed sticker.
    /// </summary>
    public Label Add(string sessionId, ChatMessage message, decimal price, string? code,
        bool isBackupPromoted = false,
        string? parentLabelId = null,
        bool isTentativeBackup = false)
    {
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        var label = new Label(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            CustomerId: customer.Id,
            Platform: message.Platform,
            Username: message.Username,
            MessageText: message.Text,
            Code: code,
            Price: price,
            AddedAt: _clock.UnixNow(),
            PrintedAt: null,
            IsBackupPromoted: isBackupPromoted,
            ParentLabelId: parentLabelId,
            IsTentativeBackup: isTentativeBackup,
            DisplayName: message.DisplayName);

        _labels.Insert(label);
        return label;
    }

    public void Delete(string labelId) => _labels.Delete(labelId);

    public IReadOnlyList<Label> GetQueue(string sessionId) =>
        _labels.GetUnprintedBySession(sessionId);

    /// <summary>Soft-cancel: marks labels as cancelled with a reason code (one of the
    /// preset codes from CancelReasonCodes). Already-cancelled labels are silently
    /// ignored. Reverses the customer's printed-label aggregates so revenue stays
    /// correct on the Customer record.
    ///
    /// Tentative backup labels never contributed to aggregates while tentative,
    /// so they're skipped in the negative-delta loop too — preventing a phantom
    /// debit when a tentative backup is cancelled.</summary>
    public void Cancel(IReadOnlyList<string> labelIds, string reason)
    {
        if (labelIds.Count == 0) return;

        var groupedAmounts = new Dictionary<string, (int Count, decimal Amount)>();
        var idsToFlip = new List<string>(labelIds.Count);
        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null || lbl.CancelledAt.HasValue) continue;
            idsToFlip.Add(id);
            // Only printed AND non-tentative rows touched the aggregates.
            if (lbl.PrintedAt.HasValue && !lbl.IsTentativeBackup)
            {
                if (groupedAmounts.TryGetValue(lbl.CustomerId, out var agg))
                    groupedAmounts[lbl.CustomerId] = (agg.Count + 1, agg.Amount + lbl.Price);
                else
                    groupedAmounts[lbl.CustomerId] = (1, lbl.Price);
            }
        }
        if (idsToFlip.Count == 0) return;

        _labels.MarkCancelled(idsToFlip, _clock.UnixNow(), reason);

        // Compensating action: if the customer-aggregate updates throw (e.g.
        // SQLite locked, FK violation), the label rows are already flipped to
        // cancelled. Without compensation the customer's TotalLabelsPrinted /
        // TotalAmount would diverge from the Label table. Better to re-flip
        // the labels and surface a loud error than carry the divergence.
        // True transaction-scoped atomicity needs an IDbTransaction overload
        // pair on both repositories — tracked separately as data-layer
        // refactor (out of scope for this hotfix).
        var customersUpdated = new List<(string CustomerId, int Count, decimal Amount)>(groupedAmounts.Count);
        try
        {
            foreach (var (customerId, agg) in groupedAmounts)
            {
                _customers.RecordPrintedLabels(customerId, -agg.Count, -agg.Amount);
                customersUpdated.Add((customerId, agg.Count, agg.Amount));
            }
        }
        catch
        {
            // Roll back the customer aggregates we already adjusted, then
            // un-flip the label rows. Swallow rollback failures — the outer
            // throw is what the caller cares about.
            foreach (var (customerId, count, amount) in customersUpdated)
                try { _customers.RecordPrintedLabels(customerId, count, amount); } catch { }
            try { _labels.Uncancel(idsToFlip); } catch { }
            throw;
        }
    }

    /// <summary>Reverses Cancel — re-applies the printed-label aggregates so
    /// the customer's revenue total reflects the un-cancelled rows again.</summary>
    public void Uncancel(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;

        var groupedAmounts = new Dictionary<string, (int Count, decimal Amount)>();
        var idsToFlip = new List<string>(labelIds.Count);
        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null || !lbl.CancelledAt.HasValue) continue;
            idsToFlip.Add(id);
            // Mirror Cancel's tentative skip — tentative rows had no aggregate
            // to undo, so they get nothing on the way back either.
            if (lbl.PrintedAt.HasValue && !lbl.IsTentativeBackup)
            {
                if (groupedAmounts.TryGetValue(lbl.CustomerId, out var agg))
                    groupedAmounts[lbl.CustomerId] = (agg.Count + 1, agg.Amount + lbl.Price);
                else
                    groupedAmounts[lbl.CustomerId] = (1, lbl.Price);
            }
        }
        if (idsToFlip.Count == 0) return;

        _labels.Uncancel(idsToFlip);

        foreach (var (customerId, agg) in groupedAmounts)
            _customers.RecordPrintedLabels(customerId, agg.Count, agg.Amount);
    }

    /// <summary>
    /// Marks the given labels as printed and increments customer aggregates per-customer.
    /// Tentative backup labels still get PrintedAt set (they physically come out of
    /// the printer) but DO NOT contribute to customer aggregates — the sale isn't
    /// real until the operator confirms the backup via <see cref="ConfirmBackup"/>.
    /// </summary>
    public void MarkPrintedAndRecord(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;

        var groupedAmounts = new Dictionary<string, (int Count, decimal Amount)>();

        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null) continue;
            if (lbl.IsTentativeBackup) continue; // tentative rows: print only, no aggregate
            if (groupedAmounts.TryGetValue(lbl.CustomerId, out var agg))
                groupedAmounts[lbl.CustomerId] = (agg.Count + 1, agg.Amount + lbl.Price);
            else
                groupedAmounts[lbl.CustomerId] = (1, lbl.Price);
        }

        _labels.MarkPrinted(labelIds, _clock.UnixNow());

        // Same compensation strategy as Cancel — see comment there. If a
        // customer aggregate update fails after the labels are already
        // marked printed, undo the aggregate updates we landed and call
        // it a hard failure rather than leave the totals diverged.
        var customersUpdated = new List<(string CustomerId, int Count, decimal Amount)>(groupedAmounts.Count);
        try
        {
            foreach (var (customerId, agg) in groupedAmounts)
            {
                _customers.RecordPrintedLabels(customerId, agg.Count, agg.Amount);
                customersUpdated.Add((customerId, agg.Count, agg.Amount));
            }
        }
        catch
        {
            foreach (var (customerId, count, amount) in customersUpdated)
                try { _customers.RecordPrintedLabels(customerId, -count, -amount); } catch { }
            // No public Unmark-printed primitive — accept the divergence
            // here and rely on the throw to alert the operator. The
            // alternative would be to add LabelRepository.UnmarkPrinted
            // which is more surface area than this hotfix wants. Tracked.
            throw;
        }
    }

    // ─── Backup buyers ──────────────────────────────────────────────────────
    // Backups are first-class Labels (rev 2 — see migration 011) with
    // ParentLabelId pointing at the original sale and IsTentativeBackup = 1.
    // They're physically printed during the live (Y stamp from
    // IsBackupPromoted) but kept out of revenue + customer aggregates until
    // the operator confirms one via ConfirmBackup.

    /// <summary>
    /// Creates a new tentative-backup label for the given parent. The new
    /// label inherits the parent's price + code by default and goes straight
    /// into the print queue (PrintedAt = null) — the operator clicks Yazdır
    /// to physically produce the spare sticker.
    /// </summary>
    public Label AddBackup(
        string parentLabelId, string platform, string username,
        string displayName, string? messageText,
        decimal? priceOverride = null, string? codeOverride = null)
    {
        var parent = _labels.GetById(parentLabelId)
            ?? throw new InvalidOperationException(
                $"Cannot add backup: parent label {parentLabelId} not found.");

        var msg = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: platform,
            ExternalId: null,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: null,
            Text: messageText ?? string.Empty,
            ReceivedAt: _clock.UnixNow(),
            Badges: System.Array.Empty<string>());

        return Add(
            sessionId: parent.SessionId,
            message: msg,
            price: priceOverride ?? parent.Price,
            code: codeOverride ?? parent.Code,
            isBackupPromoted: true,
            parentLabelId: parent.Id,
            isTentativeBackup: true);
    }

    /// <summary>Returns active (non-cancelled) tentative backups for a parent.
    /// Confirmed backups are excluded — they're already real labels visible in
    /// the normal queue/history.</summary>
    public IReadOnlyList<Label> GetBackups(string parentLabelId) =>
        _labels.GetTentativeBackupsByParent(parentLabelId);

    public IReadOnlyDictionary<string, int> GetBackupCounts(IEnumerable<string> parentLabelIds) =>
        _labels.GetTentativeBackupCounts(parentLabelIds);

    /// <summary>Hard-deletes a tentative backup label (operator made a wrong
    /// pick during the live and wants to undo). Confirmed backups should be
    /// soft-cancelled via <see cref="Cancel"/> instead — they're real sales.</summary>
    public void RemoveBackup(string backupLabelId)
    {
        var lbl = _labels.GetById(backupLabelId);
        if (lbl is null || !lbl.IsTentativeBackup) return;
        _labels.Delete(backupLabelId);
    }

    /// <summary>
    /// Confirms a tentative backup as a real sale: flips IsTentativeBackup→0,
    /// then retroactively credits the customer's aggregates if the spare
    /// sticker was already printed (which is the common path — operator
    /// printed during the live, original cancelled next-day, now confirming).
    /// Returns the updated Label.
    /// </summary>
    public Label ConfirmBackup(string backupLabelId, decimal? newPrice = null)
    {
        var lbl = _labels.GetById(backupLabelId)
            ?? throw new InvalidOperationException(
                $"Cannot confirm backup: label {backupLabelId} not found.");

        if (!lbl.IsTentativeBackup)
            return lbl; // already confirmed — caller can ignore.

        // Optional price update: operator may negotiate a different number
        // when promoting (e.g. discount because the spare buyer hesitated).
        if (newPrice.HasValue && newPrice.Value != lbl.Price)
        {
            _labels.UpdatePrice(backupLabelId, newPrice.Value);
            lbl = lbl with { Price = newPrice.Value };
        }

        _labels.ConfirmTentativeBackups(new[] { backupLabelId });

        // Retroactive aggregate credit if the sticker was already printed.
        if (lbl.PrintedAt.HasValue)
        {
            _customers.RecordPrintedLabels(lbl.CustomerId, 1, lbl.Price);
        }

        return lbl with { IsTentativeBackup = false };
    }
}
