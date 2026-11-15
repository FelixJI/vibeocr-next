using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// Applying the minimal AppContainer permissions to the product-owned
/// WebView2 runtime directory failed. Fail closed: the app never silently
/// falls back to a system WebView2 runtime and never widens permissions.
/// </summary>
public sealed class WebView2RuntimePermissionException : InvalidOperationException
{
    public WebView2RuntimePermissionException(string message) : base(message)
    {
    }

    public WebView2RuntimePermissionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Grants the two fixed AppContainer SIDs (<c>S-1-15-2-1</c> ALL_APP_PACKAGES
/// and <c>S-1-15-2-2</c> ALL_RESTRICTED_APP_PACKAGES) inheritable
/// read/execute access on one app-owned WebView2 runtime directory so the
/// sandboxed <c>msedgewebview2.exe</c> child processes of a Fixed Version
/// runtime can load their binaries on Windows 10.
/// </summary>
/// <remarks>
/// #321 F3：目标目录与全部祖先由 <see cref="StableDirectoryHandleChain"/>
/// 从本地卷根逐段固定（任何祖先/目标重解析点、UNC 路径均拒绝），ACL 读写
/// 都只针对持有的目标句柄，绝不回落到按名字 Get/Set。已有 DACL 条目（含
/// 顺序）与所有者全部保留——只追加缺失的 SID 授权；已满足掩码/继承语义的
/// ACE 不重复添加；掩码或继承不足的既有 ACE 会被补足（追加完整条目）。
/// 无显式 DACL 的目录按失败处理，不重建权限。
/// </remarks>
public static class WebView2RuntimePermissions
{
    public const string AllAppPackagesSid = "S-1-15-2-1";
    public const string AllRestrictedAppPackagesSid = "S-1-15-2-2";

    private const int FileGenericReadAndExecute = 0x001200A9;
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 4;
    // RawSecurityDescriptor.GetBinaryForm 输出 self-relative 形式的
    // SECURITY_DESCRIPTOR：头部为 Revision(1) Sbz1(1) Control(2) Owner(4)
    // Group(4) Sacl(4) Dacl(4)，Dacl 的相对偏移恒为 16（偏移值相对描述符
    // 起始字节）。
    private const int DaclOffsetInDescriptor = 16;

    public static void GrantAppContainerReadAndExecute(
        string directory, bool requireAppContainerPermissions = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string full = Path.GetFullPath(directory);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new WebView2RuntimePermissionException(
                $"WebView2 运行库权限只支持本地卷目录，拒绝 UNC 路径：{full}");
        }
        if (Path.GetPathRoot(full) is { } root &&
            string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebView2RuntimePermissionException(
                "WebView2 运行库权限只能应用于应用自有目录，不能应用于驱动器根目录。");
        }
        StableDirectoryHandleChain chain;
        try
        {
            // Validate the directory chain without requesting WRITE_DAC.
            chain = StableDirectoryHandleChain.OpenDirectoryFromVolumeRoot(full);
        }
        catch (PortableLayoutException error)
        {
            throw new WebView2RuntimePermissionException(
                $"无法从本地卷根安全固定 WebView2 运行库目录链（重解析点/权限/不存在都会被拒绝）：{full}。原因：{error.Message}",
                error);
        }
        byte[] binary;
        RawSecurityDescriptor descriptor;
        RawAcl? dacl;
        using (chain)
        {
            if (!requireAppContainerPermissions) return;
            SafeFileHandle handle = chain.TargetDirectoryHandle;
            binary = ReadDescriptorBinary(handle, full);
            descriptor = new RawSecurityDescriptor(binary, 0);
            dacl = descriptor.DiscretionaryAcl;
            if (dacl is null)
            {
                throw new WebView2RuntimePermissionException(
                    $"WebView2 运行库目录没有显式 DACL，拒绝重建权限：{full}");
            }
            if (AppendMissingAppContainerAces(dacl) == 0)
            {
                return;
            }
        }
        // 确有缺失才申请 WRITE_DAC 重新固定同一目录并写入；失败 fail closed。
        StableDirectoryHandleChain writeChain;
        try
        {
            writeChain = StableDirectoryHandleChain.OpenDirectoryFromVolumeRoot(
                full, requireWriteDacAccess: true);
        }
        catch (PortableLayoutException error)
        {
            throw new WebView2RuntimePermissionException(
                $"WebView2 运行库目录需要补 AppContainer 只读权限但无法获得写入 DACL 的权限（标准用户对已满足目录无需该权限）：{full}。原因：{error.Message}",
                error);
        }
        using (writeChain)
        {
            SafeFileHandle handle = writeChain.TargetDirectoryHandle;
            binary = ReadDescriptorBinary(handle, full);
            descriptor = new RawSecurityDescriptor(binary, 0);
            if (descriptor.DiscretionaryAcl is not { } updateAcl)
            {
                throw new WebView2RuntimePermissionException(
                    $"WebView2 运行库目录没有显式 DACL，拒绝重建权限：{full}");
            }
            if (AppendMissingAppContainerAces(updateAcl) == 0)
            {
                return;
            }
            byte[] updated = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(updated, 0);
            ApplyDescriptorDacl(handle, updated, full);
        }
    }

    private static int AppendMissingAppContainerAces(RawAcl dacl)
    {
        return Append(dacl, AllAppPackagesSid) + Append(dacl, AllRestrictedAppPackagesSid);

        int Append(RawAcl acl, string sidValue)
        {
            var sid = new SecurityIdentifier(sidValue);
            for (int index = 0; index < acl.Count; index++)
            {
                if (acl[index] is CommonAce existing &&
                    existing.AceQualifier == AceQualifier.AccessAllowed &&
                    existing.SecurityIdentifier == sid &&
                    (existing.AccessMask & FileGenericReadAndExecute) ==
                        FileGenericReadAndExecute &&
                    existing.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
                    existing.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) &&
                    !existing.AceFlags.HasFlag(AceFlags.NoPropagateInherit) &&
                    !existing.AceFlags.HasFlag(AceFlags.InheritOnly))
                {
                    return 0;
                }
            }
            // Canonical 顺序：新 explicit allow 插在第一个 inherited ACE 之前
            // （已有 explicit 条目保持相对次序；没有 inherited 时追加尾部）。
            int insertAt = acl.Count;
            for (int index = 0; index < acl.Count; index++)
            {
                if (acl[index].AceFlags.HasFlag(AceFlags.Inherited))
                {
                    insertAt = index;
                    break;
                }
            }
            acl.InsertAce(
                insertAt,
                new CommonAce(
                    AceFlags.ObjectInherit | AceFlags.ContainerInherit,
                    AceQualifier.AccessAllowed,
                    FileGenericReadAndExecute,
                    sid,
                    isCallback: false,
                    opaque: null));
            return 1;
        }
    }

    private static byte[] ReadDescriptorBinary(SafeFileHandle handle, string full)
    {
        uint open = GetSecurityInfo(
            handle,
            SeFileObject,
            DaclSecurityInformation,
            nint.Zero,
            nint.Zero,
            out _,
            nint.Zero,
            out nint descriptor);
        if (open != 0 || descriptor == 0)
        {
            throw new WebView2RuntimePermissionException(
                $"无法读取 WebView2 运行库目录的 DACL（目录不受支持或访问被拒绝）：{full}。" +
                Win32Message(open));
        }
        try
        {
            int length = (int)GetSecurityDescriptorLength(descriptor);
            byte[] binary = new byte[length];
            Marshal.Copy(descriptor, binary, 0, length);
            return binary;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void ApplyDescriptorDacl(
        SafeFileHandle handle,
        byte[] descriptorBinary,
        string full)
    {
        uint daclOffset = BitConverter.ToUInt32(descriptorBinary, DaclOffsetInDescriptor);
        if (daclOffset == 0)
        {
            throw new WebView2RuntimePermissionException(
                $"WebView2 运行库目录描述符缺少 DACL：{full}");
        }
        var pinned = GCHandle.Alloc(descriptorBinary, GCHandleType.Pinned);
        try
        {
            nint dacl = pinned.AddrOfPinnedObject() + (int)daclOffset;
            uint apply = SetSecurityInfo(
                handle,
                SeFileObject,
                DaclSecurityInformation,
                nint.Zero,
                nint.Zero,
                dacl,
                nint.Zero);
            if (apply != 0)
            {
                throw new WebView2RuntimePermissionException(
                    $"无法写入 WebView2 运行库目录的 AppContainer 只读权限：{full}。" +
                    Win32Message(apply));
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static string Win32Message(uint errorCode) =>
        $"原因：{new Win32Exception((int)errorCode).Message}";

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
      SafeFileHandle handle,
      uint objectType,
      uint securityInfo,
      nint pSidOwner,
      nint pSidGroup,
      out nint pSidDacl,
      nint pSidSacl,
      out nint pSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
      SafeFileHandle handle,
      uint objectType,
      uint securityInfo,
      nint psidOwner,
      nint psidGroup,
      nint psidDacl,
      nint psidSacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(nint descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);
}
