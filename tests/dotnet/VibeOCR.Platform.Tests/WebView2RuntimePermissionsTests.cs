using System.Security.AccessControl;
using System.Security.Principal;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class WebView2RuntimePermissionsTests
{
  // ACL 断言只作用于测试自建的临时树，不修改系统/其它目录。
  private static readonly SecurityIdentifier AppPackages =
      new(WebView2RuntimePermissions.AllAppPackagesSid);

  private static readonly SecurityIdentifier RestrictedAppPackages =
      new(WebView2RuntimePermissions.AllRestrictedAppPackagesSid);

  private static string TempRuntimeTree()
  {
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-webview2-acl-{Guid.NewGuid():N}");
    string runtime = Path.Combine(root, "WebView2Runtime");
    Directory.CreateDirectory(runtime);
    File.WriteAllText(Path.Combine(runtime, "msedgewebview2.exe"), "stub");
    return runtime;
  }

  private static int CountSatisfyingAces(
      string directory, SecurityIdentifier sid, bool inherited)
  {
    var security = new DirectorySecurity(directory, AccessControlSections.Access);
    return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .Count(rule =>
            rule.IdentityReference == sid &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IsInherited == inherited &&
            rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute) &&
            rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
            rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) &&
            !rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit));
  }

  /// <summary>仅显式（非继承）规则快照，按 DACL 存储顺序。</summary>
  private static string[] SnapshotAces(string directory)
  {
    var security = new DirectorySecurity(directory, AccessControlSections.Access);
    return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .Where(rule => !rule.IsInherited)
        .Select(rule =>
            $"{rule.IdentityReference}|{rule.AccessControlType}|{(int)rule.FileSystemRights}|" +
            $"{(int)rule.InheritanceFlags}|{(int)rule.PropagationFlags}")
        .ToArray();
  }

  [Fact]
  public void GrantsInheritedReadExecuteToBothAppContainerSidsAndKeepsExistingAces()
  {
    string runtime = TempRuntimeTree();
    try
    {
      // 预置一条与 AppContainer 无关的既有授权，验证原 ACE 保全。
      var preset = new DirectorySecurity();
      preset.AddAccessRule(new FileSystemAccessRule(
          WindowsIdentity.GetCurrent().User!,
          FileSystemRights.Modify,
          InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
          PropagationFlags.None,
          AccessControlType.Allow));
      new DirectoryInfo(runtime).SetAccessControl(preset);
      string[] before = SnapshotAces(runtime);

      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);

      // 两条新授权（显式、非继承）+ 既有显式 ACE 全部保留。
      Assert.Equal(1, CountSatisfyingAces(runtime, AppPackages, inherited: false));
      Assert.Equal(1, CountSatisfyingAces(runtime, RestrictedAppPackages, inherited: false));
      Assert.Equal(before.Length + 2, SnapshotAces(runtime).Length);
      Assert.All(before, ace => Assert.Contains(ace, SnapshotAces(runtime)));

      // 子目录/子文件继承同一可继承 RX 授权。
      string childDir = Path.Combine(runtime, "Locales");
      Directory.CreateDirectory(childDir);
      string childFile = Path.Combine(childDir, "locale.pak");
      File.WriteAllText(childFile, "x");
      Assert.Equal(1, CountSatisfyingAces(childDir, AppPackages, inherited: true));
      var fileSecurity = new FileSecurity(childFile, AccessControlSections.Access);
      Assert.Contains(fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
          .Cast<FileSystemAccessRule>(),
          rule => rule.IdentityReference == AppPackages &&
              rule.IsInherited &&
              rule.AccessControlType == AccessControlType.Allow &&
              rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));

      // 重复应用不得扩增 ACE。
      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);
      Assert.Equal(before.Length + 2, SnapshotAces(runtime).Length);
    }
    finally
    {
      Directory.Delete(Path.GetDirectoryName(runtime)!, recursive: true);
    }
  }

  [Fact]
  public void CompletesInsufficientExistingAppContainerAces()
  {
    // 既有 ACE 只给读、无继承：不视为满足，必须补足完整条目。
    string runtime = TempRuntimeTree();
    try
    {
      var preset = new DirectorySecurity();
      preset.AddAccessRule(new FileSystemAccessRule(
          AppPackages,
          FileSystemRights.Read,
          InheritanceFlags.None,
          PropagationFlags.None,
          AccessControlType.Allow));
      new DirectoryInfo(runtime).SetAccessControl(preset);
      Assert.Equal(0, CountSatisfyingAces(runtime, AppPackages, inherited: false));

      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);

      Assert.Equal(1, CountSatisfyingAces(runtime, AppPackages, inherited: false));
      // 原有不足条目仍在（不删除既有授权）。
      var security = new DirectorySecurity(runtime, AccessControlSections.Access);
      Assert.Equal(2, security.GetAccessRules(true, true, typeof(SecurityIdentifier))
          .Cast<FileSystemAccessRule>()
          .Count(rule => rule.IdentityReference == AppPackages));
    }
    finally
    {
      Directory.Delete(Path.GetDirectoryName(runtime)!, recursive: true);
    }
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void RejectsAncestorJunctionWithoutTouchingTheTarget(bool requireAppContainerPermissions)
  {
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-webview2-acl-junction-{Guid.NewGuid():N}");
    string realTree = Path.Combine(root, "real", "WebView2Runtime");
    string junctionParent = Path.Combine(root, "link");
    Directory.CreateDirectory(realTree);
    Directory.CreateDirectory(junctionParent);
    File.WriteAllText(Path.Combine(realTree, "msedgewebview2.exe"), "stub");
    var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
        "cmd.exe", $"/c mklink /J \"{Path.Combine(junctionParent, "WebView2Runtime")}\" \"{realTree}\"")
    {
      CreateNoWindow = true,
      UseShellExecute = false,
    });
    Assert.NotNull(junction);
    Assert.True(junction.WaitForExit(15000));
    try
    {
      string[] before = SnapshotAces(realTree);

      Assert.Throws<WebView2RuntimePermissionException>(
          () => WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
              Path.Combine(junctionParent, "WebView2Runtime"), requireAppContainerPermissions));

      // 目标目录 ACL 未被修改。
      Assert.Equal(before, SnapshotAces(realTree));
    }
    finally
    {
      var junctionRemoval = System.Diagnostics.Process.Start(
          new System.Diagnostics.ProcessStartInfo(
              "cmd.exe", $"/c rmdir \"{Path.Combine(junctionParent, "WebView2Runtime")}\"")
          {
              CreateNoWindow = true,
              UseShellExecute = false,
          });
      junctionRemoval?.WaitForExit(15000);
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void PlatformWithoutAppContainerRequirementLeavesDaclUnchanged()
  {
    string runtime = TempRuntimeTree();
    try
    {
      var security = new DirectorySecurity();
      security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      security.AddAccessRule(new FileSystemAccessRule(
          WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
          InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
          PropagationFlags.None, AccessControlType.Allow));
      new DirectoryInfo(runtime).SetAccessControl(security);
      string[] before = SnapshotAces(runtime);

      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
          runtime, requireAppContainerPermissions: false);

      Assert.Equal(before, SnapshotAces(runtime));
    }
    finally { Directory.Delete(Path.GetDirectoryName(runtime)!, recursive: true); }
  }

  [Fact]
  public void SatisfiedDirectoryNeedsNoWriteAttempt()
  {
    // Reapplying the Windows 10 grants preserves the existing ACL.
    string runtime = TempRuntimeTree();
    try
    {
      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);
      string[] before = SnapshotAces(runtime);

      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);

      Assert.Equal(before, SnapshotAces(runtime));
      // 只读句柄链（不申请 WRITE_DAC）对已满足目录可用。
      using var chain = StableDirectoryHandleChain.OpenDirectoryFromVolumeRoot(
          runtime, requireWriteDacAccess: false);
      Assert.False(chain.TargetDirectoryHandle.IsInvalid);
    }
    finally
    {
      Directory.Delete(Path.GetDirectoryName(runtime)!, recursive: true);
    }
  }

  [Fact]
  public void UnsatisfiedDirectoryRequestsWriteDacOnlyWhenNeeded()
  {
    // 结构性契约：未满足时才走需要 WRITE_DAC 的句柄链；满足时不申请。
    // （Windows 对目录 owner 隐式授予 WRITE_DAC，标准用户非 owner 的真实
    // 无-WRITE_DAC 场景由 root 在真实候选上验收，无法在单测内伪造。）
    string runtime = TempRuntimeTree();
    try
    {
      using (StableDirectoryHandleChain writeChain =
          StableDirectoryHandleChain.OpenDirectoryFromVolumeRoot(
              runtime, requireWriteDacAccess: true))
      {
        Assert.False(writeChain.TargetDirectoryHandle.IsInvalid);
      }
      using (StableDirectoryHandleChain readChain =
          StableDirectoryHandleChain.OpenDirectoryFromVolumeRoot(runtime))
      {
        Assert.False(readChain.TargetDirectoryHandle.IsInvalid);
      }
      // 未满足目录授予成功后，再次应用不再需要写入（见上一个测试）。
      WebView2RuntimePermissions.GrantAppContainerReadAndExecute(runtime);
    }
    finally
    {
      Directory.Delete(Path.GetDirectoryName(runtime)!, recursive: true);
    }
  }

  [Fact]
  public void RejectsUncAndMissingAndFileTargets()
  {
    Assert.Throws<WebView2RuntimePermissionException>(
        () => WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
            "\\\\localhost\\c$\\vibeocr"));
    Assert.Throws<WebView2RuntimePermissionException>(
        () => WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
            Path.Combine(Path.GetTempPath(), $"vibeocr-missing-{Guid.NewGuid():N}")));
    string file = Path.Combine(
        Path.GetTempPath(), $"vibeocr-webview2-acl-file-{Guid.NewGuid():N}.txt");
    File.WriteAllText(file, "not a directory");
    try
    {
      Assert.Throws<WebView2RuntimePermissionException>(
          () => WebView2RuntimePermissions.GrantAppContainerReadAndExecute(file));
    }
    finally
    {
      File.Delete(file);
    }
    Assert.Throws<WebView2RuntimePermissionException>(
        () => WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
            Path.GetPathRoot(Path.GetFullPath(Directory.GetCurrentDirectory()))!));
  }
}
