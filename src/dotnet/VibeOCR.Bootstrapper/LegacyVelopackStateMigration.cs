using System;
using System.IO;
using System.Linq;

namespace VibeOCR.Bootstrapper;

internal static class LegacyVelopackStateMigration
{
    private const string RecoveryDirectoryName = ".legacy-state-migration";

    internal static void Resume(string versionedCurrentRoot)
    {
        DirectoryInfo? installDirectory = ResolvePortableInstallDirectory(versionedCurrentRoot);
        if (installDirectory is null)
        {
            return;
        }
        string target = Path.Combine(installDirectory.FullName, "state");
        string recovery = Path.Combine(installDirectory.FullName, RecoveryDirectoryName);
        if (!Directory.Exists(recovery))
        {
            return;
        }
        RejectReparseTree(recovery, "recovery");
        if (!Directory.Exists(target))
        {
            string[] interruptedSources = FindLegacySources(installDirectory);
            if (interruptedSources.Length > 1)
            {
                throw new InvalidDataException(
                    "legacy state migration found ambiguous Velopack backups");
            }
            if (interruptedSources.Length == 1)
            {
                string interruptedSource = interruptedSources[0];
                RejectReparsePoint(Path.GetDirectoryName(interruptedSource)!);
                RejectReparseTree(interruptedSource, "source");
                ValidateNoConflicts(interruptedSource, recovery);
                Directory.Move(interruptedSource, target);
                MergeWithoutOverwrite(recovery, target);
            }
            else
            {
                Directory.Move(recovery, target);
            }
            return;
        }
        RejectReparseTree(target, "target");
        MergeWithoutOverwrite(recovery, target);
    }

    internal static void Migrate(string versionedCurrentRoot)
    {
        DirectoryInfo? installDirectory = ResolvePortableInstallDirectory(versionedCurrentRoot);
        if (installDirectory is null)
        {
            return;
        }
        Resume(versionedCurrentRoot);

        string[] sources = FindLegacySources(installDirectory);
        if (sources.Length == 0)
        {
            return;
        }
        if (sources.Length != 1)
        {
            throw new InvalidDataException("legacy state migration found ambiguous Velopack backups");
        }

        string source = sources[0];
        string target = Path.Combine(installDirectory.FullName, "state");
        string recovery = Path.Combine(installDirectory.FullName, RecoveryDirectoryName);
        RejectReparsePoint(Path.GetDirectoryName(source)!);
        RejectReparseTree(source, "source");
        if (!Directory.Exists(target) && !File.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }
        if (!Directory.Exists(target))
        {
            throw new InvalidDataException("legacy state migration target is not a directory");
        }
        RejectReparseTree(target, "target");
        if (Directory.Exists(recovery) || File.Exists(recovery))
        {
            throw new InvalidDataException("legacy state migration recovery path already exists");
        }
        ValidateNoConflicts(source, target);

        Directory.Move(target, recovery);
        try
        {
            Directory.Move(source, target);
        }
        catch
        {
            Directory.Move(recovery, target);
            throw;
        }
        MergeWithoutOverwrite(recovery, target);
    }

    private static string[] FindLegacySources(DirectoryInfo installDirectory)
    {
        string packages = Path.Combine(installDirectory.FullName, "packages");
        if (!Directory.Exists(packages))
        {
            return Array.Empty<string>();
        }
        RejectReparsePoint(packages);
        string velopackTemp = Path.Combine(packages, "VelopackTemp");
        string[] searchRoots = Directory.Exists(velopackTemp)
            ? new[] { packages, velopackTemp }
            : new[] { packages };
        if (Directory.Exists(velopackTemp))
        {
            RejectReparsePoint(velopackTemp);
        }
        return searchRoots
            .SelectMany(root => Directory.EnumerateDirectories(
                root,
                "tmp_*",
                SearchOption.TopDirectoryOnly))
            .Where(path => Directory.Exists(Path.Combine(path, "state")))
            .Select(path => Path.Combine(path, "state"))
            .ToArray();
    }

    private static DirectoryInfo? ResolvePortableInstallDirectory(string versionedCurrentRoot)
    {
        string current = Path.GetFullPath(versionedCurrentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo currentDirectory = new(current);
        DirectoryInfo? installDirectory = currentDirectory.Parent;
        if (!currentDirectory.Name.Equals("current", StringComparison.OrdinalIgnoreCase) ||
            installDirectory is null ||
            !File.Exists(Path.Combine(installDirectory.FullName, ".portable")) ||
            !File.Exists(Path.Combine(installDirectory.FullName, "Update.exe")))
        {
            return null;
        }
        return installDirectory;
    }

    private static void MergeWithoutOverwrite(string source, string target)
    {
        ValidateNoConflicts(source, target);
        string[] sourceDirectories = Directory.GetDirectories(
            source,
            "*",
            SearchOption.AllDirectories);
        string[] sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        foreach (string directory in sourceDirectories.OrderBy(path => path.Length))
        {
            Directory.CreateDirectory(Path.Combine(target, GetDescendantPath(source, directory)));
        }
        foreach (string file in sourceFiles)
        {
            File.Move(file, Path.Combine(target, GetDescendantPath(source, file)));
        }
        Directory.Delete(source, recursive: true);
    }

    private static void ValidateNoConflicts(string source, string target)
    {
        string[] sourceDirectories = Directory.GetDirectories(source, "*", SearchOption.AllDirectories);
        string[] sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        if (sourceDirectories.Any(directory =>
                File.Exists(Path.Combine(target, GetDescendantPath(source, directory)))) ||
            sourceFiles.Any(file =>
            {
                string destination = Path.Combine(target, GetDescendantPath(source, file));
                return Directory.Exists(destination) || File.Exists(destination);
            }))
        {
            throw new InvalidDataException("legacy state migration target contains a conflicting entry");
        }
    }

    private static string GetDescendantPath(string root, string path)
    {
        return path.Substring(root.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("legacy state migration path is a reparse point");
        }
    }

    private static void RejectReparseTree(string path, string role)
    {
        RejectReparsePoint(path);
        if (Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
            .Any(entry => File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidDataException($"legacy state migration {role} contains a reparse point");
        }
    }
}
