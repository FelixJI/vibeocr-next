using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VibeOCR.ProductLayout;

internal static class DotNetRuntimeProbe
{
  internal static string? FindVersion(string? programFiles = null)
  {
    string root = Path.Combine(
        programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "dotnet", "shared", "Microsoft.NETCore.App");
    if (!Directory.Exists(root))
    {
      return null;
    }

    return Directory.EnumerateDirectories(root)
        .Select(Path.GetFileName)
        .Where(value => Version.TryParse(value, out Version? version) && version!.Major == 10)
        .OrderByDescending(value => Version.Parse(value!))
        .FirstOrDefault();
  }
}

// #321：随包 .NET 启动闭包与 runtimeconfig 语义，Bootstrapper 与 Platform
// 共用同一解析（net472 兼容，无新依赖）；损坏按缺失报修，不回退系统运行库。
internal static class BundledRuntimeConfig
{
  internal static readonly string[] ClrStartupFiles =
  {
      "hostfxr.dll",
      "hostpolicy.dll",
      "coreclr.dll",
      "System.Private.CoreLib.dll",
  };

  internal static IReadOnlyList<string> MissingClrStartupFiles(string appRoot)
  {
    if (string.IsNullOrWhiteSpace(appRoot))
    {
      throw new ArgumentException("App root is required.", nameof(appRoot));
    }

    List<string> missing = new();
    foreach (string name in ClrStartupFiles)
    {
      if (!File.Exists(Path.Combine(appRoot, name)))
      {
        missing.Add(name);
      }
    }

    return missing;
  }

  // 返回随包 Microsoft.NETCore.App 版本；文件缺失、语法/结构损坏或缺少该
  // 框架时返回 null，调用方按随包 .NET 不可用显式报修。
  internal static string? FindNetCoreAppVersion(string runtimeConfigPath)
  {
    RuntimeConfigDocument? document;
    try
    {
      byte[] json = System.Text.Encoding.UTF8.GetBytes(
          File.ReadAllText(runtimeConfigPath));
      var serializer = new DataContractJsonSerializer(typeof(RuntimeConfigDocument));
      using var stream = new MemoryStream(json);
      document = (RuntimeConfigDocument?)serializer.ReadObject(stream);
    }
    catch (Exception error) when (
        error is IOException or UnauthorizedAccessException or SerializationException)
    {
      return null;
    }

    FrameworkDocument[]? frameworks = document?.RuntimeOptions?.IncludedFrameworks;
    if (document?.RuntimeOptions?.HasFrameworkReference == true ||
        frameworks is null || frameworks.Any(framework =>
        framework is null || string.IsNullOrWhiteSpace(framework.Name) ||
        !Version.TryParse(framework.Version, out Version? version) ||
        version!.Build < 0 || version.Revision >= 0))
    {
      return null;
    }

    return frameworks.FirstOrDefault(framework =>
            string.Equals(framework.Name, "Microsoft.NETCore.App", StringComparison.Ordinal) &&
            Version.Parse(framework.Version!).Major == 10)
        ?.Version;
  }

  [DataContract]
  private sealed class RuntimeConfigDocument
  {
    [DataMember(Name = "runtimeOptions")]
    public RuntimeOptionsDocument? RuntimeOptions { get; set; }
  }

  [DataContract]
  private sealed class RuntimeOptionsDocument
  {
    public bool HasFrameworkReference { get; private set; }

    // The host rejects framework-dependent fields alongside includedFrameworks, even null.
    [DataMember(Name = "framework")]
    private object? Framework { get => null; set => HasFrameworkReference = true; }

    [DataMember(Name = "frameworks")]
    private object? Frameworks { get => null; set => HasFrameworkReference = true; }

    [DataMember(Name = "includedFrameworks")]
    public FrameworkDocument[]? IncludedFrameworks { get; set; }
  }

  [DataContract]
  private sealed class FrameworkDocument
  {
    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "version")]
    public string? Version { get; set; }
  }
}
