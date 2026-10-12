namespace VibeOCR.App;

// One retired editor must finish draining before its replacement owns a WebView.
internal sealed class ImageEditorRetirement
{
  private Task drain = Task.CompletedTask;
  private long generation;
  internal Task Drain => drain;
  internal long BeginReplacement() => ++generation;
  internal void CancelReplacement() => ++generation;
  internal bool IsCurrent(long request, Guid session, Guid? currentSession, bool shuttingDown) =>
    !shuttingDown && request == generation && session == currentSession;

  internal void Track(Task disposal)
  {
    if (ReferenceEquals(drain, disposal)) return;
    // Preserve a failed drain: a later request must not create another control.
    drain = drain.IsCompletedSuccessfully ? disposal : Task.WhenAll(drain, disposal);
  }
}
