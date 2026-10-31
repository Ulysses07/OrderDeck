using System.Diagnostics.Metrics;

namespace OrderDeck.LicenseServer.Services.Observability;

/// <summary>
/// Custom OrderDeck domain metrics emitted via OpenTelemetry.
///
/// AspNetCore + Http + Runtime instrumentations cover most of what we want
/// (request latency histograms, GC, threadpool, outbound HTTP), but the
/// product-specific signals — license activations, backups uploaded, email
/// sends — need to be emitted by us. This Meter is registered with the OTel
/// MeterProvider in Program.cs so all counters/histograms here flow into
/// /metrics + the OTLP exporter automatically.
///
/// Naming follows OpenTelemetry semantic conventions: lower_snake_case,
/// noun-first, units in the suffix where ambiguous (orderdeck.email.send.duration_ms).
/// </summary>
public sealed class OrderDeckMetrics
{
    public const string MeterName = "OrderDeck.LicenseServer";

    private readonly Meter _meter;

    public OrderDeckMetrics()
    {
        _meter = new Meter(MeterName, version: "1.0");

        LicensesActivated = _meter.CreateCounter<long>(
            "orderdeck.license.activations_total",
            description: "Total successful license activations.");
        LicenseActivationFailures = _meter.CreateCounter<long>(
            "orderdeck.license.activation_failures_total",
            description: "Activation attempts that ended in ActivationException, tagged by code.");
        BackupsUploaded = _meter.CreateCounter<long>(
            "orderdeck.backup.uploads_total",
            description: "Encrypted backup blobs accepted from clients.");
        BackupUploadBytes = _meter.CreateHistogram<long>(
            "orderdeck.backup.upload_size_bytes",
            unit: "By",
            description: "Size of accepted backup uploads (encrypted envelope length).");
        EmailsSent = _meter.CreateCounter<long>(
            "orderdeck.email.sends_total",
            description: "Outbound emails by template + outcome (success/transient/permanent).");
        RefreshTokensRotated = _meter.CreateCounter<long>(
            "orderdeck.auth.refresh_rotations_total",
            description: "Refresh-token rotations performed via /auth/refresh.");
        RefreshTokenRotationFailures = _meter.CreateCounter<long>(
            "orderdeck.auth.refresh_failures_total",
            description: "Refresh-token rotations that returned 401 (invalid/expired/revoked).");
        LicenseTelemetryTouchFailures = _meter.CreateCounter<long>(
            "orderdeck.license.telemetry_touch_failures_total",
            description: "LastSeen/AppVersion touches from /validate that failed and were swallowed (R9-OPS03).");
        BalanceCustomerNotSynced = _meter.CreateCounter<long>(
            "orderdeck.balance.customer_not_synced_total",
            description: "Person-pool balance requests rejected with 409 customer-not-synced, tagged by endpoint.");
        BalanceClientUpdateRequired = _meter.CreateCounter<long>(
            "orderdeck.balance.client_update_required_total",
            description: "Balance requests rejected with 409 client-update-required, tagged by gate (sales-sync/person-balance) and endpoint.");
        BalanceBusy = _meter.CreateCounter<long>(
            "orderdeck.balance.busy_total",
            description: "Balance requests that returned 503 balance-busy, tagged by endpoint: a write exhausted its retries, or a read outside the retried attempt was chosen as a deadlock victim. Nothing was written in either case.");
        BalanceMultiLegDraws = _meter.CreateCounter<long>(
            "orderdeck.balance.multi_leg_draws_total",
            description: "Person-pool draws written across more than one balance row, tagged by legs (rows written, anchor included).");
    }

    public Counter<long> LicensesActivated { get; }
    public Counter<long> LicenseActivationFailures { get; }
    public Counter<long> BackupsUploaded { get; }
    public Histogram<long> BackupUploadBytes { get; }
    public Counter<long> EmailsSent { get; }
    public Counter<long> RefreshTokensRotated { get; }
    public Counter<long> RefreshTokenRotationFailures { get; }
    public Counter<long> LicenseTelemetryTouchFailures { get; }
    public Counter<long> BalanceCustomerNotSynced { get; }
    public Counter<long> BalanceClientUpdateRequired { get; }
    public Counter<long> BalanceBusy { get; }
    public Counter<long> BalanceMultiLegDraws { get; }
}
