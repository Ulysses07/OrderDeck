using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LiveDeck.Licensing;

public sealed class HardwareIdProvider : IHardwareIdProvider
{
    public string GetHardwareId()
    {
        var machineGuid = ReadMachineGuid();
        var cpuId = ReadCpuId();
        var username = Environment.UserName;
        return ComputeHash(machineGuid, cpuId, username);
    }

    /// <summary>Pure hash function — exposed for testing.</summary>
    public static string ComputeHash(string machineGuid, string cpuId, string username)
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
}
