using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LiveDeck.Licensing.Trial;

/// <summary>
/// Cross-user JSON file storage at <c>C:\ProgramData\LiveDeck\trial.dat</c>.
/// HMAC field detects field-level tampering (e.g. extending ExpiresAt by hand).
/// Multi-user readable — DPAPI not applicable.
/// </summary>
public sealed class ProgramDataTrialStorage : ITrialStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly ILogger<ProgramDataTrialStorage> _log;

    public ProgramDataTrialStorage(string path, ILogger<ProgramDataTrialStorage> log)
    {
        _path = path;
        _log = log;
    }

    public string Name => "programdata";

    public TrialRecord? TryRead()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var json = File.ReadAllText(_path);
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (envelope is null) return null;

            var record = new TrialRecord(envelope.StartedAt, envelope.ExpiresAt, envelope.HardwareFingerprint, envelope.Version);
            if (!TrialHmac.Verify(record, envelope.Hmac))
            {
                _log.LogWarning("ProgramData trial state HMAC mismatch at {Path} — treating as tampered", _path);
                return null;
            }
            return record;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read ProgramData trial state at {Path}", _path);
            return null;
        }
    }

    public void Write(TrialRecord record)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var envelope = new Envelope(
            StartedAt: record.StartedAt,
            ExpiresAt: record.ExpiresAt,
            HardwareFingerprint: record.HardwareFingerprint,
            Version: record.Version,
            Hmac: TrialHmac.Compute(record));
        File.WriteAllText(_path, JsonSerializer.Serialize(envelope, JsonOpts));
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clear ProgramData trial state at {Path}", _path); }
    }

    private sealed record Envelope(
        DateTimeOffset StartedAt,
        DateTimeOffset ExpiresAt,
        string HardwareFingerprint,
        int Version,
        string Hmac);
}
