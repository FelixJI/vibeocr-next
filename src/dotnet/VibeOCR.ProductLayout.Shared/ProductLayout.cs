using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeOCR.ProductLayout;

public sealed class PortableProductRoots
{
    private PortableProductRoots(string installRoot, string productRoot)
    {
        InstallRoot = installRoot;
        ProductRoot = productRoot;
    }

    public string InstallRoot { get; }
    public string ProductRoot { get; }

    public static PortableProductRoots Resolve(string productRoot)
    {
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            throw new ArgumentException("Product root is required.", nameof(productRoot));
        }

        string product = Path.GetFullPath(productRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo directory = new(product);
        DirectoryInfo? parent = directory.Parent;
        if (directory.Name.Equals("current", StringComparison.OrdinalIgnoreCase) &&
            parent is not null &&
            File.Exists(Path.Combine(parent.FullName, ".portable")) &&
            File.Exists(Path.Combine(parent.FullName, "Update.exe")) &&
            File.Exists(Path.Combine(parent.FullName, "VibeOCR.exe")))
        {
            return new PortableProductRoots(parent.FullName, product);
        }

        return new PortableProductRoots(product, product);
    }
}

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
        RuntimeRoot = Path.Combine(installRoot, "runtime");
        RuntimeManifest = Path.Combine(RuntimeRoot, "backend", "runtime-manifest.json");
        RuntimePython = Path.Combine(
            RuntimeRoot,
            "env", "runtime-env", "python.exe");
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
    public string RuntimeRoot { get; }
    public string RuntimeManifest { get; }
    public string RuntimePython { get; }
    public string MetadataRoot { get; }
    public string ComponentLock { get; }
    public string ComponentIdentities { get; }
    public string ReleaseManifest { get; }
    public string Descriptor { get; }

    /// <summary>#321：随包 Windows App SDK 启动闭包（缺 UI.Xaml 时 apphost
    /// 异步退出无法报修；Bootstrapper 与 Platform 共用保持一致）。</summary>
    public static readonly string[] WindowsAppRuntimeStartupFiles =
    {
        "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.UI.Xaml.dll",
    };

    /// <summary>W17-2：校验已知随包原生启动库的 PE 头部与 COFF 声明范围
    /// 语义：DOS 头、NT 签名、AMD64 机器、DLL+可执行特征、PE32+ 可选头
    /// magic，以及 COFF 声明的可选头（≥PE32+ 标准 112 字节）与节表范围
    /// （每节 40 字节）必须落在实际文件内；偏移加法全部用 long，
    /// 只做有界小读取。能力边界：这是头部及声明范围的语义检查，
    /// 不是节内容/导入/完整可运行验证，也不加载、不执行、不哈希；
    /// 目的是让截断/错误架构/非 DLL 的已知启动库在 apphost 前进入产品
    /// 报修，而不是放行为 WinUI 原生 0xC000027B 退出。</summary>
    public static void EnsureValidX64DllImageHeader(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Image path is required.", nameof(path));
        }

        InvalidDataException Fail(string reason) => new(
            "webview2.invalid-image: 随包 WebView2 启动库不是有效的 Windows x64 DLL 映像（"
            + reason + "）：" + path);
        byte[] dosHeader = new byte[0x40];
        byte[] ntHeaders = new byte[26];
        int peOffset;
        long streamLength;
        try
        {
            using FileStream stream = File.OpenRead(path);
            streamLength = stream.Length;
            if (streamLength < dosHeader.Length)
            {
                throw Fail("映像截断");
            }
            ReadExactly(stream, dosHeader);
            if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                throw Fail("DOS 头无效");
            }
            peOffset = BitConverter.ToInt32(dosHeader, 0x3C);
            if (peOffset < 0x40 || (long)peOffset + ntHeaders.Length > streamLength)
            {
                throw Fail("映像截断");
            }
            stream.Seek(peOffset, SeekOrigin.Begin);
            ReadExactly(stream, ntHeaders);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "webview2.invalid-image: 无法读取随包 WebView2 启动库：" + path, error);
        }
        if (ntHeaders[0] != (byte)'P' || ntHeaders[1] != (byte)'E' ||
            ntHeaders[2] != 0 || ntHeaders[3] != 0)
        {
            throw Fail("PE 签名无效");
        }
        if (BitConverter.ToUInt16(ntHeaders, 4) != 0x8664)
        {
            throw Fail("不是 x64 (AMD64) 映像");
        }
        ushort characteristics = BitConverter.ToUInt16(ntHeaders, 22);
        if ((characteristics & 0x2000) == 0)
        {
            throw Fail("不是 DLL 映像");
        }
        if ((characteristics & 0x0002) == 0)
        {
            throw Fail("缺少可执行映像特征");
        }
        if (BitConverter.ToUInt16(ntHeaders, 24) != 0x20B)
        {
            throw Fail("不是 PE32+ 可选头");
        }
        ushort numberOfSections = BitConverter.ToUInt16(ntHeaders, 6);
        ushort sizeOfOptionalHeader = BitConverter.ToUInt16(ntHeaders, 20);
        if (numberOfSections == 0)
        {
            throw Fail("未声明节表");
        }
        if (sizeOfOptionalHeader < 112)
        {
            throw Fail("可选头小于 PE32+ 标准头");
        }
        long optionalHeaderEnd = peOffset + 24L + sizeOfOptionalHeader;
        if (optionalHeaderEnd > streamLength)
        {
            throw Fail("映像截断：声明的可选头范围越过文件末尾");
        }
        if (optionalHeaderEnd + numberOfSections * 40L > streamLength)
        {
            throw Fail("映像截断：声明的节表范围越过文件末尾");
        }
    }

    private static void ReadExactly(FileStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int block = stream.Read(buffer, read, buffer.Length - read);
            if (block == 0)
            {
                // EOF 属 IO 语义，交给调用方统一包装读取失败诊断。
                throw new EndOfStreamException();
            }
            read += block;
        }
    }

    public static IReadOnlyList<string> MissingWindowsAppRuntimeFiles(string appRoot)
    {
        if (string.IsNullOrWhiteSpace(appRoot))
        {
            throw new ArgumentException("App root is required.", nameof(appRoot));
        }

        List<string> missing = new();
        foreach (string name in WindowsAppRuntimeStartupFiles)
        {
            if (!File.Exists(Path.Combine(appRoot, name)))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

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
        // 便携运行时允许出现的额外根条目:稳定 state 目录与运行时生成的
        // portable-layout 清单;二者允许缺失,但其它污染仍拒绝。
        var portableTolerated = new HashSet<string>(StringComparer.Ordinal)
        {
            "state",
            "portable-layout.json",
            "velopack",
        };
        var actualRoot = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(InstallRoot))
        {
            actualRoot.Add(Path.GetFileName(path));
        }
        actualRoot.Remove("sq.version");
        if (!expectedRoot.IsSubsetOf(actualRoot) ||
            actualRoot.Except(expectedRoot).Except(portableTolerated).Any())
        {
            throw new InvalidDataException("layout.root-conflict: product root is not closed");
        }
        string velopackRuntime = Path.Combine(InstallRoot, "velopack");
        if (File.Exists(velopackRuntime) ||
            (Directory.Exists(velopackRuntime) &&
             (File.GetAttributes(velopackRuntime).HasFlag(FileAttributes.ReparsePoint) ||
              Directory.EnumerateFileSystemEntries(velopackRuntime).Any(path =>
                  Directory.Exists(path) ||
                  File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ||
                  !Path.GetFileName(path).Equals(
                      "velopack_VibeOCRNext.log",
                      StringComparison.Ordinal)))))
        {
            throw new InvalidDataException("layout.root-conflict: Velopack runtime directory is invalid");
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
            RuntimeManifest,
            RuntimePython,
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
            // 必需闭包文件必须是普通文件：符号链接会把布局闭包指向
            // 产品树外，破坏“完整自包含产品”语义（Python 侧 stage 校验
            // 对全树拒绝 reparse point，这里在 C# 打开路径上同向拒绝）。
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("layout.invalid-path: 布局闭包文件是重解析点：" + path);
            }
        }
    }

    public static bool HasSafeWebView2Executable(string executable)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(executable);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "layout.invalid-path: 无法校验随包 WebView2 浏览器入口：" + executable, error);
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "layout.invalid-path: 随包 WebView2 浏览器入口是重解析点：" + executable);
        }
        return !attributes.HasFlag(FileAttributes.Directory);
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
        Expect(
            value.Runtime.Manifest,
            "runtime/backend/runtime-manifest.json",
            "runtime.manifest");
        Expect(
            value.Runtime.Python,
            "runtime/env/runtime-env/python.exe",
            "runtime.python");
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
        if (!string.IsNullOrEmpty(value.UserData.KnownFolder))
        {
            throw new InvalidDataException("layout.invalid-path: user_data.known_folder");
        }
        Expect(value.UserData.Relative, "state", "user_data.relative");
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

    }

    [DataContract]
    private sealed class RuntimeDocument
    {
        [DataMember(Name = "manifest")]
        public string? Manifest { get; set; }

        [DataMember(Name = "python")]
        public string? Python { get; set; }
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
