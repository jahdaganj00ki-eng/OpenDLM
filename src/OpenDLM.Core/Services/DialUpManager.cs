using System.Diagnostics;
using Microsoft.Win32;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Windows dial-up and VPN connections, the reference's "Dial Up / VPN" tab.
///
/// Dial-up and RAS VPN entries are configured by Windows itself, so this does not
/// implement a tunnel: it enumerates the connections Windows already knows about,
/// dials and hangs them up with <c>rasdial</c>, and redials according to the
/// settings. Anything that is not a RAS entry (a modern VPN client, for example) is
/// left to that client to manage.
/// </summary>
public static class DialUpManager
{
    private const string RasPhonebook = @"Software\Microsoft\Windows\CurrentVersion\Ras Phonebook";

    /// <summary>Connections Windows has configured, for the picker in the options dialog.</summary>
    public static List<string> ListConnections()
    {
        var connections = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RasPhonebook);
            var profile = key?.GetValue("Profile") as string;

            if (!string.IsNullOrWhiteSpace(profile) && File.Exists(profile))
            {
                // The phonebook is a plain text file: one "Type=3" entry per connection.
                foreach (var line in File.ReadLines(profile))
                {
                    if (!line.StartsWith("Type=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rest = line["Type=".Length..];
                    var separator = rest.LastIndexOf('=');
                    if (separator < 0)
                    {
                        continue;
                    }

                    var name = rest[(separator + 1)..].Trim();
                    if (name.Length > 0 && !connections.Contains(name))
                    {
                        connections.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate the Windows dial-up connections: " + ex.Message);
        }

        connections.Sort(StringComparer.OrdinalIgnoreCase);
        return connections;
    }

    /// <summary>True when the named connection is currently dialled.</summary>
    public static bool IsConnected(string connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            return false;
        }

        try
        {
            var output = RunRasDial(connectionName, exitContext: true);
            return output.Contains("Connected", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Dials the connection, redialing according to the settings. Returns true once
    /// the connection reports itself as connected.
    /// </summary>
    public static bool Dial(DialUpSettings settings, out string message)
    {
        message = string.Empty;

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ConnectionName))
        {
            message = "No dial-up or VPN connection is configured.";
            return false;
        }

        var attempts = settings.RedialAttempts == 0
            ? int.MaxValue
            : Math.Max(1, settings.RedialAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var output = RunRasDial(settings.ConnectionName, settings.UserName,
                    CredentialProtector.Unprotect(settings.PasswordProtected), settings.Domain);

                if (output.Contains("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    message = $"Connected to {settings.ConnectionName}.";
                    Log.Info(message);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Dialling failed: " + ex.Message);
            }

            if (attempt == attempts)
            {
                break;
            }

            Log.Info($"Redialling {settings.ConnectionName} " +
                     $"({attempt}/{attempts}) in {settings.RedialIntervalSeconds}s.");

            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(settings.RedialIntervalSeconds, 1, 3600)));
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }

        message = $"Could not connect to {settings.ConnectionName}.";
        return false;
    }

    /// <summary>Hangs the connection up. Harmless when it is not connected.</summary>
    public static void HangUp(DialUpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ConnectionName))
        {
            return;
        }

        try
        {
            Run("rasdial.exe", "\"" + settings.ConnectionName + "\" /disconnect", timeoutSeconds: 20);
            Log.Info($"Hung up {settings.ConnectionName}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not hang up: " + ex.Message);
        }
    }

    private static string RunRasDial(string connectionName, string? user = null, string? password = null, string? domain = null, bool exitContext = false)
    {
        var arguments = "\"" + connectionName + "\"";
        if (exitContext)
        {
            arguments += " /disconnect"; // only used to probe; the call site handles the result
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            arguments += " " + user;
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            arguments += " " + password;
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            arguments += " " + domain;
        }

        return Run("rasdial.exe", arguments, timeoutSeconds: 30);
    }

    private static string Run(string fileName, string arguments, int timeoutSeconds)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return string.Empty;
        }

        // Read both pipes before waiting, so a chatty rasdial cannot fill a buffer and
        // deadlock against WaitForExit.
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Nothing more we can do.
            }
        }

        return output;
    }
}
