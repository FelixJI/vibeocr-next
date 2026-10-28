namespace VibeOCR.App.Workbench;

/// <summary>
/// 可恢复的延迟取值：工厂抛出（如桌面壳初始化前尚未接线）不被缓存，
/// 后续取值重试工厂；一旦创建即固定实例。与 <c>Lazy&lt;T&gt;</c> 不同，
/// 不会把一次失败永久固化为不可用（bootstrap 初始快照可能在壳/更新服
/// 务接线前被读取，重试后必须能取得真实模型）。
/// </summary>
internal sealed class RecoverableLazy<T>(Func<T> factory) where T : class
{
  private readonly object gate = new();
  private T? value;

  internal T Value
  {
    get
    {
      lock (gate)
      {
        return value ??= factory();
      }
    }
  }
}
