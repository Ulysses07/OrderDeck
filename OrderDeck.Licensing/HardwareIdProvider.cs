using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OrderDeck.Licensing;

[SupportedOSPlatform("windows")]
public sealed class HardwareIdProvider : IHardwareIdProvider
{
    public string GetHardwareId()
    {
        var machineGuid = ReadMachineGuid();
        var cpuId = ReadCpuId();
        var sid = ReadCurrentUserSid();
        return ComputeHash(machineGuid, cpuId, sid);
    }

    public string? GetLegacyHardwareId()
    {
        // Reproduces the pre-Phase-5d hash so the server can migrate existing
        // activation rows. If we ever can't read MachineGuid we'd have already
        // thrown above, so this path being unreachable for real failures is fine.
        try
        {
            var machineGuid = ReadMachineGuid();
            var cpuId = ReadCpuId();
            var username = Environment.UserName;
            return ComputeLegacyHash(machineGuid, cpuId, username);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>New (Phase 5d) hash function — exposed for testing.</summary>
    public static string ComputeHash(string machineGuid, string cpuId, string sid)
    {
        var raw = $"{machineGuid}|{cpuId}|{sid}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Pre-Phase-5d hash function — exposed for testing + transition.</summary>
    public static string ComputeLegacyHash(string machineGuid, string cpuId, string username)
    {
        var raw = $"{machineGuid}|{cpuId}|{username.ToLowerInvariant()}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var value = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Cannot read HKLM\\SOFTWARE\\Microsoft\\Cryptography\\MachineGuid.");
        return value;
    }

    private static string ReadCpuId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var id = obj["ProcessorId"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }
        catch
        {
            // WMI failures fall through to fallback.
        }
        return "unknown-cpu";
    }

    /// <summary>Current Windows user's SID. Immutable for the lifetime of the
    /// account — survives username rename, profile move, etc. Falls back to a
    /// deterministic placeholder so the hash still computes; in that path the
    /// machine/CPU mix carries the identity.</summary>
    private static string ReadCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown-sid";
        }
        catch
        {
            return "unknown-sid";
        }
    }
}
