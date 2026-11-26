using System.Text.Json;

namespace MouseDebounceFix.Core;

/// <summary>
/// User preferences persisted locally in JSON format.
/// </summary>
public sealed class AppSettings
{
    public const double MinThresholdMs = 0.0;
    public const double MaxThresholdMs = 500.0;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public double ThresholdMs { get; set; } = DebounceEngine.DefaultThresholdMs;
    public bool FilterLeftButton { get; set; } = true;
    public bool FilterRightButton { get; set; } = true;
    public bool StartPaused { get; set; } = false;
    public bool IgnoreInjectedEvents { get; set; } = true;

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MouseDebounceFix",
        "settings.json");

    /// <summary>
    /// Ensures all numeric fields fall within safe operational bounds.
    /// </summary>
    public void Sanitize()
    {
        if (double.IsNaN(ThresholdMs) || double.IsInfinity(ThresholdMs))
        {
            ThresholdMs = DebounceEngine.DefaultThresholdMs;
        }
        else
        {
            ThresholdMs = Math.Clamp(ThresholdMs, MinThresholdMs, MaxThresholdMs);
        }
    }

    /// <summary>
    /// Loads settings from disk, falling back cleanly to defaults if the file is missing or corrupted.
    /// </summary>
    public static AppSettings Load(string? filePath = null)
    {
        string path = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath;

        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (loaded is null)
            {
                return new AppSettings();
            }

            loaded.Sanitize();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persists settings atomically to disk.
    /// </summary>
    public void Save(string? filePath = null)
    {
        Sanitize();

        string path = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(this, SerializerOptions);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of temporary file.
                }
            }
        }
    }
}
