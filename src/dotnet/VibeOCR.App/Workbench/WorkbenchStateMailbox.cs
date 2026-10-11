using System.Threading.Channels;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchStateMailbox(IReadOnlySet<string> scopes)
{
  internal static readonly IReadOnlySet<string> AllScopes = new HashSet<string>(StringComparer.Ordinal)
  { "shell", "recognition", "imageEdit", "batch", "pdf", "qrcode", "settings", "update", "about", "diagnostics" };
  private readonly Dictionary<string, WorkbenchStateEnvelope> latest = new(StringComparer.Ordinal);
  private readonly IReadOnlySet<string> projection = scopes.ToHashSet(StringComparer.Ordinal);
  private readonly Dictionary<string, WorkbenchStateEnvelope> settled = new(StringComparer.Ordinal);
  internal readonly Channel<bool> Signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
  { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
  internal bool Resync { get; set; }
  internal int PendingCount => latest.Count + settled.Count;

  internal void Publish(WorkbenchStateEnvelope state)
  {
    if (!projection.Contains(state.Scope)) return;
    latest[state.Scope] = state;
    if (state.State?.IsTerminal == true)
    {
      // 一个当前终态不能被随后进度覆盖；多个已替代操作转为显式当前状态恢复。
      if (settled.ContainsKey(state.Scope)) Resync = true;
      else settled[state.Scope] = state;
    }
    Signal.Writer.TryWrite(true);
  }

  internal WorkbenchStateEnvelope[] Drain(IEnumerable<WorkbenchStateEnvelope> snapshots, long revision)
  {
    WorkbenchStateEnvelope[] result;
    if (Resync)
    {
      WorkbenchStateEnvelope[] states = snapshots.Where(state => projection.Contains(state.Scope))
        .OrderBy(state => state.Scope).Select(state => state with { Revision = revision }).ToArray();
      result = [new(revision, "shell", WorkbenchStateChange.Reset, null), .. states,
        new(revision, "shell", WorkbenchStateChange.Ready, null)];
    }
    else result = settled.Values.Concat(latest.Values).DistinctBy(state => state.Revision)
      .OrderBy(state => state.Revision).ToArray();
    latest.Clear(); settled.Clear(); Resync = false;
    return result;
  }

}
