namespace OrderDeck.Licensing.Trial;

/// <summary>
/// Discriminated union returned by <see cref="TrialService.GetState"/>.
/// </summary>
public abstract record TrialState
{
    public sealed record NoTrial : TrialState
    {
        public static readonly NoTrial Instance = new();
        private NoTrial() { }
    }

    public sealed record Active(int RemainingDays, DateTimeOffset ExpiresAt) : TrialState;

    public sealed record Expired(DateTimeOffset ExpiredAt) : TrialState;
}
