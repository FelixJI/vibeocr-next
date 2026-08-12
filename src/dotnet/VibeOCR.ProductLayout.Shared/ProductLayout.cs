using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VibeOCR.ProductLayout;

public sealed class ResolvedProductLayout
{
    public const string DescriptorRelativePath = @"app\metadata\product-layout.json";

    private ResolvedProductLayout(string installRoot)
    {
        InstallRoot = installRoot;
        PublicEntry = Path.Combine(installRoot, "VibeOCR.exe");
        AppRoot = Path.Combine(installRoot, "app");
        AppEntry = Path.Combine(AppRoot, "VibeOCR.WinUI.exe");
        WebAssetsRoot = Path.Combine(AppRoot, "WebAssets");
        Updater = Path.Combine(AppRoot, "tools", "updater.exe");
        RuntimeRoot = Path.Combine(installRoot, "runtime");
        RuntimeManifest = Path.Combine(RuntimeRoot, "backend", "runtime-manifest.json");
        RuntimeInstaller = Path.Combine(
            RuntimeRoot,
            "installer",
            "vibeocr-runtime-installer.exe");
        MetadataRoot = Path.Combine(AppRoot, "metadata");
        ComponentLock = Path.Combine(MetadataRoot, "component-lock.json");
        ComponentIdentities = Path.Combine(MetadataRoot, "component-identities.json");
        ReleaseManifest = Path.Combine(MetadataRoot, "product-release-manifest.json");
        Descriptor = Path.Combine(installRoot, DescriptorRelativePath);
    }

    public string InstallRoot { get; }
    public string PublicEntry { get; }
    public string AppRoot { get; }
    public string AppEntry { get; }
    public string WebAssetsRoot { get; }
    public string Updater { get; }
    public string RuntimeRoot { get; }
    public string RuntimeManifest { get; }
    public string RuntimeInstaller { get; }
    public string MetadataRoot { get; }
    public string ComponentLock { get; }
    public string ComponentIdentities { get; }
    public string ReleaseManifest { get; }
    public string Descriptor { get; }

    public static ResolvedProductLayout Open(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new ArgumentException("Install root is required.", nameof(installRoot));
        }

        string root = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string descriptorPath = Path.Combine(root, DescriptorRelativePath);
        ProductLayoutDocument document;
        try
        {
            byte[] json = Encoding.UTF8.GetBytes(File.ReadAllText(descriptorPath));
            var serializer = new DataContractJsonSerializer(typeof(ProductLayoutDocument));
            using var stream = new MemoryStream(json);
            document = (ProductLayoutDocument?)serializer.ReadObject(stream)
                ?? throw new InvalidDataException("layout.invalid-descriptor: document is empty");
        }
        catch (Exception error) when (
            error is IOException or SerializationException or InvalidCastException)
        {
            throw new InvalidDataException(
                "layout.missing-entry: product layout descriptor is unavailable",
                error);
        }

        Validate(document);
        var layout = new ResolvedProductLayout(root);
        layout.ValidateTree();
        return layout;
    }

    private void ValidateTree()
    {
        var expectedRoot = new HashSet<string>(StringComparer.Ordinal)
        {
            "VibeOCR.exe",
            "Velopack.dll",
            "LICENSE",
            "CHANGELOG.md",
            "app",
            "runtime",
        };
        var actualRoot = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(InstallRoot))
        {
            actualRoot.Add(Path.GetFileName(path));
        }
        if (!actualRoot.SetEquals(expectedRoot))
        {
            throw new InvalidDataException("layout.root-conflict: product root is not closed");
        }

        string[] required =
        {
            PublicEntry,
            AppEntry,
            Path.Combine(AppRoot, "VibeOCR.WinUI.dll"),
            Path.Combine(AppRoot, "VibeOCR.WinUI.pri"),
            Path.Combine(AppRoot, "App.xbf"),
            Path.Combine(AppRoot, "MainWindow.xbf"),
            Path.Combine(WebAssetsRoot, "index.html"),
            Updater,
            RuntimeManifest,
            RuntimeInstaller,
            ComponentLock,
            ComponentIdentities,
            ReleaseManifest,
            Path.Combine(InstallRoot, "LICENSE"),
            Path.Combine(InstallRoot, "CHANGELOG.md"),
        };
        foreach (string path in required)
        {
            if (!File.Exists(path))
            {
                throw new InvalidDataException("layout.missing-entry: " + path);
            }
        }
    }

    private static void Validate(ProductLayoutDocument value)
    {
        if (value.SchemaVersion != 1)
        {
            throw new InvalidDataException("layout.unsupported-schema: expected schema_version 1");
        }

        if (!string.Equals(value.ProductId, "vibeocr", StringComparison.Ordinal) ||
            !string.Equals(value.PublicEntry, "VibeOCR.exe", StringComparison.Ordinal) ||
            value.Roots is null || value.App is null || value.Runtime is null ||
            value.Metadata is null || value.UserData is null)
        {
            throw new InvalidDataException("layout.product-mismatch: descriptor is not VibeOCR");
        }

        Expect(value.Roots.App, "app", "roots.app");
        Expect(value.Roots.Runtime, "runtime", "roots.runtime");
        Expect(value.Roots.Metadata, "app/metadata", "roots.metadata");
        Expect(value.App.Entry, "app/VibeOCR.WinUI.exe", "app.entry");
        Expect(value.App.WebAssets, "app/WebAssets", "app.web_assets");
        Expect(value.App.Updater, "app/tools/updater.exe", "app.updater");
        Expect(
            value.Runtime.Manifest,
            "runtime/backend/runtime-manifest.json",
            "runtime.manifest");
        Expect(
            value.Runtime.Installer,
            "runtime/installer/vibeocr-runtime-installer.exe",
            "runtime.installer");
        Expect(
            value.Metadata.ComponentLock,
            "app/metadata/component-lock.json",
            "metadata.component_lock");
        Expect(
            value.Metadata.ComponentIdentities,
            "app/metadata/component-identities.json",
            "metadata.component_identities");
        Expect(
            value.Metadata.ReleaseManifest,
            "app/metadata/product-release-manifest.json",
            "metadata.release_manifest");
        Expect(value.UserData.KnownFolder, "LocalApplicationData", "user_data.known_folder");
        Expect(value.UserData.Relative, "VibeOCR", "user_data.relative");
    }

    private static void Expect(string? actual, string expected, string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"layout.invalid-path: {field}");
        }
    }

    [DataContract]
    private sealed class ProductLayoutDocument
    {
        [DataMember(Name = "schema_version")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "product_id")]
        public string? ProductId { get; set; }

        [DataMember(Name = "public_entry")]
        public string? PublicEntry { get; set; }

        [DataMember(Name = "roots")]
        public RootDocument? Roots { get; set; }

        [DataMember(Name = "app")]
        public AppDocument? App { get; set; }

        [DataMember(Name = "runtime")]
        public RuntimeDocument? Runtime { get; set; }

        [DataMember(Name = "metadata")]
        public MetadataDocument? Metadata { get; set; }

        [DataMember(Name = "user_data")]
        public UserDataDocument? UserData { get; set; }
    }

    [DataContract]
    private sealed class RootDocument
    {
        [DataMember(Name = "app")]
        public string? App { get; set; }

        [DataMember(Name = "runtime")]
        public string? Runtime { get; set; }

        [DataMember(Name = "metadata")]
        public string? Metadata { get; set; }
    }

    [DataContract]
    private sealed class AppDocument
    {
        [DataMember(Name = "entry")]
        public string? Entry { get; set; }

        [DataMember(Name = "web_assets")]
        public string? WebAssets { get; set; }

        [DataMember(Name = "updater")]
        public string? Updater { get; set; }
    }

    [DataContract]
    private sealed class RuntimeDocument
    {
        [DataMember(Name = "manifest")]
        public string? Manifest { get; set; }

        [DataMember(Name = "installer")]
        public string? Installer { get; set; }
    }

    [DataContract]
    private sealed class MetadataDocument
    {
        [DataMember(Name = "component_lock")]
        public string? ComponentLock { get; set; }

        [DataMember(Name = "component_identities")]
        public string? ComponentIdentities { get; set; }

        [DataMember(Name = "release_manifest")]
        public string? ReleaseManifest { get; set; }
    }

    [DataContract]
    private sealed class UserDataDocument
    {
        [DataMember(Name = "known_folder")]
        public string? KnownFolder { get; set; }

        [DataMember(Name = "relative")]
        public string? Relative { get; set; }
    }
}
