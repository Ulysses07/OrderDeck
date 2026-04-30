using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace OrderDeck.Licensing.Trial;

/// <summary>
/// HKCU registry storage. Subkey path is configurable via <see cref="LicensingOptions.TrialRegistrySubKey"/>.
/// Values are stored as REG_SZ (ISO-8601 timestamps + fingerprint) and REG_DWORD (version).
/// </summary>
public sealed class HkcuTrialStorage : ITrialStorage
{
    private readonly string _subKey;
    private readonly ILogger<HkcuTrialStorage> _log;

    public HkcuTrialStorage(IOptions<LicensingOptions> opts, ILogger<HkcuTrialStorage> log)
    {
        _subKey = opts.Value.TrialRegistrySubKey;
        _log = log;
    }

    public string Name => "hkcu";

    public TrialRecord? TryRead()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKey);
            if (key is null) return null;

            var startedRaw = key.GetValue("StartedAt") as string;
            var expiresRaw = key.GetValue("ExpiresAt") as string;
            var fingerprint = key.GetValue("HardwareFingerprint") as string;
            var versionObj = key.GetValue("Version");

            if (string.IsNullOrEmpty(startedRaw) || string.IsNullOrEmpty(expiresRaw)
                || string.IsNullOrEmpty(fingerprint) || versionObj is null)
                return null;

            if (!DateTimeOffset.TryParse(startedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
                || !DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
                return null;

            return new TrialRecord(started, expires, fingerprint, Convert.ToInt32(versionObj));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read HKCU trial state at {SubKey}", _subKey);
            return null;
        }
    }

    public void Write(TrialRecord record)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot create HKCU subkey {_subKey}");
        key.SetValue("StartedAt", record.StartedAt.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue("ExpiresAt", record.ExpiresAt.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue("HardwareFingerprint", record.HardwareFingerprint, RegistryValueKind.String);
        key.SetValue("Version", record.Version, RegistryValueKind.DWord);
    }

    public void Clear()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clear HKCU trial state at {SubKey}", _subKey);
        }
    }
}
