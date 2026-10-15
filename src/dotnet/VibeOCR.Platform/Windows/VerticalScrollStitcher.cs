namespace VibeOCR.Platform.Windows;

public enum ScrollCaptureDirection { Down, Up, Right, Left }

/// <summary>Outcome of a single append attempt; rejected frames never change accepted state.</summary>
public enum ScrollAppendStatus
{
  Added, Duplicate, Reverse, NoOverlap, Ambiguous, LowTexture, LimitReached, FixedContentChanged,
}

/// <summary>AddedRows is the number of new pixels along the selected scroll axis.</summary>
public readonly record struct ScrollAppendResult(ScrollAppendStatus Status, int AddedRows);

/// <summary>
/// Exact static BGRA stitcher. Directions are normalized to downward rows internally;
/// only the first body, previous body and new strips are retained. Explicit fixed edges
/// are preserved once and must remain byte-identical in every accepted frame.
/// </summary>
public sealed class VerticalScrollStitcher
{
  public const int MaximumPixels = 8_000_000;
  public const int MaximumDimension = 32768;
  public const int MaximumFrames = 200;
  private const int MinimumOverlapFloor = 32;
  private const int SampledRowCount = 7;
  private readonly int _sourceWidth, _sourceHeight;
  private readonly ScrollCaptureDirection _direction;
  private readonly int _frameWidth, _frameHeight, _frameRowBytes;
  private readonly byte[] _leading, _trailing, _firstFrame;
  private readonly List<byte[]> _strips = [];
  private byte[] _previousFrame;
  private int _frameCount = 1;
  private int _height;
  private bool Horizontal => _direction is ScrollCaptureDirection.Right or ScrollCaptureDirection.Left;
  private bool Reversed => _direction is ScrollCaptureDirection.Up or ScrollCaptureDirection.Left;
  private int FullHeight => _height + (_leading.Length + _trailing.Length) / _frameRowBytes;
  public int Width => Horizontal ? FullHeight : _frameWidth;
  public int Height => Horizontal ? _frameWidth : FullHeight;
  public int FrameCount => _frameCount;
  public int MaximumOffset => _frameHeight - GetMinimumOverlap(_frameHeight);

  /// <param name="fixedLeadingPixels">Explicit physical top (vertical) or left (horizontal) fixed edge.</param>
  /// <param name="fixedTrailingPixels">Explicit physical bottom (vertical) or right (horizontal) fixed edge.</param>
  public VerticalScrollStitcher(CapturedFrame first,
    ScrollCaptureDirection direction = ScrollCaptureDirection.Down,
    int fixedLeadingPixels = 0, int fixedTrailingPixels = 0)
  {
    ArgumentNullException.ThrowIfNull(first);
    ValidateFrameGeometry(first, nameof(first));
    if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
    _direction = direction;
    _sourceWidth = first.Width;
    _sourceHeight = first.Height;
    int axis = Horizontal ? first.Width : first.Height;
    if (fixedLeadingPixels < 0 || fixedTrailingPixels < 0 ||
        (long)fixedLeadingPixels + fixedTrailingPixels > axis - 64)
      throw new ArgumentOutOfRangeException(nameof(fixedLeadingPixels), "Fixed edges must leave at least 64 scrollable pixels.");
    _frameWidth = Horizontal ? first.Height : first.Width;
    _frameHeight = axis - fixedLeadingPixels - fixedTrailingPixels;
    _frameRowBytes = checked(_frameWidth * 4);
    byte[] packed = Normalize(first, CancellationToken.None);
    int leadingRows = Reversed ? fixedTrailingPixels : fixedLeadingPixels;
    int trailingRows = Reversed ? fixedLeadingPixels : fixedTrailingPixels;
    _leading = packed.AsSpan(0, leadingRows * _frameRowBytes).ToArray();
    _trailing = packed.AsSpan(packed.Length - trailingRows * _frameRowBytes).ToArray();
    _firstFrame = packed.AsSpan(_leading.Length, _frameHeight * _frameRowBytes).ToArray();
    if (HasNoVerticalVariation(_firstFrame, CancellationToken.None))
      throw new InvalidDataException("The first frame has no vertical variation along the selected scroll axis.");
    _previousFrame = _firstFrame;
    _height = _frameHeight;
  }

  public ScrollAppendResult Append(CapturedFrame next, CancellationToken cancellationToken = default) =>
    AppendCore(next, cancellationToken, null);

  /// <summary>
  /// User-confirmed correction: accepts only a bounded positive offset whose complete
  /// overlap is byte-identical. It can resolve repeated-texture ambiguity, never bypass
  /// overlap, fixed-edge or resource checks.
  /// </summary>
  public ScrollAppendResult AppendWithOffset(CapturedFrame next, int offset,
    CancellationToken cancellationToken = default)
  {
    if (offset < 1 || offset > MaximumOffset) throw new ArgumentOutOfRangeException(nameof(offset));
    return AppendCore(next, cancellationToken, null, offset);
  }

  internal ScrollAppendResult AppendCore(CapturedFrame next, CancellationToken cancellationToken,
    Action<int>? beforeCandidate, int? confirmedOffset = null)
  {
    ArgumentNullException.ThrowIfNull(next);
    ValidateFrameGeometry(next, nameof(next));
    if (next.Width != _sourceWidth || next.Height != _sourceHeight)
      throw new ArgumentException("Frame size does not match the first frame.", nameof(next));
    cancellationToken.ThrowIfCancellationRequested();
    byte[] full = Normalize(next, cancellationToken);
    if (!full.AsSpan(0, _leading.Length).SequenceEqual(_leading) ||
        !full.AsSpan(full.Length - _trailing.Length).SequenceEqual(_trailing))
      return new(ScrollAppendStatus.FixedContentChanged, 0);
    byte[] packed = full.AsSpan(_leading.Length, _frameHeight * _frameRowBytes).ToArray();
    if (_previousFrame.AsSpan().SequenceEqual(packed)) return new(ScrollAppendStatus.Duplicate, 0);
    if (_frameCount >= MaximumFrames || FullHeight >= MaximumDimension ||
        (long)_frameWidth * (FullHeight + 1) > MaximumPixels)
      return new(ScrollAppendStatus.LimitReached, 0);
    if (HasNoVerticalVariation(packed, cancellationToken)) return new(ScrollAppendStatus.LowTexture, 0);
    int candidate = 0;
    if (confirmedOffset is int offset)
    {
      if (!VerifyAlignment(_previousFrame, packed, offset, cancellationToken))
        return new(ScrollAppendStatus.NoOverlap, 0);
      candidate = offset;
    }
    else
    {
      for (int scroll = 1; scroll <= MaximumOffset; scroll++)
      {
        beforeCandidate?.Invoke(scroll);
        cancellationToken.ThrowIfCancellationRequested();
        for (int sign = 1; sign >= -1; sign -= 2)
        {
          int signed = scroll * sign;
          if (!VerifyAlignment(_previousFrame, packed, signed, cancellationToken)) continue;
          // Candidates are visited by increasing magnitude. If no earlier
          // positive match fits, a matching positive offset already over the
          // limits rules out every later positive choice too.
          if (signed > 0 && candidate <= 0 && ExceedsLimits(signed))
            return new(ScrollAppendStatus.LimitReached, 0);
          if (candidate != 0)
          {
            if (candidate > 0 || signed > 0) return new(ScrollAppendStatus.Ambiguous, 0);
            // Multiple negative-only matches still require reverse recovery,
            // never a positive-offset correction that cannot succeed.
          }
          else candidate = signed;
        }
      }
    }
    if (candidate == 0) return new(ScrollAppendStatus.NoOverlap, 0);
    if (candidate < 0) return new(ScrollAppendStatus.Reverse, 0);
    if (ExceedsLimits(candidate))
      return new(ScrollAppendStatus.LimitReached, 0);
    cancellationToken.ThrowIfCancellationRequested();
    byte[] strip = packed.AsSpan((_frameHeight - candidate) * _frameRowBytes).ToArray();
    _strips.Add(strip);
    _previousFrame = packed;
    _frameCount++;
    _height += candidate;
    return new(ScrollAppendStatus.Added, candidate);
  }

  private bool ExceedsLimits(int offset) => FullHeight + offset > MaximumDimension ||
    (long)_frameWidth * (FullHeight + offset) > MaximumPixels;

  /// <summary>Samples retained strips directly; never materializes the full stitched image for a preview.</summary>
  public CapturedFrame BuildPreview(int maximumDimension, CancellationToken cancellationToken = default)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);
    cancellationToken.ThrowIfCancellationRequested();
    double scale = Math.Min(1.0, (double)maximumDimension / Math.Max(Width, Height));
    int width = Math.Max(1, (int)Math.Round(Width * scale));
    int height = Math.Max(1, (int)Math.Round(Height * scale));
    byte[] pixels = new byte[checked(width * height * 4)];
    int axisLength = Horizontal ? width : height;
    int crossLength = Horizontal ? height : width;
    for (int axis = 0; axis < axisLength; axis++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      int sourceAxis = (int)((long)axis * FullHeight / axisLength);
      ReadOnlySpan<byte> row = PreviewRow(Reversed ? FullHeight - 1 - sourceAxis : sourceAxis);
      for (int cross = 0; cross < crossLength; cross++)
      {
        int sourceCross = (int)((long)cross * _frameWidth / crossLength);
        int target = Horizontal ? cross * width + axis : axis * width + cross;
        row.Slice(sourceCross * 4, 4).CopyTo(pixels.AsSpan(target * 4, 4));
      }
    }
    return new(pixels, width, height, width * 4, "BGRA8");
  }

  private ReadOnlySpan<byte> PreviewRow(int row)
  {
    int offset = row * _frameRowBytes;
    if (offset < _leading.Length) return _leading.AsSpan(offset, _frameRowBytes);
    offset -= _leading.Length;
    if (offset < _firstFrame.Length) return _firstFrame.AsSpan(offset, _frameRowBytes);
    offset -= _firstFrame.Length;
    foreach (byte[] strip in _strips)
    {
      if (offset < strip.Length) return strip.AsSpan(offset, _frameRowBytes);
      offset -= strip.Length;
    }
    return _trailing.AsSpan(offset, _frameRowBytes);
  }

  public CapturedFrame BuildFrame(CancellationToken cancellationToken = default)
  {
    byte[] normalized = new byte[checked(_frameRowBytes * FullHeight)];
    int offset = 0;
    void Copy(byte[] bytes)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Buffer.BlockCopy(bytes, 0, normalized, offset, bytes.Length);
      offset += bytes.Length;
    }
    Copy(_leading);
    Copy(_firstFrame);
    foreach (byte[] strip in _strips) Copy(strip);
    Copy(_trailing);
    if (!Horizontal && !Reversed) return new(normalized, Width, Height, Width * 4, "BGRA8");
    byte[] pixels = new byte[normalized.Length];
    for (int y = 0; y < FullHeight; y++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      int axis = Reversed ? FullHeight - 1 - y : y;
      for (int x = 0; x < _frameWidth; x++)
      {
        int target = Horizontal ? x * Width + axis : axis * Width + x;
        normalized.AsSpan((y * _frameWidth + x) * 4, 4).CopyTo(pixels.AsSpan(target * 4, 4));
      }
    }
    return new(pixels, Width, Height, Width * 4, "BGRA8");
  }

  public static int GetMinimumOverlap(int frameHeight)
  {
    if (frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(frameHeight));
    return Math.Max(MinimumOverlapFloor, frameHeight / 4);
  }

  private byte[] Normalize(CapturedFrame frame, CancellationToken token)
  {
    byte[] pixels = new byte[checked(frame.Width * frame.Height * 4)];
    int axisLength = Horizontal ? frame.Width : frame.Height;
    for (int y = 0; y < axisLength; y++)
    {
      token.ThrowIfCancellationRequested();
      int axis = Reversed ? axisLength - 1 - y : y;
      if (!Horizontal)
      {
        frame.Pixels.AsSpan(axis * frame.Stride, _frameRowBytes).CopyTo(pixels.AsSpan(y * _frameRowBytes));
        continue;
      }
      for (int x = 0; x < _frameWidth; x++)
        frame.Pixels.AsSpan(x * frame.Stride + axis * 4, 4).CopyTo(pixels.AsSpan((y * _frameWidth + x) * 4, 4));
    }
    return pixels;
  }

  private static void ValidateFrameGeometry(CapturedFrame frame, string paramName)
  {
    if (!string.Equals(frame.PixelFormat, "BGRA8", StringComparison.Ordinal))
      throw new ArgumentException("Frame pixel format must be BGRA8.", paramName);
    if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > MaximumDimension ||
        frame.Height > MaximumDimension || (long)frame.Width * frame.Height > MaximumPixels ||
        frame.Stride < (long)frame.Width * 4)
      throw new ArgumentOutOfRangeException(paramName, "Invalid frame dimensions, stride or pixel limit.");
    if (frame.Pixels is null || frame.Pixels.LongLength != (long)frame.Stride * frame.Height)
      throw new ArgumentException("Frame pixel buffer length does not match its geometry.", paramName);
  }

  private bool HasNoVerticalVariation(byte[] packed, CancellationToken token)
  {
    for (int y = 1; y < _frameHeight; y++)
    {
      token.ThrowIfCancellationRequested();
      if (!packed.AsSpan(0, _frameRowBytes).SequenceEqual(packed.AsSpan(y * _frameRowBytes, _frameRowBytes)))
        return false;
    }
    return true;
  }

  private bool VerifyAlignment(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current,
    int scroll, CancellationToken token)
  {
    int overlap = _frameHeight - Math.Abs(scroll);
    int down = Math.Max(scroll, 0), up = Math.Max(-scroll, 0);
    for (int k = 0; k < SampledRowCount; k++)
    {
      int row = (int)((long)k * (overlap - 1) / (SampledRowCount - 1));
      if (!RowMatches(previous, row + down, current, row + up)) return false;
    }
    for (int row = 0; row < overlap; row++)
    {
      token.ThrowIfCancellationRequested();
      if (!RowMatches(previous, row + down, current, row + up)) return false;
    }
    return true;
  }

  private bool RowMatches(ReadOnlySpan<byte> left, int leftRow, ReadOnlySpan<byte> right, int rightRow) =>
    left.Slice(leftRow * _frameRowBytes, _frameRowBytes)
      .SequenceEqual(right.Slice(rightRow * _frameRowBytes, _frameRowBytes));
}
