using System.Diagnostics;
using VibeOCR.Bootstrapper;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class LegacyVelopackStateMigrationTests
{
    [Fact]
    public void UpdatedHookMovesTheSingleLegacyStateBackupToStableRoot()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(
            root,
            "packages",
            "VelopackTemp",
            "tmp_backup",
            "state");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "fixture");
        try
        {
            LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current"));

            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "fixture",
                File.ReadAllText(Path.Combine(root, "state", "config", "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdatedHookMergesIntoAStableRootWhenTopLevelEntriesDoNotConflict()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(
            root,
            "packages",
            "VelopackTemp",
            "tmp_backup",
            "state");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "fixture");
        Directory.CreateDirectory(Path.Combine(root, "state", "config"));
        File.WriteAllText(Path.Combine(root, "state", "config", "new-default.json"), "default");
        Directory.CreateDirectory(Path.Combine(root, "state", "logs"));
        File.WriteAllText(Path.Combine(root, "state", "logs", "bootstrapper.log"), "new log");
        try
        {
            LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current"));

            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "fixture",
                File.ReadAllText(Path.Combine(root, "state", "config", "settings.json")));
            Assert.Equal(
                "default",
                File.ReadAllText(Path.Combine(root, "state", "config", "new-default.json")));
            Assert.Equal(
                "new log",
                File.ReadAllText(Path.Combine(root, "state", "logs", "bootstrapper.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdatedHookRejectsConflictingStableRootEntriesBeforeMovingAnything()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(root, "packages", "tmp_backup", "state");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "legacy");
        Directory.CreateDirectory(Path.Combine(root, "state", "config"));
        File.WriteAllText(Path.Combine(root, "state", "config", "settings.json"), "stable");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current")));

            Assert.Equal(
                "legacy",
                File.ReadAllText(Path.Combine(source, "config", "settings.json")));
            Assert.Equal(
                "stable",
                File.ReadAllText(Path.Combine(root, "state", "config", "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupResumesAnInterruptedRecoveryMergeWithoutOverwritingLegacyState()
    {
        string root = CreatePortableRoot();
        string target = Path.Combine(root, "state");
        string recovery = Path.Combine(root, ".legacy-state-migration");
        Directory.CreateDirectory(Path.Combine(target, "config"));
        File.WriteAllText(Path.Combine(target, "config", "settings.json"), "legacy");
        Directory.CreateDirectory(Path.Combine(recovery, "logs"));
        File.WriteAllText(Path.Combine(recovery, "logs", "bootstrapper.log"), "new log");
        try
        {
            LegacyVelopackStateMigration.Resume(Path.Combine(root, "current"));

            Assert.False(Directory.Exists(recovery));
            Assert.Equal(
                "legacy",
                File.ReadAllText(Path.Combine(target, "config", "settings.json")));
            Assert.Equal(
                "new log",
                File.ReadAllText(Path.Combine(target, "logs", "bootstrapper.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupResumesBetweenDirectoryMovesByPromotingTheLegacySourceFirst()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(root, "packages", "tmp_backup", "state");
        string recovery = Path.Combine(root, ".legacy-state-migration");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "legacy");
        Directory.CreateDirectory(Path.Combine(recovery, "logs"));
        File.WriteAllText(Path.Combine(recovery, "logs", "bootstrapper.log"), "new log");
        try
        {
            LegacyVelopackStateMigration.Resume(Path.Combine(root, "current"));

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(recovery));
            Assert.Equal(
                "legacy",
                File.ReadAllText(Path.Combine(root, "state", "config", "settings.json")));
            Assert.Equal(
                "new log",
                File.ReadAllText(Path.Combine(root, "state", "logs", "bootstrapper.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdatedHookRejectsSourceDescendantJunctionWithoutChangingOutsideData()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(root, "packages", "tmp_backup", "state");
        string outside = Path.Combine(Path.GetTempPath(), $"vibeocr-legacy-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "settings.json"), "outside");
        string link = Path.Combine(source, "config");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current")));

            Assert.True(Directory.Exists(source));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "settings.json")));
        }
        finally
        {
            DeleteJunction(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void UpdatedHookRejectsTargetDescendantJunctionWithoutMovingLegacyState()
    {
        string root = CreatePortableRoot();
        string source = Path.Combine(root, "packages", "tmp_backup", "state");
        Directory.CreateDirectory(Path.Combine(source, "config"));
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "legacy");
        string outside = Path.Combine(Path.GetTempPath(), $"vibeocr-legacy-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(root, "state"));
        string link = Path.Combine(root, "state", "logs");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current")));

            Assert.Equal(
                "legacy",
                File.ReadAllText(Path.Combine(source, "config", "settings.json")));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            DeleteJunction(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void UpdatedHookRejectsAmbiguousLegacyStateBackups()
    {
        string root = CreatePortableRoot();
        Directory.CreateDirectory(Path.Combine(root, "packages", "tmp_one", "state"));
        Directory.CreateDirectory(Path.Combine(
            root,
            "packages",
            "VelopackTemp",
            "tmp_two",
            "state"));
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                LegacyVelopackStateMigration.Migrate(Path.Combine(root, "current")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePortableRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-legacy-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "current"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        File.WriteAllText(Path.Combine(root, ".portable"), "");
        File.WriteAllText(Path.Combine(root, "Update.exe"), "fixture");
        return root;
    }

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start mklink.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to create junction: {error}");
        }
    }

    private static void DeleteJunction(string link)
    {
        if (Directory.Exists(link) &&
            File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(link);
        }
    }
}
