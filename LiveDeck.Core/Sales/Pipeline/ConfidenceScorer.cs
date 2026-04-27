namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Combines pipeline signals into a single 0-100 confidence score.
///   * No matched code → 0
///   * Otherwise: 0.625 × intent + 30 if a size matched (or 10 if no size)
/// Output bounded to [0, 100].
/// </summary>
public sealed class ConfidenceScorer
{
    public int Score(ActiveCode? matched, string? size, int quantity, int intentScore)
    {
        if (matched is null) return 0;

        int sizeBoost = size is null ? 10 : 30;
        int raw = (int)(intentScore * 0.625) + sizeBoost;

        return System.Math.Clamp(raw, 0, 100);
    }
}
