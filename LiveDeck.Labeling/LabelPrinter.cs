using System.Collections.Generic;
using System.Runtime.Versioning;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Labeling;

/// <summary>
/// Sends a batch of labels to the configured printer via Windows printing subsystem.
/// Printer-independent — works with any Windows-driver-backed printer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LabelPrinter
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
    /// </summary>
    public void Print(IReadOnlyList<Label> labels)
    {
        if (labels.Count == 0)
        {
            _log.LogInformation("Print called with empty label batch — no-op");
            return;
        }

        using var doc = LabelPrintDocument.Build(labels, _settings, _settings.PrinterName);
        _log.LogInformation("Printing {Count} label(s) on '{Printer}'",
            labels.Count, doc.PrinterSettings.PrinterName);

        doc.Print();
    }
}
