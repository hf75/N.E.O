using FluentAssertions;
using Neo.App;
using System.ComponentModel;
using System.Text.Json;
using Xunit;

namespace Neo.App.Core.Tests;

public class SettingsModelTests
{
    // ── Default values ──────────────────────────────────────────────

    [Fact]
    public void DefaultValues_AiCodeGenerationAttempts_Is5()
    {
        var model = new SettingsModel();

        model.AiCodeGenerationAttempts.Should().Be(5);
    }

    [Fact]
    public void DefaultValues_UseReactUi_IsFalse()
    {
        var model = new SettingsModel();

        model.UseReactUi.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_UsePython_IsFalse()
    {
        var model = new SettingsModel();

        model.UsePython.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_UseAvalonia_IsFalse()
    {
        var model = new SettingsModel();

        model.UseAvalonia.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_AcceptAutomatic_IsFalse()
    {
        var model = new SettingsModel();

        model.AcceptAutomatic.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_AIQueryProvider_IsClaude()
    {
        var model = new SettingsModel();

        model.AIQueryProvider.Should().Be("Claude");
    }

    // ── PropertyChanged fires when setting changes ──────────────────

    [Fact]
    public void PropertyChanged_FiredWhenValueChanges()
    {
        var model = new SettingsModel();
        var firedProps = new List<string>();
        model.PropertyChanged += (_, e) => firedProps.Add(e.PropertyName!);

        model.AiCodeGenerationAttempts = 10;

        firedProps.Should().Contain(nameof(SettingsModel.AiCodeGenerationAttempts));
    }

    [Fact]
    public void PropertyChanged_FiredForStringProperty()
    {
        var model = new SettingsModel();
        var firedProps = new List<string>();
        model.PropertyChanged += (_, e) => firedProps.Add(e.PropertyName!);

        model.ClaudeModel = "new-model";

        firedProps.Should().Contain(nameof(SettingsModel.ClaudeModel));
    }

    [Fact]
    public void PropertyChanged_FiredForBoolProperty()
    {
        var model = new SettingsModel();
        var firedProps = new List<string>();
        model.PropertyChanged += (_, e) => firedProps.Add(e.PropertyName!);

        model.UseAvalonia = true;

        firedProps.Should().Contain(nameof(SettingsModel.UseAvalonia));
    }

    // ── PropertyChanged does NOT fire when same value set ────────────

    [Fact]
    public void PropertyChanged_DoesNotFire_WhenSameValueSet()
    {
        var model = new SettingsModel();
        var firedCount = 0;
        model.PropertyChanged += (_, _) => firedCount++;

        model.AiCodeGenerationAttempts = 5; // Same as default.

        firedCount.Should().Be(0);
    }

    [Fact]
    public void PropertyChanged_DoesNotFire_WhenSameStringValueSet()
    {
        var model = new SettingsModel();
        var firedCount = 0;
        model.PropertyChanged += (_, _) => firedCount++;

        model.ClaudeModel = model.ClaudeModel; // Same value.

        firedCount.Should().Be(0);
    }

    [Fact]
    public void PropertyChanged_DoesNotFire_WhenSameBoolValueSet()
    {
        var model = new SettingsModel();
        var firedCount = 0;
        model.PropertyChanged += (_, _) => firedCount++;

        model.UseReactUi = false; // Same as default.

        firedCount.Should().Be(0);
    }

    // ── PluginAgentModels ───────────────────────────────────────────

    [Fact]
    public void PluginAgentModels_DefaultIsEmptyDictionary()
    {
        var model = new SettingsModel();

        model.PluginAgentModels.Should().NotBeNull();
        model.PluginAgentModels.Should().BeEmpty();
    }

    [Fact]
    public void PluginAgentModels_SetNull_BecomesEmptyDictionary()
    {
        var model = new SettingsModel();

        model.PluginAgentModels = null!;

        model.PluginAgentModels.Should().NotBeNull();
        model.PluginAgentModels.Should().BeEmpty();
    }
}

/// <summary>
/// SettingsService uses static methods. Every test here works in a folder of its own, never on
/// the settings of whoever runs the tests.
/// </summary>
[Collection("SettingsFolder")]
public class SettingsServiceTests : IDisposable
{
    private readonly string? _previousFolder = Environment.GetEnvironmentVariable(SettingsService.FolderEnvironmentVariable);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo_settings_" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsServiceTests() => Environment.SetEnvironmentVariable(SettingsService.FolderEnvironmentVariable, _folder);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SettingsService.FolderEnvironmentVariable, _previousFolder);
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    // ── Save + Load roundtrip ───────────────────────────────────────

    [Fact]
    public void SaveAndLoad_Roundtrip_PreservesProperties()
    {
        // Save a known model, then load it back and verify.
        var model = new SettingsModel
        {
            AiCodeGenerationAttempts = 10,
            UseReactUi = true,
            ClaudeModel = "test-model",
            ExportBasePath = "/tmp/export",
        };

        SettingsService.Save(model);
        var loaded = SettingsService.Load();

        loaded.AiCodeGenerationAttempts.Should().Be(10);
        loaded.UseReactUi.Should().BeTrue();
        loaded.ClaudeModel.Should().Be("test-model");
        loaded.ExportBasePath.Should().Be("/tmp/export");
    }

    [Fact]
    public void Folder_IsTheOneTheEnvironmentVariableNames()
    {
        SettingsService.GetSettingsFilePath().Should().Be(Path.Combine(_folder, "settings.json"));
    }

    // ── Load with corrupt file → defaults ───────────────────────────

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(SettingsService.GetSettingsFilePath(), "THIS IS NOT VALID JSON {{{");

        var loaded = SettingsService.Load();

        loaded.Should().NotBeNull();
        loaded.AiCodeGenerationAttempts.Should().Be(5);
    }

    // ── Save validates AiCodeGenerationAttempts minimum ──────────────

    [Fact]
    public void Save_AttemptsLessThan1_CorrectedTo5()
    {
        var model = new SettingsModel { AiCodeGenerationAttempts = 0 };

        // Save mutates the model's property before writing.
        SettingsService.Save(model);

        model.AiCodeGenerationAttempts.Should().Be(5);
    }

    // ── GetSettingsFolderPath creates the directory ──────────────────

    [Fact]
    public void GetSettingsFolderPath_ReturnsExistingDirectory()
    {
        var path = SettingsService.GetSettingsFolderPath();

        Directory.Exists(path).Should().BeTrue();
        path.Should().Be(_folder);
    }

    [Fact]
    public void GetSettingsFolderPath_WithoutOverride_IsTheNeoFolder()
    {
        Environment.SetEnvironmentVariable(SettingsService.FolderEnvironmentVariable, null);

        SettingsService.GetSettingsFolderPath().Should().EndWith("Neo");
    }

    // ── JSON serialization roundtrip (no file system) ───────────────

    [Fact]
    public void JsonRoundtrip_SerializeAndDeserialize_PreservesModel()
    {
        var model = new SettingsModel
        {
            AiCodeGenerationAttempts = 7,
            UseAvalonia = true,
            OllamaEndpoint = "http://custom:1234/v1/",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(model, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SettingsModel>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        loaded.Should().NotBeNull();
        loaded!.AiCodeGenerationAttempts.Should().Be(7);
        loaded.UseAvalonia.Should().BeTrue();
        loaded.OllamaEndpoint.Should().Be("http://custom:1234/v1/");
    }
}
