using System.Runtime.InteropServices;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>The events that can play a sound.</summary>
public enum SoundEvent
{
    Start = 0,
    Complete = 1,
    Error = 2
}

/// <summary>
/// Plays the event sounds configured in the options.
///
/// The reference lets the user pick a sound file per event and press a button to
/// hear it. This does the same, but by driving <c>winmm</c> directly rather than
/// through <c>System.Media.SoundPlayer</c>: that type lives in
/// <c>System.Windows.Extensions</c>, which the engine cannot reference without
/// pulling in a WPF-only assembly the engine has no use for.
///
/// Playback is fire-and-forget on a throwaway thread, because the engine must not
/// block a download worker on audio.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    private readonly SettingsService _settingsService;
    private bool _disposed;

    public SoundPlayer(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>Sound files found on this machine, offered in the options.</summary>
    public static IReadOnlyList<string> ListAvailableSounds()
    {
        var found = new List<string>();

        foreach (var folder in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media"),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Microsoft", "Windows", "Sounds")
                 })
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                found.AddRange(Directory.EnumerateFiles(folder, "*.wav").OrderBy(f => f));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not list sounds in " + folder + ": " + ex.Message);
            }
        }

        return found;
    }

    /// <summary>Plays the sound configured for an event, if one is enabled.</summary>
    public void Play(SoundEvent which)
    {
        if (_disposed)
        {
            return;
        }

        var settings = _settingsService.Current.Sounds;

        if (!settings.Enabled)
        {
            return;
        }

        var enabled = which switch
        {
            SoundEvent.Start => settings.PlayOnStart,
            SoundEvent.Complete => settings.PlayOnComplete,
            _ => settings.PlayOnError
        };

        if (!enabled)
        {
            return;
        }

        var file = which switch
        {
            SoundEvent.Start => settings.StartSound,
            SoundEvent.Complete => settings.CompleteSound,
            _ => settings.ErrorSound
        };

        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        PlayFile(file);
    }

    /// <summary>Plays a sound file, which is what the options dialog's Play button uses.</summary>
    public void PlayFile(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                Log.Warn("The sound file does not exist: " + path);
                return;
            }

            // Off the calling thread: this may be a download worker.
            var thread = new Thread(() => PlayOnWorkerThread(path))
            {
                IsBackground = true,
                Name = "opendlm-sound"
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not start playing " + path + ": " + ex.Message);
        }
    }

    private static void PlayOnWorkerThread(string path)
    {
        // PlaySound with SND_FILENAME plays asynchronously and returns, so the
        // thread finishes immediately and the sound is owned by the OS.
        const uint sndAsync = 0x0001;
        const uint sndMemory = 0x0004;

        if (!PlaySound(path, IntPtr.Zero, sndAsync | sndMemory))
        {
            Log.Warn("The system refused to play " + path);
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    public void Dispose() => _disposed = true;
}
