using LocalCaption.Core.Data;

namespace LocalCaption.Core.Tests;

/// <summary>specs/SPEC-16 §6 C6 (transcript folder writability) and C7 (detection limits).</summary>
public sealed class SettingsInputTests
{
    [Theory]
    [InlineData(0, 200)]
    [InlineData(199, 200)]
    [InlineData(200, 200)]
    [InlineData(625, 625)]        // clamped, not snapped: a Mac config keeps its value
    [InlineData(2000, 2000)]
    [InlineData(99999, 2000)]
    [InlineData(-5, 200)]
    public void Endpoint_silence_is_clamped_to_the_mac_range(int value, int expected) =>
        Assert.Equal(expected, SettingsInput.EndpointSilenceMs(value));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(20, 20)]
    [InlineData(60, 60)]
    [InlineData(600, 60)]
    public void Max_utterance_is_clamped_to_the_mac_range(int value, int expected) =>
        Assert.Equal(expected, SettingsInput.MaxUtteranceS(value));

    [Fact]
    public void A_writable_folder_passes_and_is_left_clean()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lc-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(SettingsInput.FolderProblem(folder));
            Assert.True(Directory.Exists(folder));                 // created when missing
            Assert.Empty(Directory.GetFileSystemEntries(folder));  // the probe is gone
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_cannot_be_created_is_refused()
    {
        // A "folder" whose parent is a file can never be created, on any platform.
        var file = Path.Combine(Path.GetTempPath(), "lc-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(SettingsInput.UnwritableFolder, SettingsInput.FolderProblem(Path.Combine(file, "inside")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/folder")]
    public void Blank_or_relative_is_refused(string? folder) =>
        Assert.NotNull(SettingsInput.FolderProblem(folder));
}
