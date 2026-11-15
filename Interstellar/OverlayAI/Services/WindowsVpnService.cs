using System.IO;
using System.Diagnostics;
using System.Text;

namespace OverlayAI.Services;

/// <summary>Connects to VPN profiles already configured in Windows using the built-in RasDial client.</summary>
internal static class WindowsVpnService
{
    private static readonly string[] PhonebookPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Ras", "rasphone.pbk")
    };

    public static IReadOnlyList<string> GetProfiles()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in PhonebookPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
                    {
                        var name = line[1..^1].Trim();
                        if (name.Length > 0 && !name.Equals("Phonebook", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                    }
                }
            }
            catch { /* An inaccessible all-users phonebook should not prevent showing user profiles. */ }
        }
        return names.ToArray();
    }

    public static Task<string> ConnectAsync(string profileName) => RunAsync(profileName, disconnect: false);
    public static Task<string> DisconnectAsync(string profileName) => RunAsync(profileName, disconnect: true);
    public static Task<string> GetConnectionSummaryAsync() => RunAsync(null, disconnect: false);

    private static async Task<string> RunAsync(string? profileName, bool disconnect)
    {
        var rasdial = Path.Combine(Environment.SystemDirectory, "rasdial.exe");
        var start = new ProcessStartInfo
        {
            FileName = rasdial,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };

        if (profileName is not null)
        {
            start.ArgumentList.Add(profileName);
            var phonebook = FindPhonebook(profileName);
            if (phonebook is not null) start.ArgumentList.Add($"/phonebook:{phonebook}");
        }
        if (disconnect) start.ArgumentList.Add("/disconnect");

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Windows VPN dialer could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("The Windows VPN command took longer than 45 seconds. Check the VPN profile or finish any sign-in prompt in Windows Settings.");
        }

        var output = (await stdout) + Environment.NewLine + (await stderr);
        output = output.Trim();
        if (output.Length == 0)
            output = process.ExitCode == 0 ? "Windows VPN command completed." : $"Windows VPN returned exit code {process.ExitCode}.";
        if (process.ExitCode != 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase))
            output += $"\nExit code: {process.ExitCode}. If this VPN needs a sign-in prompt, connect it once in Windows Settings and save its credentials.";
        return output.Length > 1800 ? output[..1800] : output;
    }

    private static string? FindPhonebook(string profileName)
    {
        foreach (var path in PhonebookPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length > 2 && line[0] == '[' && line[^1] == ']' &&
                        line[1..^1].Trim().Equals(profileName, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }
            catch { }
        }
        return null;
    }
}
