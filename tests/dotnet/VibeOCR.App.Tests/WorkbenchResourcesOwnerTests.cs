using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchResourcesOwnerTests
{
  [Fact]
  public async Task ConcurrentRetirementAndPublishingKeepOnlySurvivingResources()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-resource-retirement-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      var owner = new WorkbenchResourcesOwner(broker, root);
      byte[] bytes = [1, 2, 3, 4];
      var previous = new List<WorkbenchResourceReference>();
      for (int i = 0; i < 16; i++)
        previous.Add(await owner.PublishBytesAsync(bytes, "image/png", ".png", TestContext.Current.CancellationToken));
      var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task[] retirements = previous.Select(resource => Task.Run(async () =>
      {
        await start.Task;
        owner.ReleaseResource(resource);
      })).ToArray();
      Task<WorkbenchResourceReference>[] publications = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
      {
        await start.Task;
        return await owner.PublishBytesAsync(bytes, "image/png", ".png", TestContext.Current.CancellationToken);
      })).ToArray();
      start.SetResult();
      await Task.WhenAll(retirements.Concat<Task>(publications));
      Assert.Equal(16, Directory.GetFiles(Path.Combine(root, "session")).Length);
      foreach (WorkbenchResourceReference retired in previous)
        await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
          await broker.OpenAsync(new Uri(retired.Url), TestContext.Current.CancellationToken));
      foreach (Task<WorkbenchResourceReference> publication in publications)
      {
        WorkbenchResourceReference resource = await publication;
        await using WorkbenchResourceResponse live = await broker.OpenAsync(new Uri(resource.Url), TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await live.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, copy.ToArray());
      }
      owner.Release();
      Assert.Empty(Directory.GetFiles(Path.Combine(root, "session")));
    }
    finally { Directory.Delete(root, recursive: true); }
  }
}