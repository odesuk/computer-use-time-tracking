using ComputerUse.TimeTracking.Services;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void NewFields_RoundTrip()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        var service = new SettingsService(new ClientConfigurationStore(workspace.SettingsPath));
        service.Save(new ClientSetupSettings
        {
            PdfReportDirectory = workspace.ReportDirectory,
            EndOfDayReportTime = new TimeOnly(17, 30),
            IncludeWindowTitlesInPdf = false,
            ExcludedProcessNames = new() { "WhatsApp", "Signal" },
            RawActivityRetentionDays = 30,
            PausedUntil = DateTimeOffset.MaxValue,
        });

        ClientSetupSettings loaded = new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)).Current;

        Assert.True(loaded.SetupCompleted);
        Assert.Equal(new TimeOnly(17, 30), loaded.EndOfDayReportTime);
        Assert.False(loaded.IncludeWindowTitlesInPdf);
        Assert.Equal(new[] { "WhatsApp", "Signal" }, loaded.ExcludedProcessNames);
        Assert.Equal(30, loaded.RawActivityRetentionDays);
    Assert.Equal(ClientSetupSettings.RawActivityRetentionLimitDays, loaded.EffectiveRetentionDays);
        Assert.Equal(DateTimeOffset.MaxValue, loaded.PausedUntil);
    }

    [Fact]
    public void Version1File_LoadsWithDefaults()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        File.WriteAllText(workspace.SettingsPath, $$"""
            {
              "SetupCompleted": true,
              "PdfReportDirectory": {{System.Text.Json.JsonSerializer.Serialize(workspace.ReportDirectory)}},
              "HostOwnedSetting": true
            }
            """);

        ClientSetupSettings loaded = new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)).Current;

        Assert.True(loaded.SetupCompleted);
        Assert.True(loaded.ExtensionData!["HostOwnedSetting"].GetBoolean());
        Assert.Equal(workspace.ReportDirectory, loaded.PdfReportDirectory);
        Assert.Equal(new TimeOnly(18, 0), loaded.EndOfDayReportTime);
        Assert.True(loaded.IncludeWindowTitlesInPdf);
        Assert.Empty(loaded.ExcludedProcessNames);
    Assert.Equal(ClientSetupSettings.RawActivityRetentionLimitDays, loaded.RawActivityRetentionDays);
        Assert.Null(loaded.PausedUntil);
    }

    [Fact]
    public void DamagedFile_FallsBackToDefaults()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        File.WriteAllText(workspace.SettingsPath, "{ not json");

        ClientSetupSettings loaded = new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)).Current;

        Assert.False(loaded.SetupCompleted);
    }

    [Fact]
    public void Update_SavesAndRaisesChanged_AndCurrentIsACopy()
    {
        using var workspace = new TempWorkspace(initializeDatabase: false);
        SettingsService service = workspace.CreateSettings();
        ClientSetupSettings? changed = null;
        service.Changed += (_, settings) => changed = settings;

        service.Current.RawActivityRetentionDays = 1;   // mutating a copy has no effect
        service.Update(settings => settings.EndOfDayReportTime = new TimeOnly(16, 0));

        Assert.Equal(new TimeOnly(16, 0), changed?.EndOfDayReportTime);
        Assert.Equal(ClientSetupSettings.RawActivityRetentionLimitDays, service.Current.RawActivityRetentionDays);
        Assert.Equal(new TimeOnly(16, 0),
            new SettingsService(new ClientConfigurationStore(workspace.SettingsPath)).Current.EndOfDayReportTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(100000)]
    public void EffectiveRetention_IsFixedByPrivacyPolicy(int configured)
    {
        Assert.Equal(ClientSetupSettings.RawActivityRetentionLimitDays,
            new ClientSetupSettings { RawActivityRetentionDays = configured }.EffectiveRetentionDays);
    }
}
