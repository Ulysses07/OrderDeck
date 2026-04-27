using System.Collections.Generic;
using LiveDeck.Core.Sales.Pipeline;

namespace LiveDeck.Core.Sales;

/// <summary>
/// Pure pipeline that turns a raw chat message + the current set of active codes into a
/// <see cref="CaptureResult"/>. Stateless and deterministic so it is safe to reuse across
/// threads and trivial to unit test.
/// </summary>
public sealed class OrderCaptureEngine
{
    private readonly MessageNormalizer _normalizer;
    private readonly CodeMatcher _matcher;
    private readonly VariantExtractor _variants;
    private readonly QuantityExtractor _quantity;
    private readonly IntentScorer _intent;
    private readonly ConfidenceScorer _confidence;

    public int HighConfidenceThreshold { get; init; } = 80;
    public int LowConfidenceThreshold { get; init; } = 50;

    public OrderCaptureEngine(
        MessageNormalizer normalizer,
        CodeMatcher matcher,
        VariantExtractor variants,
        QuantityExtractor quantity,
        IntentScorer intent,
        ConfidenceScorer confidence)
    {
        _normalizer = normalizer;
        _matcher = matcher;
        _variants = variants;
        _quantity = quantity;
        _intent = intent;
        _confidence = confidence;
    }

    public CaptureResult Capture(string originalMessage, IEnumerable<ActiveCode> activeCodes)
    {
        var normalised = _normalizer.Normalize(originalMessage);
        if (string.IsNullOrEmpty(normalised))
            return new CaptureResult(false, null, null, 0, 0, 0, "empty after normalisation");

        var matched = _matcher.Match(normalised, activeCodes);
        var size = matched is null ? null : _variants.Extract(normalised, matched.Sizes);
        var qty = _quantity.Extract(normalised);
        var intent = _intent.Score(normalised, originalMessage);
        var confidence = _confidence.Score(matched, size, qty, intent);

        var isCapture = matched is not null && confidence >= HighConfidenceThreshold;
        var reason = matched is null
            ? "no active code matched"
            : (confidence < LowConfidenceThreshold ? "low confidence (rejected)"
                : confidence < HighConfidenceThreshold ? "needs operator approval"
                : "auto-captured");

        return new CaptureResult(isCapture, matched, size, qty, intent, confidence, reason);
    }
}
