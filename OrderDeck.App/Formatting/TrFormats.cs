using System;
using System.Globalization;

namespace LiveDeck.App.Formatting;

/// <summary>
/// Application-wide tr-TR formatting helpers. Keep WPF Bindings culture-agnostic but
/// guarantee tr-TR rendering across machines regardless of system locale.
/// </summary>
public static class TrFormats
{
    /// <summary>Shared tr-TR culture instance — use for all explicit format calls.</summary>
    public static readonly CultureInfo TR = new("tr-TR");

    /// <summary>"100,50 TL" — fixed-grouping currency text.</summary>
    public static string Currency(decimal value) => value.ToString("N2", TR) + " TL";

    /// <summary>"28 Nis 2026 14:30" — short Turkish date.</summary>
    public static string DateTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMM yyyy HH:mm", TR);

    /// <summary>"28 Nisan 2026 14:30" — long Turkish date.</summary>
    public static string DateTimeLong(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMMM yyyy HH:mm", TR);
}
