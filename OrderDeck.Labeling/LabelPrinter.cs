using System.Collections.Generic;
using System.Runtime.Versioning;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Labeling;

/// <summary>
/// Sends a batch of labels to the configured printer via Windows printing subsystem.
/// Printer-independent — works with any Windows-driver-backed printer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LabelPrinter : ILabelPrinter
{
    private readonly AppSettings _settings;
    private readonly ILogger<LabelPrinter> _log;

    public LabelPrinter(AppSettings settings, ILogger<LabelPrinter>? log = null)
    {
        _settings = settings;
        _log = log ?? NullLogger<LabelPrinter>.Instance;
    }

    /// <summary>
    /// Prints the given labels in order. No-op if no labels.
    /// Kargo PR F: <paramref name="recipientPaysLabelIds"/> set'inde olan
    /// label'lar etikette "ALICI ÖDEMELİ" kırmızı yazı + kargo ücreti
    /// (settings.Shipping.ShippingFee) render eder.
    /// </summary>
    public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null)
    {
        if (labels.Count == 0)
        {
            _log.LogInformation("Print called with empty label batch — no-op");
            return;
        }

        using var doc = LabelPrintDocument.Build(labels, _settings, _settings.PrinterName,
            recipientPaysLabelIds);
        _log.LogInformation(
            "Printing {Count} label(s) on '{Printer}' (RecipientPays marks: {MarkCount})",
            labels.Count, doc.PrinterSettings.PrinterName,
            recipientPaysLabelIds?.Count ?? 0);

        // UI freeze fix (2026-05-13): süre ölç. Print 10 sn+ sürerse yazıcı
        // problem yaşıyor olabilir — log'a WARN bırak ki ileri analizde
        // donmanın printer kaynaklı mı olduğu görülür.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        doc.Print();
        sw.Stop();
        if (sw.Elapsed.TotalSeconds >= 10)
            _log.LogWarning(
                "Print slow: {Count} label(s) took {Elapsed:0.0}s on '{Printer}'",
                labels.Count, sw.Elapsed.TotalSeconds, doc.PrinterSettings.PrinterName);
        else
            _log.LogDebug(
                "Print done: {Count} label(s) in {Elapsed:0.00}s",
                labels.Count, sw.Elapsed.TotalSeconds);
    }
}
