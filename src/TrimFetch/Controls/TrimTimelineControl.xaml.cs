using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using TrimFetch.Helpers;
using TrimFetch.Services;

namespace TrimFetch.Controls;

public sealed partial class TrimTimelineControl : UserControl
{
    private const double HandleWidth = 22;
    private const double SelectionFrameBorderThickness = 4;
    private const double TimelineEdgeInset = 12;
    private const double MinRangeSeconds = 0.25;
    private static double FilmstripHeight => FilmstripLayout.FilmstripHeight;
    private static double TrackInset => FilmstripLayout.TrackInset;
    private const double DefaultFrameAspectRatio = FilmstripLayout.DefaultAspectRatio;

    private readonly List<string> _framePaths = [];
    private readonly List<Image> _filmstripImages = [];
    private bool _isUpdating;
    private INotifyCollectionChanged? _frameCollection;
    private InputCursor? _defaultCursor;
    private InputCursor? _handCursor;
    private double _lastFilmstripWidth = -1;
    private string _lastFrameSignature = string.Empty;
    private int _builtSlotCount;
    private int _builtPathCount;
    private bool _filmstripUpdateQueued;
    private bool _filmstripNeedsRebuild;
    private DispatcherQueueTimer? _sizeDebounceTimer;
    private double _lastWaveformWidth = -1;
    private string _lastWaveformSignature = string.Empty;

    public TrimTimelineControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler<double>? SeekRequested;

    public double StartSeconds
    {
        get => (double)GetValue(StartSecondsProperty);
        set => SetValue(StartSecondsProperty, value);
    }

    public static readonly DependencyProperty StartSecondsProperty = DependencyProperty.Register(
        nameof(StartSeconds),
        typeof(double),
        typeof(TrimTimelineControl),
        new PropertyMetadata(0d, OnTimelinePropertyChanged));

    public double EndSeconds
    {
        get => (double)GetValue(EndSecondsProperty);
        set => SetValue(EndSecondsProperty, value);
    }

    public static readonly DependencyProperty EndSecondsProperty = DependencyProperty.Register(
        nameof(EndSeconds),
        typeof(double),
        typeof(TrimTimelineControl),
        new PropertyMetadata(15d, OnTimelinePropertyChanged));

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(
        nameof(DurationSeconds),
        typeof(double),
        typeof(TrimTimelineControl),
        new PropertyMetadata(15d, OnTimelinePropertyChanged));

    public double PlaybackPositionSeconds
    {
        get => (double)GetValue(PlaybackPositionSecondsProperty);
        set => SetValue(PlaybackPositionSecondsProperty, value);
    }

    public static readonly DependencyProperty PlaybackPositionSecondsProperty = DependencyProperty.Register(
        nameof(PlaybackPositionSeconds),
        typeof(double),
        typeof(TrimTimelineControl),
        new PropertyMetadata(0d, OnPlaybackPositionChanged));

    public IEnumerable<string> FramePaths
    {
        get => (IEnumerable<string>)GetValue(FramePathsProperty);
        set => SetValue(FramePathsProperty, value);
    }

    public static readonly DependencyProperty FramePathsProperty = DependencyProperty.Register(
        nameof(FramePaths),
        typeof(IEnumerable<string>),
        typeof(TrimTimelineControl),
        new PropertyMetadata(Array.Empty<string>(), OnFramePathsChanged));

    public int ExpectedFrameCount
    {
        get => (int)GetValue(ExpectedFrameCountProperty);
        set => SetValue(ExpectedFrameCountProperty, value);
    }

    public static readonly DependencyProperty ExpectedFrameCountProperty = DependencyProperty.Register(
        nameof(ExpectedFrameCount),
        typeof(int),
        typeof(TrimTimelineControl),
        new PropertyMetadata(FilmstripLayout.DefaultFrameCount, OnExpectedFrameCountChanged));

    public double FrameAspectRatio
    {
        get => (double)GetValue(FrameAspectRatioProperty);
        set => SetValue(FrameAspectRatioProperty, value);
    }

    public static readonly DependencyProperty FrameAspectRatioProperty = DependencyProperty.Register(
        nameof(FrameAspectRatio),
        typeof(double),
        typeof(TrimTimelineControl),
        new PropertyMetadata(DefaultFrameAspectRatio, OnFrameAspectRatioChanged));

    public bool IsAudioMode
    {
        get => (bool)GetValue(IsAudioModeProperty);
        set => SetValue(IsAudioModeProperty, value);
    }

    public static readonly DependencyProperty IsAudioModeProperty = DependencyProperty.Register(
        nameof(IsAudioMode),
        typeof(bool),
        typeof(TrimTimelineControl),
        new PropertyMetadata(false, OnAudioModeChanged));

    public float[]? WaveformPeaks
    {
        get => (float[]?)GetValue(WaveformPeaksProperty);
        set => SetValue(WaveformPeaksProperty, value);
    }

    public static readonly DependencyProperty WaveformPeaksProperty = DependencyProperty.Register(
        nameof(WaveformPeaks),
        typeof(float[]),
        typeof(TrimTimelineControl),
        new PropertyMetadata(null, OnWaveformPeaksChanged));

    private static void OnAudioModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control.UpdateAudioModeVisual();
        }
    }

    private static void OnWaveformPeaksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control._lastWaveformSignature = string.Empty;
            control.DrawWaveformPeaks();
        }
    }

    private static void OnFrameAspectRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control._lastFrameSignature = string.Empty;
            control.UpdateLayoutVisuals(rebuildFilmstrip: true);
        }
    }

    private static void OnExpectedFrameCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control._lastFrameSignature = string.Empty;
            control.UpdateLayoutVisuals(rebuildFilmstrip: true);
        }
    }

    private static void OnTimelinePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control && !control._isUpdating)
        {
            control.NormalizeSelection();
            control.UpdateLayoutVisuals(rebuildFilmstrip: false);
        }
    }

    private static void OnPlaybackPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control.UpdatePlayhead();
        }
    }

    private static void OnFramePathsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrimTimelineControl control)
        {
            control.ApplyFramePaths(e.NewValue as IEnumerable<string>);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _defaultCursor = ProtectedCursor;
        _sizeDebounceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sizeDebounceTimer.Interval = TimeSpan.FromMilliseconds(32);
        _sizeDebounceTimer.Tick += (_, _) =>
        {
            _sizeDebounceTimer.Stop();
            UpdateLayoutVisuals(rebuildFilmstrip: true);
        };

        ApplyFramePaths(FramePaths);
        UpdateClipBounds();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_frameCollection is not null)
        {
            _frameCollection.CollectionChanged -= FrameCollection_Changed;
            _frameCollection = null;
        }

        _sizeDebounceTimer?.Stop();
        _sizeDebounceTimer = null;
    }

    private void ApplyFramePaths(IEnumerable<string>? paths)
    {
        if (_frameCollection is not null)
        {
            _frameCollection.CollectionChanged -= FrameCollection_Changed;
            _frameCollection = null;
        }

        _framePaths.Clear();
        if (paths is not null)
        {
            _framePaths.AddRange(paths.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        if (paths is INotifyCollectionChanged collection)
        {
            _frameCollection = collection;
            _frameCollection.CollectionChanged += FrameCollection_Changed;
        }

        _lastFrameSignature = string.Empty;
        UpdateLayoutVisuals(rebuildFilmstrip: true);
    }

    private void FrameCollection_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_filmstripUpdateQueued)
        {
            return;
        }

        _filmstripUpdateQueued = true;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _filmstripUpdateQueued = false;
            if (sender is IEnumerable<string> livePaths)
            {
                ApplyFramePaths(livePaths);
                return;
            }

            ApplyFramePaths(FramePaths);
        });
    }

    private void ClipHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateClipBounds();

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClipBounds();
        _sizeDebounceTimer?.Stop();
        _sizeDebounceTimer?.Start();
    }

    private void UpdateClipBounds()
    {
        var width = ClipHost.ActualWidth;
        var height = ClipHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            ClipHost.Clip = null;
            return;
        }

        ClipHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, height),
        };
    }

    private void SeekTarget_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);
        ProtectedCursor = _handCursor;
    }

    private void SeekTarget_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = _defaultCursor;
    }

    private void SeekTarget_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineCanvas).Position;
        var seconds = Math.Clamp(SecondsFromX(point.X), 0, EffectiveDuration());
        SeekRequested?.Invoke(this, seconds);
        UpdatePlayhead();
    }

    private void StartThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var next = SecondsFromX(StartX() + e.HorizontalChange);
        StartSeconds = Math.Clamp(next, 0, EndSeconds - MinRangeSeconds);
    }

    private void EndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var next = SecondsFromX(EndX() + e.HorizontalChange);
        EndSeconds = Math.Clamp(next, StartSeconds + MinRangeSeconds, EffectiveDuration());
    }

    private void StartThumb_DragCompleted(object sender, DragCompletedEventArgs e) =>
        SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void EndThumb_DragCompleted(object sender, DragCompletedEventArgs e) =>
        SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void NormalizeSelection()
    {
        var duration = EffectiveDuration();
        var start = Math.Clamp(StartSeconds, 0, Math.Max(duration - MinRangeSeconds, 0));
        var end = Math.Clamp(EndSeconds, start + MinRangeSeconds, duration);

        _isUpdating = true;
        StartSeconds = start;
        EndSeconds = end;
        _isUpdating = false;
    }

    private void UpdateLayoutVisuals(bool rebuildFilmstrip)
    {
        if (TimelineCanvas.ActualWidth <= 0)
        {
            if (rebuildFilmstrip && _framePaths.Count > 0)
            {
                _filmstripNeedsRebuild = true;
            }

            return;
        }

        NormalizeSelection();

        var width = TimelineCanvas.ActualWidth;
        var trackWidth = TrackLayoutWidth();
        var trackRight = TrackLayoutRight();

        Canvas.SetLeft(TrackBackground, TimelineEdgeInset);
        TrackBackground.Width = trackWidth;

        var startX = StartX();
        var endX = EndX();
        var filmstripWidth = Math.Max(0, trackWidth - (TrackInset * 2));
        var frameInset = SelectionFrameBorderThickness / 2;
        var frameLeft = Math.Clamp(startX + frameInset, TimelineEdgeInset, trackRight);
        var frameRight = Math.Clamp(endX - frameInset, frameLeft, trackRight);
        var frameWidth = Math.Max(HandleWidth, frameRight - frameLeft);
        var thumbMin = TimelineEdgeInset;
        var thumbMax = trackRight - HandleWidth;

        Canvas.SetLeft(StartThumb, Math.Clamp(startX, thumbMin, thumbMax));
        Canvas.SetLeft(EndThumb, Math.Clamp(endX - HandleWidth, thumbMin, thumbMax));
        Canvas.SetLeft(SelectionFrame, frameLeft);
        SelectionFrame.Width = frameWidth;

        Canvas.SetLeft(LeftDim, TimelineEdgeInset);
        LeftDim.Width = Math.Max(0, startX - TimelineEdgeInset);

        Canvas.SetLeft(RightDim, endX);
        RightDim.Width = Math.Max(0, trackRight - endX);

        Canvas.SetLeft(FilmstripClip, TimelineEdgeInset + TrackInset);
        FilmstripClip.Width = filmstripWidth;
        FilmstripClip.Height = FilmstripHeight;

        Canvas.SetLeft(SeekTarget, TimelineEdgeInset);
        SeekTarget.Width = trackWidth;

        if (rebuildFilmstrip || _filmstripNeedsRebuild)
        {
            _filmstripNeedsRebuild = false;
            if (!IsAudioMode)
            {
                EnsureFilmstrip(filmstripWidth);
            }
        }

        if (IsAudioMode)
        {
            DrawWaveformPeaks();
        }

        UpdatePlayhead();
        UpdateClipBounds();
    }

    private void UpdateAudioModeVisual()
    {
        FilmstripHost.Visibility = IsAudioMode ? Visibility.Collapsed : Visibility.Visible;
        WaveformCanvas.Visibility = IsAudioMode ? Visibility.Visible : Visibility.Collapsed;
        _lastWaveformSignature = string.Empty;

        if (IsAudioMode)
        {
            DrawWaveformPeaks();
        }
        else
        {
            WaveformCanvas.Children.Clear();
            UpdateLayoutVisuals(rebuildFilmstrip: true);
        }
    }

    private void DrawWaveformPeaks()
    {
        if (!IsAudioMode || TimelineCanvas.ActualWidth <= 0)
        {
            return;
        }

        var width = Math.Max(0, TrackLayoutWidth() - (TrackInset * 2));
        var peaks = WaveformPeaks;
        if (peaks is null || peaks.Length < 2)
        {
            WaveformCanvas.Children.Clear();
            WaveformCanvas.Width = width;
            WaveformCanvas.Height = FilmstripHeight;
            return;
        }

        var columnCount = peaks.Length / 2;
        var sampleCount = (int)Math.Clamp(Math.Floor(width), 64, 480);
        var signature = $"{columnCount}|{sampleCount}|{width:0.#}|{peaks[0]:0.####}|{peaks[^1]:0.####}";
        if (Math.Abs(width - _lastWaveformWidth) < 0.5 && signature == _lastWaveformSignature)
        {
            return;
        }

        _lastWaveformWidth = width;
        _lastWaveformSignature = signature;

        WaveformCanvas.Children.Clear();
        WaveformCanvas.Width = width;
        WaveformCanvas.Height = FilmstripHeight;

        var centerY = FilmstripHeight / 2;
        var halfHeight = (FilmstripHeight - 6) / 2;

        WaveformCanvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = FilmstripHeight,
            Fill = new SolidColorBrush(Color.FromArgb(255, 24, 28, 34)),
        });

        WaveformCanvas.Children.Add(new Line
        {
            X1 = 0,
            Y1 = centerY,
            X2 = width,
            Y2 = centerY,
            Stroke = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
            StrokeThickness = 1,
        });

        var amplitudes = ResampleWaveformAmplitudes(peaks, sampleCount);
        WaveformCanvas.Children.Add(BuildSymmetricWaveformPath(amplitudes, width, centerY, halfHeight));
    }

    private static float[] ResampleWaveformAmplitudes(float[] peaks, int targetCount)
    {
        var columnCount = peaks.Length / 2;
        if (columnCount <= 0 || targetCount <= 0)
        {
            return [];
        }

        var source = new float[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var min = peaks[i * 2];
            var max = peaks[i * 2 + 1];
            source[i] = Math.Clamp(Math.Max(Math.Abs(min), Math.Abs(max)), 0f, 1f);
        }

        if (targetCount == columnCount)
        {
            return source;
        }

        var result = new float[targetCount];
        if (targetCount < columnCount)
        {
            var bucketSize = (double)columnCount / targetCount;
            for (var i = 0; i < targetCount; i++)
            {
                var start = (int)Math.Floor(i * bucketSize);
                var end = (int)Math.Floor((i + 1) * bucketSize);
                end = Math.Min(end, columnCount);
                if (end <= start)
                {
                    end = Math.Min(start + 1, columnCount);
                }

                var peak = 0f;
                for (var j = start; j < end; j++)
                {
                    peak = Math.Max(peak, source[j]);
                }

                result[i] = peak;
            }

            return result;
        }

        for (var i = 0; i < targetCount; i++)
        {
            var position = (double)i / (targetCount - 1) * (columnCount - 1);
            var index = (int)Math.Floor(position);
            var next = Math.Min(index + 1, columnCount - 1);
            var blend = position - index;
            result[i] = (float)(source[index] * (1 - blend) + source[next] * blend);
        }

        return result;
    }

    private static Microsoft.UI.Xaml.Shapes.Path BuildSymmetricWaveformPath(
        float[] amplitudes,
        double width,
        double centerY,
        double halfHeight)
    {
        var count = amplitudes.Length;
        var step = width / count;
        var figure = new PathFigure
        {
            IsClosed = true,
            StartPoint = new Point(0, centerY),
        };

        for (var i = 0; i < count; i++)
        {
            var x = (i + 0.5) * step;
            var amp = Math.Max(0.04, amplitudes[i]);
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(x, centerY - (amp * halfHeight)),
            });
        }

        for (var i = count - 1; i >= 0; i--)
        {
            var x = (i + 0.5) * step;
            var amp = Math.Max(0.04, amplitudes[i]);
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(x, centerY + (amp * halfHeight)),
            });
        }

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Fill = new SolidColorBrush(Color.FromArgb(220, 255, 214, 10)),
            Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 230, 120)),
            StrokeThickness = 0.75,
        };
    }

    private void EnsureFilmstrip(double width)
    {
        var slotCount = GetSlotCount();
        if (slotCount <= 0)
        {
            DrawPlaceholderStrip(width);
            return;
        }

        if (TryAppendFilmstripFrames(width, slotCount))
        {
            return;
        }

        var signature = BuildFilmstripSignature(slotCount);
        if (Math.Abs(width - _lastFilmstripWidth) < 0.5 && signature == _lastFrameSignature)
        {
            return;
        }

        BuildFilmstrip(width, slotCount, signature);
        _lastFilmstripWidth = width;
        _lastFrameSignature = signature;
    }

    private int GetSlotCount() =>
        FilmstripLayout.ClampFrameCount(Math.Max(ExpectedFrameCount, _framePaths.Count));

    private bool TryAppendFilmstripFrames(double width, int slotCount)
    {
        if (_builtSlotCount != slotCount
            || Math.Abs(width - _lastFilmstripWidth) >= 0.5
            || _framePaths.Count <= _builtPathCount
            || _filmstripImages.Count < _framePaths.Count)
        {
            return false;
        }

        var columnWidth = FilmstripLayout.CalculateColumnWidth(width, slotCount);
        for (var column = _builtPathCount; column < _framePaths.Count; column++)
        {
            ActivateFilmstripSlot(column, columnWidth, _framePaths[column]);
        }

        _builtPathCount = _framePaths.Count;
        return true;
    }

    private void ActivateFilmstripSlot(int column, double columnWidth, string path)
    {
        UIElement? existing = null;
        for (var i = 0; i < FilmstripHost.Children.Count; i++)
        {
            if (FilmstripHost.Children[i] is FrameworkElement child
                && Grid.GetColumn(child) == column)
            {
                existing = child;
                break;
            }
        }

        if (existing is Grid existingCell && existingCell.Children[0] is Image existingImage)
        {
            if (column < _filmstripImages.Count)
            {
                _filmstripImages[column] = existingImage;
            }

            _ = SetFilmstripImageSourceAsync(existingImage, path);
            return;
        }

        if (existing is not null)
        {
            FilmstripHost.Children.Remove(existing);
        }

        var cell = CreateImageCell(columnWidth);
        var image = (Image)cell.Children[0];
        Grid.SetRow(cell, 0);
        Grid.SetColumn(cell, column);
        FilmstripHost.Children.Add(cell);

        while (_filmstripImages.Count <= column)
        {
            _filmstripImages.Add(new Image());
        }

        _filmstripImages[column] = image;
        _ = SetFilmstripImageSourceAsync(image, path);
    }

    private string BuildFilmstripSignature(int slotCount)
    {
        var builder = new StringBuilder(_framePaths.Count * 48 + 24);
        builder.Append(FrameAspectRatio.ToString("0.####", CultureInfo.InvariantCulture));
        builder.Append('|').Append(slotCount);

        foreach (var path in _framePaths)
        {
            builder.Append('|').Append(path);
        }

        return builder.ToString();
    }

    private void BuildFilmstrip(double width, int slotCount, string signature)
    {
        _ = signature;
        FilmstripHost.Children.Clear();
        FilmstripHost.ColumnDefinitions.Clear();
        FilmstripHost.RowDefinitions.Clear();
        _filmstripImages.Clear();
        _builtSlotCount = slotCount;
        _builtPathCount = _framePaths.Count;

        var columnWidth = FilmstripLayout.CalculateColumnWidth(width, slotCount);
        FilmstripHost.Width = width;
        FilmstripHost.HorizontalAlignment = HorizontalAlignment.Left;
        FilmstripHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var column = 0; column < slotCount; column++)
        {
            FilmstripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });

            if (column < _framePaths.Count)
            {
                var cell = CreateImageCell(columnWidth);
                var image = (Image)cell.Children[0];
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, column);
                FilmstripHost.Children.Add(cell);
                _filmstripImages.Add(image);
                _ = SetFilmstripImageSourceAsync(image, _framePaths[column]);
            }
            else
            {
                var block = CreatePlaceholderCell(column, columnWidth);
                Grid.SetRow(block, 0);
                Grid.SetColumn(block, column);
                FilmstripHost.Children.Add(block);
                _filmstripImages.Add(new Image());
            }
        }
    }

    private static Grid CreateImageCell(double columnWidth) =>
        new()
        {
            Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, columnWidth, FilmstripHeight),
            },
            Children =
            {
                new Image
                {
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                },
            },
        };

    private static Border CreatePlaceholderCell(int column, double columnWidth) =>
        new()
        {
            Width = columnWidth,
            Height = FilmstripHeight,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 36)),
            Margin = new Thickness(column == 0 ? 0 : 1, 0, 0, 0),
        };

    private int GetFilmstripDecodeWidth()
    {
        var aspect = FrameAspectRatio;
        if (!double.IsFinite(aspect) || aspect <= 0)
        {
            aspect = DefaultFrameAspectRatio;
        }

        return Math.Max(1, (int)Math.Round(ThumbnailStripService.FrameHeight * aspect));
    }

    private Task SetFilmstripImageSourceAsync(Image image, string path)
    {
        try
        {
            var bitmap = ImageFileSource.FromPath(path, GetFilmstripDecodeWidth());

            if (!_filmstripImages.Contains(image))
            {
                return Task.CompletedTask;
            }

            var isNew = image.Source == null && bitmap != null;
            if (isNew)
            {
                image.Opacity = 0;
            }

            image.Source = bitmap;

            if (isNew)
            {
                FadeInElement(image);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filmstrip image load failed for {path}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static void FadeInElement(UIElement element)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        sb.Begin();
    }

    public void RefreshLayout()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(RefreshLayout);
            return;
        }

        if (TimelineCanvas.ActualWidth <= 0)
        {
            _filmstripNeedsRebuild = true;
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (TimelineCanvas.ActualWidth > 0)
                {
                    UpdateLayoutVisuals(rebuildFilmstrip: true);
                }
            });
            return;
        }

        UpdateLayoutVisuals(rebuildFilmstrip: true);
    }

    private void DrawPlaceholderStrip(double availableWidth)
    {
        _builtSlotCount = 0;
        _builtPathCount = 0;
        _filmstripImages.Clear();
        FilmstripHost.Children.Clear();
        FilmstripHost.ColumnDefinitions.Clear();
        FilmstripHost.RowDefinitions.Clear();

        var placeholderCount = GetSlotCount();
        if (placeholderCount <= 0)
        {
            placeholderCount = FilmstripLayout.CalculateFrameCount(availableWidth, FrameAspectRatio);
        }

        var columnWidth = FilmstripLayout.CalculateColumnWidth(availableWidth, placeholderCount);
        FilmstripHost.Width = availableWidth;
        FilmstripHost.HorizontalAlignment = HorizontalAlignment.Left;
        FilmstripHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var column = 0; column < placeholderCount; column++)
        {
            FilmstripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });
            var block = CreatePlaceholderCell(column, columnWidth);
            Grid.SetRow(block, 0);
            Grid.SetColumn(block, column);
            FilmstripHost.Children.Add(block);
        }

        _builtSlotCount = placeholderCount;
    }

    private void UpdatePlayhead()
    {
        if (TimelineCanvas.ActualWidth <= 0 || EffectiveDuration() <= 0)
        {
            Playhead.Visibility = Visibility.Collapsed;
            return;
        }

        var position = Math.Clamp(PlaybackPositionSeconds, 0, EffectiveDuration());
        var x = Math.Clamp(XFromSeconds(position) - 1.5, TimelineEdgeInset, TrackLayoutRight() - 3);
        Canvas.SetLeft(Playhead, x);
        Playhead.Visibility = Visibility.Visible;
    }

    private double StartX() => XFromSeconds(StartSeconds);

    private double EndX() => XFromSeconds(EndSeconds);

    private double TrackLayoutWidth() =>
        Math.Max(0, TimelineCanvas.ActualWidth - (TimelineEdgeInset * 2));

    private double TrackLayoutRight() => TimelineEdgeInset + TrackLayoutWidth();

    private double XFromSeconds(double seconds)
    {
        var duration = EffectiveDuration();
        var trackWidth = TrackLayoutWidth();
        if (duration <= 0 || trackWidth <= 0)
        {
            return TimelineEdgeInset;
        }

        return TimelineEdgeInset + (Math.Clamp(seconds / duration, 0, 1) * trackWidth);
    }

    private double SecondsFromX(double x)
    {
        var duration = EffectiveDuration();
        var trackWidth = TrackLayoutWidth();
        if (duration <= 0 || trackWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp((x - TimelineEdgeInset) / trackWidth, 0, 1) * duration;
    }

    private double EffectiveDuration() => Math.Max(DurationSeconds, MinRangeSeconds);
}
