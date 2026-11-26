using MouseDebounceFix.Core;

namespace MouseDebounceFix.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsAllProperties()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"MouseDebounceFixTests_{Guid.NewGuid():N}");
        string filePath = Path.Combine(tempDir, "settings.json");

        try
        {
            var original = new AppSettings
            {
                ThresholdMs = 75.0,
                FilterLeftButton = true,
                FilterRightButton = false,
                StartPaused = true,
                IgnoreInjectedEvents = false
            };

            original.Save(filePath);
            AppSettings loaded = AppSettings.Load(filePath);

            Assert.Equal(75.0, loaded.ThresholdMs);
            Assert.True(loaded.FilterLeftButton);
            Assert.False(loaded.FilterRightButton);
            Assert.True(loaded.StartPaused);
            Assert.False(loaded.IgnoreInjectedEvents);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json");

        AppSettings loaded = AppSettings.Load(missingPath);

        Assert.Equal(DebounceEngine.DefaultThresholdMs, loaded.ThresholdMs);
        Assert.True(loaded.FilterLeftButton);
        Assert.True(loaded.FilterRightButton);
        Assert.False(loaded.StartPaused);
        Assert.True(loaded.IgnoreInjectedEvents);
    }

    [Fact]
    public void Load_WhenJsonCorrupted_ReturnsDefaultsWithoutThrowing()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempFile, "{ invalid json :::");

            AppSettings loaded = AppSettings.Load(tempFile);

            Assert.Equal(DebounceEngine.DefaultThresholdMs, loaded.ThresholdMs);
            Assert.True(loaded.IgnoreInjectedEvents);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Sanitize_ClampsOutOfRangeAndInvalidThresholds()
    {
        var tooHigh = new AppSettings { ThresholdMs = 9999.0 };
        tooHigh.Sanitize();
        Assert.Equal(AppSettings.MaxThresholdMs, tooHigh.ThresholdMs);

        var negative = new AppSettings { ThresholdMs = -25.0 };
        negative.Sanitize();
        Assert.Equal(AppSettings.MinThresholdMs, negative.ThresholdMs);

        var nan = new AppSettings { ThresholdMs = double.NaN };
        nan.Sanitize();
        Assert.Equal(DebounceEngine.DefaultThresholdMs, nan.ThresholdMs);
    }
}
