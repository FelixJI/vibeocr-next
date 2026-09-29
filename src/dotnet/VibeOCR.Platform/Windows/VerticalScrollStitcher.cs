namespace VibeOCR.Platform.Windows;

/// <summary>Outcome of a single <see cref="VerticalScrollStitcher.Append"/> attempt.</summary>
public enum ScrollAppendStatus
{
  /// <summary>A unique downward scroll was verified; the frame tail was appended.</summary>
  Added,

  /// <summary>The frame is byte-identical to the last accepted frame (no scroll happened).</summary>
  Duplicate,

  /// <summary>The content scrolled up (opposite direction); nothing was appended.</summary>
  Reverse,

  /// <summary>No vertical offset produced a valid overlap of at least the minimum.</summary>
  NoOverlap,

  /// <summary>At least two distinct vertical offsets fully verified; the scroll is not unique.</summary>
  Ambiguous,

  /// <summary>The frame has no vertical variation, so alignment cannot be determined.</summary>
  LowTexture,

  /// <summary>Accepting the frame would exceed a fixed stitch limit.</summary>
  LimitReached,
}

/// <summary>Result of appending one frame: the status plus the document rows actually added.</summary>
public readonly record struct ScrollAppendResult(ScrollAppendStatus Status, int AddedRows);

/// <summary>
/// Minimal pure C# stitcher for vertically scrolling screen captures. Frames must be
/// same-size BGRA8 <see cref="CapturedFrame"/>s; only exact static content is matched.
/// Stores the first frame, the last accepted frame and the appended strips (never one
/// buffer per frame). Failed or rejected appends never mutate accepted state.
/// Instances are not thread-safe.
/// </summary>
public sealed class VerticalScrollStitcher
{
  /// <summary>Hard cap on total output pixels (width x height of the stitched document).</summary>
  public const int MaximumPixels = 8_000_000;

  /// <summary>Hard cap on any single frame dimension and on the stitched height.</summary>
  public const int MaximumDimension = 32768;

  /// <summary>Hard cap on accepted frames; the constructor frame counts as one.</summary>
  public const int MaximumFrames = 200;

  private const int MinimumOverlapFloor = 32;
  private const int SampledRowCount = 7;

  private readonly int _frameWidth;
  private readonly int _frameHeight;
  private readonly int _frameRowBytes;
  private readonly byte[] _firstFrame;
  private readonly List<byte[]> _strips = [];
  private byte[] _previousFrame;
  private int _frameCount;
  private int _height;

  /// <summary>Current stitched document width in pixels (fixed by the first frame).</summary>
  public int Width => _frameWidth;

  /// <summary>Current stitched document height in pixels.</summary>
  public int Height => _height;

  /// <summary>Number of accepted frames; the first frame counts as one, duplicates do not.</summary>
  public int FrameCount => _frameCount;

  /// <param name="first">The first captured frame of the scroll sequence.</param>
  /// <exception cref="ArgumentException">The frame is not a well-formed BGRA8 buffer.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A dimension or pixel-count limit is exceeded.</exception>
  /// <exception cref="InvalidDataException">Every row of the frame is identical, so vertical alignment would be undecidable.</exception>
  public VerticalScrollStitcher(CapturedFrame first)
  {
    ArgumentNullException.ThrowIfNull(first);
    ValidateFrameGeometry(first, nameof(first));

    byte[] packed = PackFrame(first);
    if (HasNoVerticalVariation(packed, checked(first.Width * 4), first.Height, CancellationToken.None))
    {
      throw new InvalidDataException(
        "The first frame has no vertical variation: every row is byte-identical, so scroll offsets cannot be resolved. Choose a region whose content varies between rows.");
    }

    _frameWidth = first.Width;
    _frameHeight = first.Height;
    _frameRowBytes = checked(first.Width * 4);
    _firstFrame = packed;
    _previousFrame = packed;
    _frameCount = 1;
    _height = first.Height;
  }

  /// <summary>
  /// Attempts to append the next frame of the scroll sequence. The frame must have the
  /// same size as the first frame. Candidate vertical offsets are considered in both
  /// directions; a candidate is only accepted after every row of its overlap has been
  /// verified with exact BGRA byte comparison. Exactly one reliable downward offset is
  /// required to append the frame tail.
  /// </summary>
  /// <returns>The status of the attempt and, for <see cref="ScrollAppendStatus.Added"/>, the appended row count.</returns>
  /// <exception cref="OperationCanceledException">The cancellation token fired during validation or scanning.</exception>
  public ScrollAppendResult Append(CapturedFrame next, CancellationToken cancellationToken = default) =>
    AppendCore(next, cancellationToken, beforeCandidate: null);

  // A synchronous scan boundary lets regression tests cancel without timer/thread-pool races.
  internal ScrollAppendResult AppendCore(
    CapturedFrame next, CancellationToken cancellationToken, Action<int>? beforeCandidate)
  {
    ArgumentNullException.ThrowIfNull(next);
    ValidateFrameGeometry(next, nameof(next));
    if (next.Width != _frameWidth || next.Height != _frameHeight)
    {
      throw new ArgumentException(
        $"Frame size {next.Width}x{next.Height} does not match the stitcher frame size {_frameWidth}x{_frameHeight}.",
        nameof(next));
    }

    cancellationToken.ThrowIfCancellationRequested();

    byte[] packed = PackFrame(next);
    if (_previousFrame.AsSpan().SequenceEqual(packed))
    {
      return new ScrollAppendResult(ScrollAppendStatus.Duplicate, 0);
    }

    if (_frameCount >= MaximumFrames)
    {
      return new ScrollAppendResult(ScrollAppendStatus.LimitReached, 0);
    }

    if (HasNoVerticalVariation(packed, _frameRowBytes, _frameHeight, cancellationToken))
    {
      return new ScrollAppendResult(ScrollAppendStatus.LowTexture, 0);
    }

    // Fast path: even a single new row would already exceed a dimension or pixel limit.
    if (_height >= MaximumDimension || (long)_frameWidth * (_height + 1) > MaximumPixels)
    {
      return new ScrollAppendResult(ScrollAppendStatus.LimitReached, 0);
    }

    int maxScroll = _frameHeight - GetMinimumOverlap(_frameHeight);
    int candidate = 0;
    bool ambiguous = false;
    ReadOnlySpan<byte> previous = _previousFrame;
    for (int scroll = 1; scroll <= maxScroll; scroll++)
    {
      beforeCandidate?.Invoke(scroll);
      cancellationToken.ThrowIfCancellationRequested();
      if (VerifyAlignment(previous, packed, scroll, _frameRowBytes, _frameHeight, cancellationToken))
      {
        if (candidate == 0)
        {
          candidate = scroll;
        }
        else
        {
          ambiguous = true;
          break;
        }
      }

      if (VerifyAlignment(previous, packed, -scroll, _frameRowBytes, _frameHeight, cancellationToken))
      {
        if (candidate == 0)
        {
          candidate = -scroll;
        }
        else
        {
          ambiguous = true;
          break;
        }
      }
    }

    if (ambiguous)
    {
      return new ScrollAppendResult(ScrollAppendStatus.Ambiguous, 0);
    }

    if (candidate == 0)
    {
      return new ScrollAppendResult(ScrollAppendStatus.NoOverlap, 0);
    }

    if (candidate < 0)
    {
      return new ScrollAppendResult(ScrollAppendStatus.Reverse, 0);
    }

    int newHeight = checked(_height + candidate);
    if (newHeight > MaximumDimension || (long)_frameWidth * newHeight > MaximumPixels)
    {
      return new ScrollAppendResult(ScrollAppendStatus.LimitReached, 0);
    }

    // Accept: store only the new strip plus the packed copy used for the next match.
    byte[] strip = new byte[checked(candidate * _frameRowBytes)];
    Buffer.BlockCopy(packed, (_frameHeight - candidate) * _frameRowBytes, strip, 0, strip.Length);
    _strips.Add(strip);
    _previousFrame = packed;
    _frameCount++;
    _height = newHeight;
    return new ScrollAppendResult(ScrollAppendStatus.Added, candidate);
  }

  /// <summary>Composes the stitched document as a single BGRA8 frame (stride = width x 4).</summary>
  public CapturedFrame BuildFrame()
  {
    byte[] pixels = new byte[checked(_frameRowBytes * _height)];
    Buffer.BlockCopy(_firstFrame, 0, pixels, 0, _firstFrame.Length);
    int offset = _firstFrame.Length;
    foreach (byte[] strip in _strips)
    {
      Buffer.BlockCopy(strip, 0, pixels, offset, strip.Length);
      offset += strip.Length;
    }

    return new CapturedFrame(pixels, _frameWidth, _height, _frameRowBytes, "BGRA8");
  }

  /// <summary>
  /// The smallest overlap (in rows) accepted between two frames:
  /// max(32, frameHeight / 4). Regions smaller than 32x64 cannot satisfy it.
  /// </summary>
  public static int GetMinimumOverlap(int frameHeight)
  {
    if (frameHeight <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(frameHeight), frameHeight, "Frame height must be positive.");
    }

    return Math.Max(MinimumOverlapFloor, frameHeight / 4);
  }

  private static void ValidateFrameGeometry(CapturedFrame frame, string paramName)
  {
    if (!string.Equals(frame.PixelFormat, "BGRA8", StringComparison.Ordinal))
    {
      throw new ArgumentException(
        $"Frame pixel format must be \"BGRA8\", but was \"{frame.PixelFormat}\".", paramName);
    }

    if (frame.Width <= 0)
    {
      throw new ArgumentOutOfRangeException(paramName, frame.Width, "Frame width must be positive.");
    }

    if (frame.Height <= 0)
    {
      throw new ArgumentOutOfRangeException(paramName, frame.Height, "Frame height must be positive.");
    }

    if (frame.Width > MaximumDimension)
    {
      throw new ArgumentOutOfRangeException(
        paramName, frame.Width, $"Frame width exceeds the {nameof(MaximumDimension)} limit of {MaximumDimension}.");
    }

    if (frame.Height > MaximumDimension)
    {
      throw new ArgumentOutOfRangeException(
        paramName, frame.Height, $"Frame height exceeds the {nameof(MaximumDimension)} limit of {MaximumDimension}.");
    }

    if ((long)frame.Width * frame.Height > MaximumPixels)
    {
      throw new ArgumentOutOfRangeException(
        paramName, (long)frame.Width * frame.Height,
        $"Frame pixel count exceeds the {nameof(MaximumPixels)} limit of {MaximumPixels}.");
    }

    int rowBytes = checked(frame.Width * 4);
    if (frame.Stride < rowBytes)
    {
      throw new ArgumentOutOfRangeException(
        paramName, frame.Stride, $"Frame stride {frame.Stride} is smaller than the BGRA row size of {rowBytes} bytes.");
    }

    long requiredLength = (long)frame.Stride * frame.Height;
    if (frame.Pixels is null || frame.Pixels.LongLength != requiredLength)
    {
      throw new ArgumentException(
        $"Frame pixel buffer length {frame.Pixels?.Length ?? -1} does not match stride {frame.Stride} x height {frame.Height} = {requiredLength}.",
        paramName);
    }
  }

  private static byte[] PackFrame(CapturedFrame frame)
  {
    int rowBytes = checked(frame.Width * 4);
    byte[] packed = new byte[checked(rowBytes * frame.Height)];
    byte[] source = frame.Pixels!;
    if (frame.Stride == rowBytes)
    {
      Buffer.BlockCopy(source, 0, packed, 0, packed.Length);
      return packed;
    }

    for (int y = 0; y < frame.Height; y++)
    {
      Buffer.BlockCopy(source, y * frame.Stride, packed, y * rowBytes, rowBytes);
    }

    return packed;
  }

  private static bool HasNoVerticalVariation(
    byte[] packed,
    int rowBytes,
    int height,
    CancellationToken cancellationToken)
  {
    ReadOnlySpan<byte> firstRow = packed.AsSpan(0, rowBytes);
    for (int y = 1; y < height; y++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!firstRow.SequenceEqual(packed.AsSpan(y * rowBytes, rowBytes)))
      {
        return false;
      }
    }

    return true;
  }

  /// <summary>
  /// Verifies one candidate offset over the whole overlap. A handful of spread rows is
  /// compared first as a cheap filter; acceptance requires every overlap row to match
  /// exactly. For scroll &gt; 0 the content moved down (current row i equals previous row
  /// i + scroll); for scroll &lt; 0 it moved up.
  /// </summary>
  private static bool VerifyAlignment(
    ReadOnlySpan<byte> previous,
    ReadOnlySpan<byte> current,
    int scroll,
    int rowBytes,
    int frameHeight,
    CancellationToken cancellationToken)
  {
    int overlap = frameHeight - Math.Abs(scroll);
    if (overlap <= 0)
    {
      return false;
    }

    int down = Math.Max(scroll, 0);
    int up = Math.Max(-scroll, 0);

    int samples = Math.Min(SampledRowCount, overlap);
    for (int k = 0; k < samples; k++)
    {
      int row = samples == 1 ? 0 : (int)((long)k * (overlap - 1) / (samples - 1));
      if (!RowMatches(previous, row + down, current, row + up, rowBytes))
      {
        return false;
      }
    }

    for (int row = 0; row < overlap; row++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!RowMatches(previous, row + down, current, row + up, rowBytes))
      {
        return false;
      }
    }

    return true;
  }

  private static bool RowMatches(
    ReadOnlySpan<byte> left,
    int leftRow,
    ReadOnlySpan<byte> right,
    int rightRow,
    int rowBytes)
  {
    return left.Slice(leftRow * rowBytes, rowBytes)
      .SequenceEqual(right.Slice(rightRow * rowBytes, rowBytes));
  }
}
