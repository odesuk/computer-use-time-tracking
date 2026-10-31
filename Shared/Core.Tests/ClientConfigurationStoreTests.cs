using ComputerUse.TimeTracking.Services;
using System.Text.Json;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class ClientConfigurationStoreTests
{
    [Fact]
    public void SaveAndLoad_PreservesUserChoices()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string settingsPath = Path.Combine(directory, "settings.json");
            var expected = new ClientSetupSettings
            {
                SetupCompleted = true,
                PdfReportDirectory = Path.Combine(directory, "reports"),
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["HostOwnedSetting"] = JsonSerializer.SerializeToElement(true),
                },
            };

            var store = new ClientConfigurationStore(settingsPath);
            store.Save(expected);
            ClientSetupSettings? actual = store.Load();

            Assert.NotNull(actual);
            Assert.Equal(expected.SetupCompleted, actual.SetupCompleted);
            Assert.Equal(expected.PdfReportDirectory, actual.PdfReportDirectory);
            Assert.True(actual.ExtensionData!["HostOwnedSetting"].GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
