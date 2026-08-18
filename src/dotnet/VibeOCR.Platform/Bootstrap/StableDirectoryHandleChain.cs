using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// Pins each directory from the install root through a target parent and owns
/// handle-relative temporary-file creation and promotion. Path-based I/O never
/// resumes after the directory chain has been validated.
/// </summary>
internal sealed class StableDirectoryHandleChain : IDisposable
{
  private const uint FileReadAttributes = 0x00000080;
  private const uint FileTraverse = 0x00000020;
  private const uint FileShareRead = 0x00000001;
  private const uint FileShareWrite = 0x00000002;
  private const uint OpenExisting = 3;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const int FileAttributeTagInfoClass = 9;
  private const int FileIdInfoClass = 18;

  private const uint GenericRead = 0x80000000;
  private const uint GenericWrite = 0x40000000;
  private const uint Delete = 0x00010000;
  private const uint Synchronize = 0x00100000;
  private const uint FileAttributeNormal = 0x00000080;
  private const uint FileAttributeDirectory = 0x00000010;
  private const uint FileCreate = 2;
  private const uint FileOpenIf = 3;
  private const uint FileDirectoryFile = 0x00000001;
  private const uint FileWriteThrough = 0x00000002;
  private const uint FileSynchronousIoNonAlert = 0x00000020;
  private const uint FileNonDirectoryFile = 0x00000040;
  private const uint ObjectCaseInsensitive = 0x00000040;
  private const int FileRenameInformationClass = 10;
  private const int FileDispositionInfoClass = 4;

  private readonly List<SafeFileHandle> _handles = [];
  private readonly string _installRoot;
  private readonly string _permittedRoot;
  private readonly string _targetDirectory;
  private string _parentFinalPath = string.Empty;
  private DirectoryIdentity _parentIdentity;

  private StableDirectoryHandleChain(
      string installRoot,
      string permittedRoot,
      string targetDirectory)
  {
    _installRoot = installRoot;
    _permittedRoot = permittedRoot;
    _targetDirectory = targetDirectory;
  }

  private SafeFileHandle ParentHandle => _handles[^1];

  public static StableDirectoryHandleChain Open(
      string installRoot,
      string permittedRoot,
      string targetDirectory,
      string targetFileName)
  {
    string install = NormalizeDirectory(installRoot);
    string permitted = NormalizeDirectory(permittedRoot);
    string directory = NormalizeDirectory(targetDirectory);
    ValidateFileName(targetFileName);
    if (!IsDescendantOrSelf(install, permitted)
        || !IsDescendantOrSelf(permitted, directory))
    {
      throw new PortableLayoutException("目标目录不在允许的 Portable 根目录内。");
    }

    var chain = new StableDirectoryHandleChain(install, permitted, directory);
    try
    {
      string current = install;
      string installFinal = chain.OpenAndHold(current);
      string parentFinal = installFinal;
      string? permittedFinal = string.Equals(
          current,
          permitted,
          StringComparison.OrdinalIgnoreCase)
              ? installFinal
              : null;
      string relative = Path.GetRelativePath(install, directory);
      if (relative != ".")
      {
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
          current = Path.Combine(current, segment);
          string currentFinal = chain.OpenAndHold(current);
          if (!IsStrictDescendant(parentFinal, currentFinal))
          {
            throw new PortableLayoutException(
                $"Portable 目录段的最终路径脱离父目录：{current}。");
          }
          parentFinal = currentFinal;
          if (string.Equals(current, permitted, StringComparison.OrdinalIgnoreCase))
          {
            permittedFinal = currentFinal;
          }
        }
      }

      if (permittedFinal is null)
      {
        throw new PortableLayoutException(
            "无法在安全目录句柄链中定位允许的 Portable 根目录。");
      }
      string targetFinal = Path.Combine(parentFinal, targetFileName);
      if (!IsStrictDescendant(installFinal, targetFinal)
          || !IsStrictDescendant(permittedFinal, targetFinal))
      {
        throw new PortableLayoutException(
            "目标文件的最终路径不在安装目录和允许的 Portable 根目录内。");
      }

      chain._parentFinalPath = parentFinal;
      chain._parentIdentity = GetDirectoryIdentity(chain.ParentHandle);
      return chain;
    }
    catch
    {
      chain.Dispose();
      throw;
    }
  }

  public static void EnsureDirectory(
      string installRoot,
      string targetDirectory,
      Action? beforeFinalSegmentOpen)
  {
    string install = NormalizeDirectory(installRoot);
    string target = NormalizeDirectory(targetDirectory);
    if (!IsDescendantOrSelf(install, target))
    {
      throw new PortableLayoutException("目标目录不在 Portable 安装根目录内。");
    }

    using var chain = new StableDirectoryHandleChain(install, install, target);
    string current = install;
    string parentFinal = chain.OpenAndHold(current);
    string relative = Path.GetRelativePath(install, target);
    string[] segments = relative == "."
        ? []
        : relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    for (int index = 0; index < segments.Length; index++)
    {
      string segment = segments[index];
      if (index == segments.Length - 1)
      {
        beforeFinalSegmentOpen?.Invoke();
      }
      current = Path.Combine(current, segment);
      string currentFinal = chain.OpenOrCreateRelativeDirectory(segment, current);
      if (!IsStrictDescendant(parentFinal, currentFinal))
      {
        throw new PortableLayoutException(
            $"Portable 目录创建期间父目录已被替换：{current}。");
      }
      parentFinal = currentFinal;
    }

    chain._parentFinalPath = parentFinal;
    chain._parentIdentity = GetDirectoryIdentity(chain.ParentHandle);
    chain.VerifyLexicalParentIdentity(".directory-identity");
  }

  public void WriteFileAtomically(
      string targetFileName,
      byte[] contents,
      Action? beforeFinalOpen)
  {
    ArgumentNullException.ThrowIfNull(contents);
    ValidateFileName(targetFileName);
    beforeFinalOpen?.Invoke();

    string temporaryName = $".{targetFileName}.{Guid.NewGuid():N}.tmp";
    using SafeFileHandle temporary = CreateRelativeFile(temporaryName);
    bool promoted = false;
    try
    {
      using (var borrowed = new SafeFileHandle(
          temporary.DangerousGetHandle(),
          ownsHandle: false))
      using (var stream = new FileStream(
          borrowed,
          FileAccess.Write,
          bufferSize: 4096,
          isAsync: false))
      {
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
      }

      PromoteRelativeFile(temporary, targetFileName);
      promoted = true;
    }
    catch (Exception operationError)
    {
      if (!promoted)
      {
        try
        {
          DeleteRelativeFile(temporary);
        }
        catch (Exception cleanupError)
        {
          throw new PortableLayoutException(
              $"相对临时文件写入失败且无法清理：{operationError.Message}; "
              + $"cleanup: {cleanupError.Message}");
        }
      }
      throw;
    }

    VerifyLexicalParentIdentity(targetFileName);
  }

  public void ProbeWritableDirectory(string probeFileName, Action? beforeProbeOpen)
  {
    ValidateFileName(probeFileName);
    beforeProbeOpen?.Invoke();

    byte[] payload = "probe"u8.ToArray();
    string renamedName = probeFileName + ".renamed";
    using SafeFileHandle probe = CreateRelativeFile(
        probeFileName,
        GenericRead | GenericWrite | Delete | Synchronize);
    bool deletionRequested = false;
    try
    {
      using (var borrowed = new SafeFileHandle(
          probe.DangerousGetHandle(),
          ownsHandle: false))
      using (var stream = new FileStream(
          borrowed,
          FileAccess.ReadWrite,
          bufferSize: 4096,
          isAsync: false))
      {
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        byte[] readBack = new byte[payload.Length];
        stream.ReadExactly(readBack);
        if (!readBack.AsSpan().SequenceEqual(payload))
        {
          throw new IOException("probe content mismatch");
        }
      }

      PromoteRelativeFile(probe, renamedName);
      DeleteRelativeFile(probe);
      deletionRequested = true;
    }
    catch (Exception operationError)
    {
      if (!deletionRequested)
      {
        try
        {
          DeleteRelativeFile(probe);
        }
        catch (Exception cleanupError)
        {
          throw new PortableLayoutException(
              $"可写探针失败且无法清理：{operationError.Message}; "
              + $"cleanup: {cleanupError.Message}");
        }
      }
      throw;
    }

    VerifyLexicalParentIdentity(probeFileName);
  }

  private void VerifyLexicalParentIdentity(string targetFileName)
  {
    StableDirectoryHandleChain reopened;
    try
    {
      reopened = Open(
          _installRoot,
          _permittedRoot,
          _targetDirectory,
          targetFileName);
    }
    catch (PortableLayoutException error)
    {
      throw new PortableLayoutException(
          $"原子写入期间目标父目录已被替换，操作已 fail closed：{error.Message}");
    }

    using (reopened)
    {
      if (!string.Equals(
              _parentFinalPath,
              reopened._parentFinalPath,
              StringComparison.OrdinalIgnoreCase)
          || _parentIdentity != reopened._parentIdentity)
      {
        throw new PortableLayoutException(
            "原子写入期间目标父目录已被替换，操作已 fail closed。");
      }
    }
  }

  private SafeFileHandle CreateRelativeFile(string fileName) =>
      CreateRelativeFile(fileName, GenericWrite | Delete | Synchronize);

  private SafeFileHandle CreateRelativeFile(string fileName, uint desiredAccess)
  {
    ValidateFileName(fileName);
    using var nativeName = new NativeUnicodeString(fileName);
    var attributes = new ObjectAttributes
    {
      Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
      RootDirectory = ParentHandle.DangerousGetHandle(),
      ObjectName = nativeName.Structure,
      Attributes = ObjectCaseInsensitive,
    };
    int status = NtCreateFile(
        out SafeFileHandle handle,
        desiredAccess,
        ref attributes,
        out _,
        nint.Zero,
        FileAttributeNormal,
        0,
        FileCreate,
        FileWriteThrough | FileSynchronousIoNonAlert | FileNonDirectoryFile,
        nint.Zero,
        0);
    if (status < 0)
    {
      handle?.Dispose();
      throw NtError(status, $"无法在已固定目录中创建临时文件 {fileName}");
    }
    return handle;
  }

  private void PromoteRelativeFile(SafeFileHandle temporary, string targetFileName)
  {
    ValidateFileName(targetFileName);
    byte[] name = Encoding.Unicode.GetBytes(targetFileName);
    int rootOffset = IntPtr.Size == 8 ? 8 : 4;
    int lengthOffset = rootOffset + IntPtr.Size;
    int nameOffset = lengthOffset + sizeof(uint);
    int structureSize = IntPtr.Size == 8 ? 24 : 16;
    int size = checked(structureSize + name.Length);
    nint buffer = Marshal.AllocHGlobal(size);
    try
    {
      Marshal.Copy(new byte[size], 0, buffer, size);
      Marshal.WriteByte(buffer, 0, 1);
      Marshal.WriteIntPtr(buffer, rootOffset, ParentHandle.DangerousGetHandle());
      Marshal.WriteInt32(buffer, lengthOffset, name.Length);
      Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
      int status = NtSetInformationFile(
          temporary,
          out _,
          buffer,
          checked((uint)size),
          FileRenameInformationClass);
      if (status < 0)
      {
        throw NtError(status, $"无法在已固定目录中提升临时文件为 {targetFileName}");
      }
    }
    finally
    {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static void DeleteRelativeFile(SafeFileHandle temporary)
  {
    var disposition = new FileDispositionInfo { DeleteFile = 1 };
    if (!SetFileDispositionByHandle(
        temporary,
        FileDispositionInfoClass,
        ref disposition,
        (uint)Marshal.SizeOf<FileDispositionInfo>()))
    {
      throw Win32Error("无法删除失败的相对临时文件");
    }
  }

  private string OpenOrCreateRelativeDirectory(string segment, string displayPath)
  {
    ValidateFileName(segment);
    using var nativeName = new NativeUnicodeString(segment);
    var attributes = new ObjectAttributes
    {
      Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
      RootDirectory = ParentHandle.DangerousGetHandle(),
      ObjectName = nativeName.Structure,
      Attributes = ObjectCaseInsensitive,
    };
    int status = NtCreateFile(
        out SafeFileHandle handle,
        FileReadAttributes | FileTraverse | Synchronize,
        ref attributes,
        out _,
        nint.Zero,
        FileAttributeDirectory,
        FileShareRead | FileShareWrite,
        FileOpenIf,
        FileDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
        nint.Zero,
        0);
    if (status < 0)
    {
      handle?.Dispose();
      throw NtError(status, $"无法安全创建或打开 Portable 目录 {displayPath}");
    }
    return VerifyAndHoldDirectory(handle, displayPath);
  }

  private string OpenAndHold(string path)
  {
    SafeFileHandle handle = CreateFileW(
        path,
        FileReadAttributes,
        FileShareRead | FileShareWrite,
        nint.Zero,
        OpenExisting,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        nint.Zero);
    if (handle.IsInvalid)
    {
      int error = Marshal.GetLastWin32Error();
      handle.Dispose();
      throw new PortableLayoutException(
          $"无法安全打开 Portable 目录 {path}：{new Win32Exception(error).Message}");
    }

    return VerifyAndHoldDirectory(handle, path);
  }

  private string VerifyAndHoldDirectory(SafeFileHandle handle, string path)
  {
    try
    {
      if (!GetFileAttributeTagInformation(
          handle,
          FileAttributeTagInfoClass,
          out FileAttributeTagInfo information,
          (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      var attributes = (FileAttributes)information.FileAttributes;
      if (!attributes.HasFlag(FileAttributes.Directory))
      {
        throw new PortableLayoutException($"Portable 路径段不是目录：{path}。");
      }
      if (attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new PortableLayoutException($"Portable 目录链包含重解析点：{path}。");
      }
      string finalPath = GetFinalPath(handle);
      _handles.Add(handle);
      return finalPath;
    }
    catch (Win32Exception error)
    {
      handle.Dispose();
      throw new PortableLayoutException(
          $"无法核验 Portable 目录 {path}：{error.Message}");
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static DirectoryIdentity GetDirectoryIdentity(SafeFileHandle handle)
  {
    if (!GetFileIdInformation(
        handle,
        FileIdInfoClass,
        out FileIdInfo information,
        (uint)Marshal.SizeOf<FileIdInfo>()))
    {
      throw Win32Error("无法读取 Portable 父目录 identity");
    }
    return new DirectoryIdentity(
        information.VolumeSerialNumber,
        information.FileId.Low,
        information.FileId.High);
  }

  private static string GetFinalPath(SafeFileHandle handle)
  {
    var buffer = new StringBuilder(512);
    while (true)
    {
      uint length = GetFinalPathNameByHandleW(
          handle,
          buffer,
          checked((uint)buffer.Capacity),
          0);
      if (length == 0)
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      if (length < buffer.Capacity)
      {
        return NormalizeDirectory(StripExtendedPrefix(buffer.ToString()));
      }
      buffer.Clear();
      buffer.EnsureCapacity(checked((int)length + 1));
    }
  }

  private static void ValidateFileName(string fileName)
  {
    if (string.IsNullOrWhiteSpace(fileName)
        || fileName is "." or ".."
        || Path.GetFileName(fileName) != fileName)
    {
      throw new PortableLayoutException("目标文件名必须是单个非空路径段。");
    }
  }

  private static string StripExtendedPrefix(string path)
  {
    const string extendedUnc = @"\\?\UNC\";
    const string extended = @"\\?\";
    if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
    {
      return @"\\" + path[extendedUnc.Length..];
    }
    return path.StartsWith(extended, StringComparison.Ordinal)
        ? path[extended.Length..]
        : path;
  }

  private static bool IsStrictDescendant(string root, string candidate) =>
      !string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
      && IsDescendantOrSelf(root, candidate);

  private static bool IsDescendantOrSelf(string root, string candidate)
  {
    string relative = Path.GetRelativePath(root, candidate);
    return !Path.IsPathRooted(relative)
        && relative is not ".."
        && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private static string NormalizeDirectory(string path) =>
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

  private static PortableLayoutException NtError(int status, string context)
  {
    int error = checked((int)RtlNtStatusToDosError(status));
    return new PortableLayoutException($"{context}：{new Win32Exception(error).Message}");
  }

  private static PortableLayoutException Win32Error(string context)
  {
    int error = Marshal.GetLastWin32Error();
    return new PortableLayoutException($"{context}：{new Win32Exception(error).Message}");
  }

  public void Dispose()
  {
    for (int index = _handles.Count - 1; index >= 0; index--)
    {
      _handles[index].Dispose();
    }
    _handles.Clear();
  }

  [DllImport("ntdll.dll")]
  private static extern int NtCreateFile(
      out SafeFileHandle fileHandle,
      uint desiredAccess,
      ref ObjectAttributes objectAttributes,
      out IoStatusBlock ioStatusBlock,
      nint allocationSize,
      uint fileAttributes,
      uint shareAccess,
      uint createDisposition,
      uint createOptions,
      nint eaBuffer,
      uint eaLength);

  [DllImport("ntdll.dll")]
  private static extern uint RtlNtStatusToDosError(int status);

  [DllImport("ntdll.dll")]
  private static extern int NtSetInformationFile(
      SafeFileHandle fileHandle,
      out IoStatusBlock ioStatusBlock,
      nint fileInformation,
      uint length,
      int fileInformationClass);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(
      string fileName,
      uint desiredAccess,
      uint shareMode,
      nint securityAttributes,
      uint creationDisposition,
      uint flagsAndAttributes,
      nint templateFile);

  [DllImport(
      "kernel32.dll",
      EntryPoint = "GetFileInformationByHandleEx",
      SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileAttributeTagInformation(
      SafeFileHandle file,
      int informationClass,
      out FileAttributeTagInfo fileInformation,
      uint bufferSize);

  [DllImport(
      "kernel32.dll",
      EntryPoint = "GetFileInformationByHandleEx",
      SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileIdInformation(
      SafeFileHandle file,
      int informationClass,
      out FileIdInfo fileInformation,
      uint bufferSize);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandleW(
      SafeFileHandle file,
      StringBuilder filePath,
      uint filePathLength,
      uint flags);

  [DllImport(
      "kernel32.dll",
      EntryPoint = "SetFileInformationByHandle",
      SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetFileDispositionByHandle(
      SafeFileHandle file,
      int informationClass,
      ref FileDispositionInfo fileInformation,
      uint bufferSize);

  [StructLayout(LayoutKind.Sequential)]
  private struct ObjectAttributes
  {
    public uint Length;
    public nint RootDirectory;
    public nint ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoStatusBlock
  {
    public nint Status;
    public nuint Information;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct UnicodeString
  {
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  private struct FileDispositionInfo
  {
    public byte DeleteFile;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileAttributeTagInfo
  {
    public uint FileAttributes;
    public uint ReparseTag;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileIdInfo
  {
    public ulong VolumeSerialNumber;
    public FileId128 FileId;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileId128
  {
    public ulong Low;
    public ulong High;
  }

  private readonly record struct DirectoryIdentity(
      ulong VolumeSerialNumber,
      ulong FileIdLow,
      ulong FileIdHigh);

  private sealed class NativeUnicodeString : IDisposable
  {
    public NativeUnicodeString(string value)
    {
      int length = checked(value.Length * sizeof(char));
      int maximumLength = checked(length + sizeof(char));
      if (maximumLength > ushort.MaxValue)
      {
        throw new PortableLayoutException("相对文件名超过 NT UnicodeString 上限。");
      }

      Buffer = Marshal.StringToHGlobalUni(value);
      try
      {
        Structure = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var native = new UnicodeString
        {
          Length = (ushort)length,
          MaximumLength = (ushort)maximumLength,
          Buffer = Buffer,
        };
        Marshal.StructureToPtr(native, Structure, fDeleteOld: false);
      }
      catch
      {
        Marshal.FreeHGlobal(Buffer);
        throw;
      }
    }

    private nint Buffer { get; }
    public nint Structure { get; }

    public void Dispose()
    {
      Marshal.FreeHGlobal(Structure);
      Marshal.FreeHGlobal(Buffer);
    }
  }
}
