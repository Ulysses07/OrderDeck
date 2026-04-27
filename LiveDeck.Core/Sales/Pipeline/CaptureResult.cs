namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Outcome of a single message running through the OrderCaptureEngine pipeline.
/// </summary>
public sealed record CaptureResult(
    bool IsCapture,
    ActiveCode? MatchedCode,
    string? Size,
    int Quantity,
    int IntentScore,
    int Confidence,
    string Reason);
