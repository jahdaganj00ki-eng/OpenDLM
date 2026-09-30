using System.Diagnostics;
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
/// hear it. This does the same with <c>System.Media.SoundPlayer</c>, which covers
/// WAV and plays synchronously on the caller's thread, so it is used from the UI
/// thread rather than a download worker.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    private readonly SettingsService _settingsService;
    private System.Media.SoundPlayer? _player;
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
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\Media",
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

                foreach (var file in Directory.EnumerateFiles(folder, "*.wav").OrderBy(f => f))
                {
                    found.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not list sounds in " + folder + ": " + ex.Message);
            }
        }

        return found;
    }

    /// <summary>Plays the sound for an event, if one is configured and enabled.</summary>
    public void Play(SoundEvent which)
    {
        var settings = _settingsService.Current.Sounds;

        if (_disposed || !settings.Enabled)
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

    /// <summary>
    /// Plays a sound file directly, which is what the options dialog's "play" button
    /// uses so the user can hear the choice before saving it.
    /// </summary>
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

            Stop();

            _player = new System.Media.SoundPlayer(path);
            _player.Load();
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not play " + path + ": " + ex.Message);
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch
        {
            // Nothing useful to do if stopping fails.
        }
        finally
        {
            _player = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }
}
