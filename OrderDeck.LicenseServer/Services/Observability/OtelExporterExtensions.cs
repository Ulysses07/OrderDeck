using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace OrderDeck.LicenseServer.Services.Observability;

/// <summary>
/// Conditional OTLP exporter wiring. We don't want to require an OTLP endpoint
/// to run the server (dev / smoke deployments don't have one), so the exporter
/// is added only when the standard OTEL_EXPORTER_OTLP_ENDPOINT env var is set.
///
/// /metrics (Prometheus) keeps working regardless — it's the always-on signal.
/// </summary>
public static class OtelExporterExtensions
{
    public static TracerProviderBuilder AddOtlpExporterIfConfigured(
        this TracerProviderBuilder builder, IConfiguration config)
    {
        if (!IsOtlpConfigured(config)) return builder;
        return builder.AddOtlpExporter();
    }

    public static MeterProviderBuilder AddOtlpExporterIfConfigured(
        this MeterProviderBuilder builder, IConfiguration config)
    {
        if (!IsOtlpConfigured(config)) return builder;
        return builder.AddOtlpExporter();
    }

    private static bool IsOtlpConfigured(IConfiguration config)
    {
        // OTEL_EXPORTER_OTLP_ENDPOINT is the OpenTelemetry standard env var.
        // We accept either env var or appsettings key for ops flexibility.
        var endpoint = config["OTEL_EXPORTER_OTLP_ENDPOINT"]
                       ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return !string.IsNullOrWhiteSpace(endpoint);
    }
}
