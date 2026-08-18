using System.Text.Json;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Migration;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class ProfileMigrationClientTests
{
    [Fact]
    public void MigrateUnversionedAddsSchemaVersionAndBackup()
    {
        PortableLayout layout = CreateLayout(out string root);
        string path = layout.ConfigFile;
        File.WriteAllText(path, "{\"preload_pipelines\":[\"OCR\"],\"hotkey\":\"Ctrl+Alt+Q\"}");
        byte[] original = File.ReadAllBytes(path);
        try
        {
            MigrationResult result = ProfileMigrationClient.MigrateConfig(layout);

            Assert.Equal("migrated", result.Status);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
            Assert.Equal(ProfileMigrationClient.CurrentSchemaVersion, data!["schema_version"].GetInt32());
            Assert.Equal("Ctrl+Alt+Q", data!["hotkey"].GetString());
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(original, File.ReadAllBytes(result.BackupPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecondRunIsAlreadyMigrated()
    {
        PortableLayout layout = CreateLayout(out string root);
        string path = layout.ConfigFile;
        File.WriteAllText(path, "{\"x\":1}");
        try
        {
            ProfileMigrationClient.MigrateConfig(layout);
            byte[] snapshot = File.ReadAllBytes(path);

            MigrationResult second = ProfileMigrationClient.MigrateConfig(layout);

            Assert.Equal("already_migrated", second.Status);
            Assert.Equal(snapshot, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFileIsSkipped()
    {
        PortableLayout layout = CreateLayout(out string root);
        try
        {
            MigrationResult result = ProfileMigrationClient.MigrateConfig(layout);
            Assert.Equal("skipped", result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NewerSchemaIsSkipped()
    {
        PortableLayout layout = CreateLayout(out string root);
        string path = layout.ConfigFile;
        File.WriteAllText(path, "{\"schema_version\":99}");
        try
        {
            MigrationResult result = ProfileMigrationClient.MigrateConfig(layout);
            Assert.Equal("skipped", result.Status);
            Assert.Contains("newer", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptJsonIsSkippedWithoutWriting()
    {
        PortableLayout layout = CreateLayout(out string root);
        string path = layout.ConfigFile;
        File.WriteAllText(path, "{ broken");
        try
        {
            MigrationResult result = ProfileMigrationClient.MigrateConfig(layout);
            Assert.Equal("skipped", result.Status);
            Assert.Equal("{ broken", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PortableLayout CreateLayout(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"vibeocr-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(root, "VibeOCR.Next.exe"),
            "production");
        layout.EnsurePortableState();
        return layout;
    }
}
