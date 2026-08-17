using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;

namespace OrderDeck.Labeling;

/// <summary>
/// Barkodlu etiketi yazıcıya gönderir. <see cref="LabelPrinter"/>'dan ayrı bir
/// sınıf: yükü farklı (müşteri/mesaj değil, ürün/varyant/barkod), ortak bir
/// arayüze zorlamak ikisini de bulandırırdı.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BarcodeLabelPrinter
{
    private readonly AppSettings _settings;
    private readonly ILogger<BarcodeLabelPrinter>? _log;

    public BarcodeLabelPrinter(AppSettings settings, ILogger<BarcodeLabelPrinter>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public void Print(IReadOnlyList<BarcodeLabelDocument.Label> labels, int copies)
    {
        using var doc = BarcodeLabelDocument.Build(
            labels, copies,
            _settings.PrinterName,
            _settings.LabelWidthMm,
            _settings.LabelHeightMm,
            _settings.LabelFontFamily);

        // Basımı log'a yaz: "etiket çıkmadı" şikâyetinde işin yazıcıya hiç
        // gidip gitmediğini ayırt etmenin tek yolu bu (LabelPrinter'da da var).
        _log?.LogInformation(
            "Barkod etiketi basılıyor: {Count} adet, yazıcı '{Printer}'.",
            labels.Count * copies,
            string.IsNullOrWhiteSpace(_settings.PrinterName)
                ? "(varsayılan)"
                : _settings.PrinterName);

        var started = DateTimeOffset.UtcNow;
        doc.Print();
        var elapsed = DateTimeOffset.UtcNow - started;
        // Yavaş basım donmanın yazıcı kaynaklı olup olmadığını sonradan
        // ayırt etmeyi sağlıyor — LabelPrinter'daki aynı eşik.
        if (elapsed > TimeSpan.FromSeconds(10))
            _log?.LogWarning(
                "Barkod etiketi basımı {Seconds:F1} sn sürdü ({Count} etiket).",
                elapsed.TotalSeconds, labels.Count * copies);
    }
}
