namespace VibeOCR.Platform.Windows;

/// <summary>Finds closed, high-contrast rectangular regions in a frozen BGRA desktop.</summary>
public static class ScreenEdgeCandidates
{
  public static IReadOnlyList<PhysicalRectangle> Find(byte[] pixels, int width, int height,
    int stride, PhysicalPoint point)
  {
    if (width < 2 || height < 2 || stride < (long)width * 4 ||
        pixels.LongLength < (long)stride * height || point.X < 0 || point.X >= width ||
        point.Y < 0 || point.Y >= height) return [];

    bool Edge(int x, int y, bool vertical)
    {
      if (x < 1 || y < 1 || x >= width || y >= height) return false;
      int offset = y * stride + x * 4;
      int previous = offset - (vertical ? 4 : stride);
      return Math.Max(Math.Abs(pixels[offset] - pixels[previous]),
        Math.Max(Math.Abs(pixels[offset + 1] - pixels[previous + 1]),
          Math.Abs(pixels[offset + 2] - pixels[previous + 2]))) >= 24;
    }

    List<int> Scan(bool vertical, int direction)
    {
      var found = new List<int>();
      int origin = vertical ? point.X : point.Y;
      int cross = vertical ? point.Y : point.X;
      int limit = vertical ? width : height;
      // Bounded work per pointer move; several samples reject glyphs and isolated noise.
      for (int distance = 1; distance <= 2048 && found.Count < 12; distance++)
      {
        int at = origin + direction * distance;
        if (at < 1 || at >= limit) break;
        bool StrongEdge(int coordinate)
        {
          int hits = 0;
          for (int delta = -12; delta <= 12; delta += 6)
            if (Edge(vertical ? coordinate : cross + delta, vertical ? cross + delta : coordinate, vertical)) hits++;
          return hits >= 3;
        }
        if (!StrongEdge(at)) continue;
        // Include both sides of a thin outline instead of cropping its inner edge.
        int outer = at;
        for (int extra = 1; extra <= 4; extra++)
          if (StrongEdge(at + direction * extra)) outer = at + direction * extra;
        found.Add(outer);
        distance += Math.Abs(outer - at) + 3;
      }
      return found;
    }

    bool Side(int fixedAt, int from, int to, bool vertical)
    {
      int hits = 0;
      for (int i = 0; i < 32; i++)
      {
        int at = from + (int)((long)(to - from - 1) * i / 31);
        if (Edge(vertical ? fixedAt : at, vertical ? at : fixedAt, vertical)) hits++;
      }
      return hits >= 28;
    }

    List<int> left = Scan(true, -1), right = Scan(true, 1);
    List<int> top = Scan(false, -1), bottom = Scan(false, 1);
    var result = new List<PhysicalRectangle>();
    foreach (int x in left)
      foreach (int r in right)
      {
        if (r - x < 48) continue;
        int[] upper = top.Where(y => Side(y, x, r, false)).ToArray();
        int[] lower = bottom.Where(y => Side(y, x, r, false)).ToArray();
        foreach (int y in upper)
          foreach (int b in lower)
            if (b - y >= 32 && Side(x, y, b, true) && Side(r, y, b, true))
              result.Add(new(x, y, r - x, b - y));
      }
    return result.OrderBy(rect => (long)rect.Width * rect.Height).Take(4).ToArray();
  }
}
