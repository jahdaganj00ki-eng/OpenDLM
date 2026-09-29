using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Writes are atomic (temp file + replace)
/// so a crash or power loss can never leave a truncated settings file behind, and a
/// corrupt file is quarantined instead of silently discarding the user's setup.
/// </summary>
public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly object _gate = new();
    private AppSettings _current = AppSettings.CreateDefault();

    public SettingsService()
    {
        Current = Load();
    }

    /// <summary>Live settings instance. Mutate through <see cref="Update"/> or call <see cref="Save"/> after editing.</summary>
    public AppSettings Current
    {
        get => _current;
        private set => _current = value;
    }

    /// <summary>Raised after settings were replaced or saved, so open windows can refresh.</summary>
    public event EventHandler<AppSettings>? Changed;

    public static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TimeSpanJsonConverter(),
            new NullableTimeSpanJsonConverter()
        }
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                var fresh = AppSettings.CreateDefault();
                fresh.Normalize();
                return fresh;
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
            {
                throw new InvalidDataException("Settings file deserialized to null.");
            }

            loaded.Normalize();
            Log.Info($"Settings loaded from {AppPaths.SettingsFile}");
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings; falling back to defaults.", ex);
            QuarantineCorruptFile(AppPaths.SettingsFile);
            var fallback = AppSettings.CreateDefault();
            fallback.Normalize();
            return fallback;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Log.Enabled = _current.Advanced.EnableLogging;
                AtomicWrite(AppPaths.SettingsFile,
                    JsonSerializer.Serialize(_current, JsonOptions));
                Changed?.Invoke(this, _current);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save settings.", ex);
                throw;
            }
        }
    }

    /// <summary>Applies a mutation, normalizes and persists it, then notifies listeners.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            _current.Normalize();
        }
        Save();
    }

    /// <summary>Replaces the whole configuration (used by "import settings").</summary>
    public void Replace(AppSettings settings)
    {
        lock (_gate)
        {
            _current = settings;
            _current.Normalize();
        }
        Save();
    }

    public void ResetToDefaults()
    {
        lock (_gate)
        {
            _current = AppSettings.CreateDefault();
            _current.Normalize();
        }
        Save();
    }

    /// <summary>
    /// Writes a file atomically: serialize to a sibling temp file, flush it to disk,
    /// then swap it into place. Prevents a half-written config after a crash.
    /// </summary>
    public static void AtomicWrite(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(true);
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            var backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(path, backup);
            Log.Warn("Moved unreadable file aside: " + backup);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not quarantine corrupt file: " + ex.Message);
        }
    }
}
