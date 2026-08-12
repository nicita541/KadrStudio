using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Models;
using KadrStudio.Services;

namespace KadrStudio.Controls;

public sealed class TimelineControl : FrameworkElement
{
    public const string MediaAssetDataFormat = "KadrStudio.MediaAssetId";
    public const double LeftGutterWidth = 96;

    private const double RulerHeight = 32;
    private const double TrackAreaTop = 35;
    private const double TrackHeight = 54;
    private const double TrackGap = 5;
    private const double TrackBottomPadding = 12;
    private const double TimelineEndPadding = 72;
    private const double ClipEdgeGrip = 12;
    private const double MinimumClipDuration = 0.1;
    private const double MinimumPixelsPerSecond = 0.0001;
    private const double MaximumPixelsPerSecond = 4000;
    private readonly Pen _gridPen = CreatePen(Color.FromRgb(48, 49, 57), 1);
    private readonly Pen _minorGridPen = CreatePen(Color.FromRgb(38, 39, 46), 1);
    private readonly Pen _playheadPen = CreatePen(Color.FromRgb(242, 84, 105), 2);
    private EditorProject? _project;
    private Guid? _selectedClipId;
    private double _playheadSeconds;
    private double _pixelsPerSecond = 72;
    private DragOperation _dragOperation;
    private Point _dragOrigin;
    private double _dragPointerOffsetSeconds;
    private TimelineClip? _dragClip;
    private TimelineClip? _dragOriginal;
    private readonly List<(TimelineClip Clip, TimelineClip Original)> _dragLinkedClips = [];
    private readonly Dictionary<string, ImageSource> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private TextOverlay? _dragTextOverlay;
    private TextOverlay? _dragTextOriginal;
    private Guid? _selectedTextOverlayId;
    private bool _isDraggingPlayhead;
    private bool _dragChanged;

    public TimelineControl()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = false;
    }

    public event EventHandler<ClipSelectedEventArgs>? ClipSelected;
    public event EventHandler<TextOverlaySelectedEventArgs>? TextOverlaySelected;
    public event EventHandler<TextOverlaySelectedEventArgs>? TextOverlayEditRequested;
    public event EventHandler<PlayheadChangedEventArgs>? PlayheadChanged;
    public event EventHandler<TimelineEditEventArgs>? EditStarted;
    public event EventHandler<TimelineEditEventArgs>? EditCompleted;
    public event EventHandler<AssetDroppedEventArgs>? AssetDropped;

    public EditorProject? Project
    {
        get => _project;
        set
        {
            if (ReferenceEquals(_project, value))
            {
                return;
            }

            DetachProject(_project);
            _project = value;
            AttachProject(_project);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public Guid? SelectedClipId
    {
        get => _selectedClipId;
        set
        {
            if (_selectedClipId == value)
            {
                return;
            }
            _selectedClipId = value;
            InvalidateVisual();
        }
    }

    public Guid? SelectedTextOverlayId
    {
        get => _selectedTextOverlayId;
        set
        {
            if (_selectedTextOverlayId == value)
            {
                return;
            }
            _selectedTextOverlayId = value;
            InvalidateVisual();
        }
    }

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            var bounded = Math.Clamp(value, 0, Math.Max(0, Project?.TimelineDisplayDuration ?? 0));
            if (Math.Abs(_playheadSeconds - bounded) < 0.0001)
            {
                return;
            }
            _playheadSeconds = bounded;
            InvalidateVisual();
        }
    }

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            var bounded = Math.Clamp(value, MinimumPixelsPerSecond, MaximumPixelsPerSecond);
            if (Math.Abs(_pixelsPerSecond - bounded) < 0.0000001)
            {
                return;
            }
            _pixelsPerSecond = bounded;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public double HorizontalViewportOffset { get; set; }
    public double HorizontalViewportWidth { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var duration = Math.Max(1, Project?.TimelineDisplayDuration ?? 0);
        var width = LeftGutterWidth + duration * PixelsPerSecond + TimelineEndPadding;
        if (!double.IsInfinity(availableSize.Width))
        {
            width = Math.Max(width, availableSize.Width);
        }
        return new Size(width, GetRequiredHeight());
    }

    protected override Size ArrangeOverride(Size finalSize)
        => new(Math.Max(finalSize.Width, DesiredSize.Width), Math.Max(finalSize.Height, GetRequiredHeight()));

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 16, 20)), null, new Rect(RenderSize));
        DrawRuler(context, dpi);

        var visualCount = GetTrackCount(TrackKind.Visual);
        var audioCount = GetTrackCount(TrackKind.Audio);
        if (HasTextTrack)
        {
            DrawTextTrack(context, dpi);
        }
        for (var index = 0; index < visualCount; index++)
        {
            DrawTrack(context, TrackKind.Visual, index, dpi);
        }
        for (var index = 0; index < audioCount; index++)
        {
            DrawTrack(context, TrackKind.Audio, index, dpi);
        }

        DrawClips(context, dpi);
        DrawTextOverlays(context, dpi);
        DrawSemanticFlags(context, dpi);
        DrawInOutSelection(context, dpi);
        DrawPlayhead(context);
        DrawStickyHeaders(context, dpi);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        var playheadX = LeftGutterWidth + PlayheadSeconds * PixelsPerSecond;
        if (point.Y <= RulerHeight && HitTestMarker(point) is { } marker)
        {
            PlayheadSeconds = marker.Start;
            PlayheadChanged?.Invoke(this, new PlayheadChangedEventArgs(marker.Start));
            BeginPlayheadDrag();
            e.Handled = true;
            return;
        }
        if (point.Y <= RulerHeight || Math.Abs(point.X - playheadX) <= 7)
        {
            SetPlayheadFromPoint(point);
            BeginPlayheadDrag();
            e.Handled = true;
            return;
        }

        if (HitTestTextOverlay(point) is { } textOverlay)
        {
            SelectTextOverlay(textOverlay);
            if (e.ClickCount >= 2)
            {
                TextOverlayEditRequested?.Invoke(this, new TextOverlaySelectedEventArgs(textOverlay.Id));
                e.Handled = true;
                return;
            }
            BeginTextOverlayDrag(textOverlay, point);
            e.Handled = true;
            return;
        }

        var hit = HitTestClip(point);
        if (hit is null)
        {
            SelectedClipId = null;
            SelectedTextOverlayId = null;
            ClipSelected?.Invoke(this, new ClipSelectedEventArgs(null));
            TextOverlaySelected?.Invoke(this, new TextOverlaySelectedEventArgs(null));
            SetPlayheadFromPoint(point);
            BeginPlayheadDrag();
            e.Handled = true;
            return;
        }

        SelectedTextOverlayId = null;
        TextOverlaySelected?.Invoke(this, new TextOverlaySelectedEventArgs(null));
        SelectedClipId = hit.Id;
        ClipSelected?.Invoke(this, new ClipSelectedEventArgs(hit.Id));
        _dragClip = hit;
        _dragOriginal = hit.Clone();
        _dragLinkedClips.Clear();
        if (hit.LinkGroupId is Guid linkGroupId && Project is not null)
        {
            _dragLinkedClips.AddRange(Project.Clips
                .Where(clip => clip.Id != hit.Id && clip.LinkGroupId == linkGroupId)
                .Select(clip => (clip, clip.Clone())));
        }
        _dragOrigin = point;
        _dragChanged = false;
        var rectangle = GetClipRectangle(hit);
        _dragPointerOffsetSeconds = Math.Max(0, (point.X - rectangle.Left) / PixelsPerSecond);
        _dragOperation = Math.Abs(point.X - rectangle.Left) <= ClipEdgeGrip
            ? DragOperation.TrimLeft
            : Math.Abs(point.X - rectangle.Right) <= ClipEdgeGrip
                ? DragOperation.TrimRight
                : DragOperation.Move;
        EditStarted?.Invoke(this, new TimelineEditEventArgs(hit.Id, false));
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (HitTestTextOverlay(point) is { } overlay)
        {
            SelectTextOverlay(overlay);
            return;
        }
        var hit = HitTestClip(point);
        SelectedTextOverlayId = null;
        TextOverlaySelected?.Invoke(this, new TextOverlaySelectedEventArgs(null));
        SelectedClipId = hit?.Id;
        ClipSelected?.Invoke(this, new ClipSelectedEventArgs(hit?.Id));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_isDraggingPlayhead && e.LeftButton == MouseButtonState.Pressed)
        {
            SetPlayheadFromPoint(point);
            e.Handled = true;
            return;
        }
        if (_dragTextOverlay is not null && _dragTextOriginal is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            ApplyTextOverlayDrag(point);
            e.Handled = true;
            return;
        }
        if (_dragClip is not null && _dragOriginal is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            ApplyDrag(point);
            e.Handled = true;
            return;
        }

        if (HitTestTextOverlay(point) is { } textOverlay)
        {
            var textRectangle = GetTextOverlayRectangle(textOverlay);
            Cursor = Math.Abs(point.X - textRectangle.Left) <= ClipEdgeGrip || Math.Abs(point.X - textRectangle.Right) <= ClipEdgeGrip
                ? Cursors.SizeWE
                : Cursors.SizeAll;
            return;
        }
        var hit = HitTestClip(point);
        if (hit is null)
        {
            Cursor = point.Y <= RulerHeight ? Cursors.Hand : Cursors.Arrow;
            return;
        }
        var rectangle = GetClipRectangle(hit);
        Cursor = Math.Abs(point.X - rectangle.Left) <= ClipEdgeGrip || Math.Abs(point.X - rectangle.Right) <= ClipEdgeGrip
            ? Cursors.SizeWE
            : Cursors.SizeAll;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDraggingPlayhead)
        {
            _isDraggingPlayhead = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        if (_dragTextOverlay is not null)
        {
            var overlayId = _dragTextOverlay.Id;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            _dragTextOverlay = null;
            _dragTextOriginal = null;
            _dragOperation = DragOperation.None;
            EditCompleted?.Invoke(this, new TimelineEditEventArgs(overlayId, _dragChanged));
            _dragChanged = false;
            e.Handled = true;
            return;
        }
        if (_dragClip is null)
        {
            return;
        }
        var clipId = _dragClip.Id;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _dragClip = null;
        _dragOriginal = null;
        _dragLinkedClips.Clear();
        _dragOperation = DragOperation.None;
        EditCompleted?.Invoke(this, new TimelineEditEventArgs(clipId, _dragChanged));
        _dragChanged = false;
        e.Handled = true;
    }

    private void BeginPlayheadDrag()
    {
        _isDraggingPlayhead = true;
        CaptureMouse();
        Cursor = Cursors.SizeWE;
    }

    private void SelectTextOverlay(TextOverlay overlay)
    {
        SelectedClipId = null;
        ClipSelected?.Invoke(this, new ClipSelectedEventArgs(null));
        SelectedTextOverlayId = overlay.Id;
        TextOverlaySelected?.Invoke(this, new TextOverlaySelectedEventArgs(overlay.Id));
    }

    private void BeginTextOverlayDrag(TextOverlay overlay, Point point)
    {
        _dragTextOverlay = overlay;
        _dragTextOriginal = overlay.Clone();
        _dragOrigin = point;
        _dragChanged = false;
        var rectangle = GetTextOverlayRectangle(overlay);
        _dragPointerOffsetSeconds = Math.Max(0, (point.X - rectangle.Left) / PixelsPerSecond);
        _dragOperation = Math.Abs(point.X - rectangle.Left) <= ClipEdgeGrip
            ? DragOperation.TrimLeft
            : Math.Abs(point.X - rectangle.Right) <= ClipEdgeGrip
                ? DragOperation.TrimRight
                : DragOperation.Move;
        EditStarted?.Invoke(this, new TimelineEditEventArgs(overlay.Id, false));
        CaptureMouse();
    }

    private void ApplyTextOverlayDrag(Point point)
    {
        if (_dragTextOverlay is null || _dragTextOriginal is null)
        {
            return;
        }
        var delta = SnapDuration((point.X - _dragOrigin.X) / PixelsPerSecond);
        switch (_dragOperation)
        {
            case DragOperation.TrimLeft:
            {
                var applied = Math.Clamp(delta, -_dragTextOriginal.Start, _dragTextOriginal.Duration - MinimumClipDuration);
                _dragTextOverlay.Start = _dragTextOriginal.Start + applied;
                _dragTextOverlay.Duration = _dragTextOriginal.Duration - applied;
                break;
            }
            case DragOperation.TrimRight:
                _dragTextOverlay.Duration = Math.Max(MinimumClipDuration, _dragTextOriginal.Duration + delta);
                break;
            case DragOperation.Move:
                _dragTextOverlay.Start = SnapTime(Math.Max(0,
                    (point.X - LeftGutterWidth) / PixelsPerSecond - _dragPointerOffsetSeconds));
                break;
        }
        _dragChanged |= !TextOverlayEquals(_dragTextOverlay, _dragTextOriginal);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            base.OnMouseWheel(e);
            return;
        }
        PixelsPerSecond *= e.Delta > 0 ? 1.16 : 1 / 1.16;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(MediaAssetDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent(MediaAssetDataFormat) ||
            e.Data.GetData(MediaAssetDataFormat) is not string idText ||
            !Guid.TryParse(idText, out var assetId))
        {
            return;
        }
        var point = e.GetPosition(this);
        var time = Math.Max(0, (point.X - LeftGutterWidth) / PixelsPerSecond);
        var target = GetTrackAt(point.Y) ?? new TrackAddress(TrackKind.Visual, 0);
        AssetDropped?.Invoke(this, new AssetDroppedEventArgs(assetId, time, target.Kind, target.Index));
        e.Handled = true;
    }

    private void ApplyDrag(Point point)
    {
        if (_dragClip is null || _dragOriginal is null || Project is null)
        {
            return;
        }
        var deltaSeconds = (point.X - _dragOrigin.X) / PixelsPerSecond;
        var asset = Project.FindAsset(_dragClip.AssetId);
        switch (_dragOperation)
        {
            case DragOperation.TrimLeft:
            {
                var minimumDelta = Math.Max(-_dragOriginal.SourceStart, -_dragOriginal.Start);
                var previous = GetPreviousClip(_dragOriginal);
                if (previous is not null)
                {
                    minimumDelta = Math.Max(minimumDelta, previous.End - _dragOriginal.Start);
                }
                var maximumDelta = _dragOriginal.Duration - MinimumClipDuration;
                var applied = Math.Clamp(SnapDuration(deltaSeconds), minimumDelta, maximumDelta);
                _dragClip.SourceStart = _dragOriginal.SourceStart + applied;
                _dragClip.Duration = _dragOriginal.Duration - applied;
                _dragClip.Start = Math.Max(0, _dragOriginal.Start + applied);
                break;
            }
            case DragOperation.TrimRight:
            {
                var maximumDuration = asset?.Kind == MediaKind.Image
                    ? 3600
                    : Math.Max(MinimumClipDuration, (asset?.Duration ?? _dragOriginal.Duration) - _dragOriginal.SourceStart);
                var next = GetNextClip(_dragOriginal);
                if (next is not null)
                {
                    maximumDuration = Math.Min(maximumDuration, Math.Max(MinimumClipDuration, next.Start - _dragOriginal.Start));
                }
                _dragClip.Duration = Math.Clamp(
                    _dragOriginal.Duration + SnapDuration(deltaSeconds),
                    MinimumClipDuration,
                    maximumDuration);
                break;
            }
            case DragOperation.Move:
                MoveClip(point);
                break;
        }

        PropagateLinkedEdit();

        _dragChanged |= !ClipEquals(_dragClip, _dragOriginal);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void MoveClip(Point point)
    {
        if (Project is null || _dragClip is null)
        {
            return;
        }
        if (GetTrackAt(point.Y) is { } target && target.Kind == _dragClip.Track)
        {
            _dragClip.TrackIndex = target.Index;
        }
        var desiredStart = SnapTime(Math.Max(0, (point.X - LeftGutterWidth) / PixelsPerSecond - _dragPointerOffsetSeconds));
        _dragClip.Start = FindNonOverlappingStart(_dragClip, desiredStart);
    }

    private void PropagateLinkedEdit()
    {
        if (_dragClip is null || _dragOriginal is null || _dragLinkedClips.Count == 0)
        {
            return;
        }

        var startDelta = _dragClip.Start - _dragOriginal.Start;
        var sourceDelta = _dragClip.SourceStart - _dragOriginal.SourceStart;
        var durationDelta = _dragClip.Duration - _dragOriginal.Duration;
        foreach (var (clip, original) in _dragLinkedClips)
        {
            clip.Start = Math.Max(0, original.Start + startDelta);
            clip.SourceStart = Math.Max(0, original.SourceStart + sourceDelta);
            clip.Duration = Math.Max(MinimumClipDuration, original.Duration + durationDelta);
        }
    }

    private double FindNonOverlappingStart(TimelineClip moving, double desiredStart)
    {
        if (Project is null)
        {
            return Math.Max(0, desiredStart);
        }
        var others = Project.GetTrackClips(moving.Track, moving.TrackIndex)
            .Where(clip => clip.Id != moving.Id &&
                           (moving.LinkGroupId is null || clip.LinkGroupId != moving.LinkGroupId))
            .ToList();
        var candidate = Math.Max(0, desiredStart);
        for (var attempt = 0; attempt <= others.Count; attempt++)
        {
            var overlap = others.FirstOrDefault(clip => candidate < clip.End - 0.0001 && candidate + moving.Duration > clip.Start + 0.0001);
            if (overlap is null)
            {
                break;
            }
            candidate = candidate + moving.Duration / 2 < overlap.Start + overlap.Duration / 2
                ? Math.Max(0, overlap.Start - moving.Duration)
                : overlap.End;
        }
        return SnapTime(candidate);
    }

    private void DrawRuler(DrawingContext context, double dpi)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 21, 26)), null, new Rect(0, 0, RenderSize.Width, RulerHeight));
        context.DrawLine(_gridPen, new Point(0, RulerHeight), new Point(RenderSize.Width, RulerHeight));
        var majorStep = NiceTimeStep(92 / PixelsPerSecond);
        var minorStep = majorStep / 5;
        var maximumTime = Math.Max(1, (RenderSize.Width - LeftGutterWidth) / PixelsPerSecond);
        for (var time = 0.0; time <= maximumTime + majorStep; time += minorStep)
        {
            var x = LeftGutterWidth + time * PixelsPerSecond;
            var isMajor = Math.Abs(time / majorStep - Math.Round(time / majorStep)) < 0.001;
            context.DrawLine(isMajor ? _gridPen : _minorGridPen, new Point(x, isMajor ? 14 : 23), new Point(x, RulerHeight));
            if (isMajor)
            {
                context.DrawText(CreateText(FormatRulerTime(time), 9.5, Color.FromRgb(167, 168, 176), dpi), new Point(x + 4, 1));
            }
        }
    }

    private void DrawTrack(DrawingContext context, TrackKind kind, int index, double dpi)
    {
        var top = GetTrackTop(kind, index);
        var color = kind == TrackKind.Visual ? Color.FromRgb(26, 29, 37) : Color.FromRgb(23, 31, 28);
        context.DrawRoundedRectangle(
            new SolidColorBrush(color),
            _gridPen,
            new Rect(LeftGutterWidth, top, Math.Max(0, RenderSize.Width - LeftGutterWidth - 7), TrackHeight),
            4,
            4);
        var hasClips = Project?.Clips.Any(clip => clip.Track == kind && clip.TrackIndex == index) ?? false;
        if (!hasClips)
        {
            var last = index == GetTrackCount(kind) - 1;
            var hint = last ? "+ следующая дорожка" : "Перетащите клип сюда";
            context.DrawText(CreateText(hint, 10.5, Color.FromRgb(92, 94, 104), dpi), new Point(LeftGutterWidth + 15, top + 18));
        }
    }

    private void DrawTextTrack(DrawingContext context, double dpi)
    {
        var top = GetTextTrackTop();
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(35, 24, 45)),
            _gridPen,
            new Rect(LeftGutterWidth, top, Math.Max(0, RenderSize.Width - LeftGutterWidth - 7), TrackHeight),
            4,
            4);
    }

    private void DrawStickyHeaders(DrawingContext context, double dpi)
    {
        var left = Math.Clamp(HorizontalViewportOffset, 0, Math.Max(0, RenderSize.Width - LeftGutterWidth));
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 21, 26)), null,
            new Rect(left, 0, LeftGutterWidth, RulerHeight));
        context.DrawLine(_gridPen, new Point(left + LeftGutterWidth, 0), new Point(left + LeftGutterWidth, RenderSize.Height));
        var visibleTime = Math.Max(0, HorizontalViewportOffset / PixelsPerSecond);
        context.DrawText(CreateText(FormatRulerTime(visibleTime), 9.5, Color.FromRgb(167, 168, 176), dpi), new Point(left + 8, 1));
        if (HasTextTrack) DrawStickyTrackHeader(context, left, GetTextTrackTop(), "T1", Color.FromRgb(216, 180, 254), dpi);
        for (var index = 0; index < GetTrackCount(TrackKind.Visual); index++)
            DrawStickyTrackHeader(context, left, GetTrackTop(TrackKind.Visual, index), $"V{index + 1}", Color.FromRgb(89, 145, 245), dpi);
        for (var index = 0; index < GetTrackCount(TrackKind.Audio); index++)
            DrawStickyTrackHeader(context, left, GetTrackTop(TrackKind.Audio, index), $"A{index + 1}", Color.FromRgb(55, 190, 128), dpi);
    }

    private void DrawStickyTrackHeader(DrawingContext context, double left, double top, string label, Color color, double dpi)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 16, 20)), null,
            new Rect(left, top, LeftGutterWidth, TrackHeight));
        context.DrawText(CreateText(label, 10, color, dpi, FontWeights.SemiBold), new Point(left + 14, top + 18));
    }

    private void DrawClips(DrawingContext context, double dpi)
    {
        if (Project is null)
        {
            return;
        }
        foreach (var clip in Project.Clips.OrderBy(item => item.Track).ThenBy(item => item.TrackIndex).ThenBy(item => item.Start))
        {
            var rectangle = GetClipRectangle(clip);
            var visibleLeft = HorizontalViewportOffset + LeftGutterWidth;
            var visibleRight = HorizontalViewportWidth > 0
                ? HorizontalViewportOffset + HorizontalViewportWidth
                : RenderSize.Width;
            if (rectangle.Right < visibleLeft || rectangle.Left > visibleRight)
            {
                continue;
            }
            var asset = Project.FindAsset(clip.AssetId);
            var selected = SelectedClipId == clip.Id;
            var baseColor = clip.Track == TrackKind.Audio
                ? Color.FromRgb(31, 136, 91)
                : asset?.Kind == MediaKind.Image
                    ? Color.FromRgb(147, 84, 189)
                    : Color.FromRgb(54, 111, 205);
            var outline = selected ? CreatePen(Color.FromRgb(246, 239, 255), 2.5) : CreatePen(Color.FromArgb(120, 255, 255, 255), 1);
            context.DrawRoundedRectangle(new SolidColorBrush(baseColor), outline, rectangle, 5, 5);
            if (clip.Track == TrackKind.Audio)
            {
                DrawWaveform(context, clip, asset, rectangle, dpi);
            }
            else
            {
                DrawVideoFrames(context, clip, asset, rectangle);
                DrawClipAnalysisOverlays(context, clip, rectangle, dpi);
            }

            var title = asset?.Name ?? "Файл не найден";
            var clippedTitle = title.Length > 36 ? title[..33] + "…" : title;
            context.PushClip(new RectangleGeometry(new Rect(rectangle.X + 7, rectangle.Y, Math.Max(0, rectangle.Width - 14), rectangle.Height)));
            if (rectangle.Width >= 42)
            {
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 8, 10, 16)), null,
                    new Rect(rectangle.X + 5, rectangle.Y + 4, Math.Min(rectangle.Width - 10, 250), 17), 3, 3);
                context.DrawText(CreateText(clippedTitle, 10.5, Colors.White, dpi, FontWeights.SemiBold), new Point(rectangle.X + 9, rectangle.Y + 7));
            }
            if (clip.LinkGroupId.HasValue && rectangle.Width >= 70)
            {
                context.DrawText(CreateText("↔", 11, Color.FromRgb(225, 216, 255), dpi, FontWeights.Bold), new Point(rectangle.Right - 20, rectangle.Y + 6));
            }
            if (rectangle.Width >= 58)
            {
                context.DrawText(CreateText(FormatClipDuration(clip.Duration), 9.5, Color.FromArgb(220, 255, 255, 255), dpi), new Point(rectangle.X + 9, rectangle.Bottom - 20));
            }
            context.Pop();
            if (selected)
            {
                var handleBrush = new SolidColorBrush(Colors.White);
                context.DrawRoundedRectangle(handleBrush, null, new Rect(rectangle.Left - 2, rectangle.Top + 6, 5, rectangle.Height - 12), 2, 2);
                context.DrawRoundedRectangle(handleBrush, null, new Rect(rectangle.Right - 3, rectangle.Top + 6, 5, rectangle.Height - 12), 2, 2);
            }
        }
    }

    private void DrawVideoFrames(DrawingContext context, TimelineClip clip, MediaAsset? asset, Rect rectangle)
    {
        if (asset is null || asset.TimelineFramePaths.Count == 0)
        {
            return;
        }

        var visible = GetVisibleContentRectangle(rectangle, 1);
        if (visible.IsEmpty) return;
        context.PushClip(new RectangleGeometry(visible));
        context.PushOpacity(0.88);
        const double tileWidth = 82;
        var firstTile = rectangle.Left + Math.Floor((visible.Left - rectangle.Left) / tileWidth) * tileWidth;
        for (var left = firstTile; left < visible.Right; left += tileWidth)
        {
            var center = Math.Min(rectangle.Right, left + tileWidth / 2);
            var localRatio = rectangle.Width <= 0 ? 0 : (center - rectangle.Left) / rectangle.Width;
            var sourceTime = clip.SourceStart + localRatio * clip.Duration;
            var sourceRatio = asset.Duration <= 0 ? 0 : Math.Clamp(sourceTime / asset.Duration, 0, 1);
            var frameIndex = Math.Clamp((int)Math.Round(sourceRatio * (asset.TimelineFramePaths.Count - 1)),
                0, asset.TimelineFramePaths.Count - 1);
            if (TryLoadImage(asset.TimelineFramePaths[frameIndex]) is not { } image)
            {
                continue;
            }
            context.DrawImage(image,
                new Rect(left, rectangle.Top, Math.Min(tileWidth, rectangle.Right - left), rectangle.Height));
        }
        context.Pop();
        context.Pop();
    }

    private void DrawClipAnalysisOverlays(DrawingContext context, TimelineClip clip, Rect rectangle, double dpi)
    {
        if (Project is null)
        {
            return;
        }
        foreach (var marker in Project.Markers.Where(marker => marker.End > clip.Start && marker.Start < clip.End).OrderBy(marker => MarkerPriority(marker.Kind)))
        {
            var start = Math.Max(marker.Start, clip.Start);
            var end = Math.Min(marker.End, clip.End);
            var left = rectangle.Left + (start - clip.Start) * PixelsPerSecond;
            var width = Math.Max(1.5, (end - start) * PixelsPerSecond);
            var color = MarkerColor(marker.Kind);
            if (marker.Kind == MarkerKind.Scene)
            {
                context.DrawLine(CreatePen(Color.FromArgb(150, color.R, color.G, color.B), 1.2), new Point(left, rectangle.Top + 1), new Point(left, rectangle.Bottom - 1));
                continue;
            }
            if (marker.Kind is MarkerKind.BlackFrame or MarkerKind.Silence or MarkerKind.Freeze)
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(145, color.R, color.G, color.B)), null, new Rect(left, rectangle.Bottom - 6, width, 5));
                continue;
            }

            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(92, color.R, color.G, color.B)), null, new Rect(left, rectangle.Top + 1, width, rectangle.Height - 2));
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(left, rectangle.Top + 1, width, 4));
            context.DrawLine(CreatePen(Color.FromArgb(245, color.R, color.G, color.B), 1.8),
                new Point(left, rectangle.Top), new Point(left, rectangle.Bottom));
            context.DrawLine(CreatePen(Color.FromArgb(245, color.R, color.G, color.B), 1.8),
                new Point(left + width, rectangle.Top), new Point(left + width, rectangle.Bottom));
            if (width >= 54)
            {
                context.PushClip(new RectangleGeometry(new Rect(left + 4, rectangle.Top + 3, Math.Max(0, width - 8), 17)));
                context.DrawText(CreateText(marker.Title, 9, Colors.White, dpi, FontWeights.SemiBold), new Point(left + 5, rectangle.Top + 4));
                context.Pop();
            }
        }
    }

    private void DrawSemanticFlags(DrawingContext context, double dpi)
    {
        if (Project is null)
        {
            return;
        }
        foreach (var marker in Project.Markers.Where(marker => MarkerPriority(marker.Kind) == 2).OrderBy(marker => marker.Start))
        {
            var x = LeftGutterWidth + marker.Start * PixelsPerSecond;
            if (x < LeftGutterWidth || x > RenderSize.Width)
            {
                continue;
            }
            var color = MarkerColor(marker.Kind);
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, 18, 2, 14));
            var flag = new StreamGeometry();
            using (var geometry = flag.Open())
            {
                geometry.BeginFigure(new Point(x + 2, 18), true, true);
                geometry.LineTo(new Point(x + 10, 21), true, false);
                geometry.LineTo(new Point(x + 2, 25), true, false);
            }
            flag.Freeze();
            context.DrawGeometry(new SolidColorBrush(color), null, flag);
        }
    }

    private void DrawTextOverlays(DrawingContext context, double dpi)
    {
        if (Project is null || Project.TextOverlays.Count == 0)
        {
            return;
        }

        foreach (var overlay in Project.TextOverlays.OrderBy(item => item.Start))
        {
            var rect = GetTextOverlayRectangle(overlay);
            if (rect.Left > RenderSize.Width || rect.Right < LeftGutterWidth)
            {
                continue;
            }

            var color = overlay.IsSubtitle ? Color.FromRgb(236, 72, 153) : Color.FromRgb(168, 85, 247);
            var selected = SelectedTextOverlayId == overlay.Id;
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)),
                selected ? CreatePen(Colors.White, 2.5) : CreatePen(Color.FromArgb(190, 255, 255, 255), 1), rect, 5, 5);
            context.PushClip(new RectangleGeometry(new Rect(rect.Left + 6, rect.Top, Math.Max(0, rect.Width - 12), rect.Height)));
            if (rect.Width >= 30)
            {
                var title = overlay.IsSubtitle ? $"CC  {overlay.Text}" : $"T  {overlay.Text}";
                context.DrawText(CreateText(title, 10, Colors.White, dpi, FontWeights.SemiBold),
                    new Point(rect.Left + 9, rect.Top + 7));
            }
            if (rect.Width >= 55)
            {
                context.DrawText(CreateText(FormatClipDuration(overlay.Duration), 9, Color.FromArgb(220, 255, 255, 255), dpi),
                    new Point(rect.Left + 9, rect.Bottom - 19));
            }
            context.Pop();
            if (selected)
            {
                context.DrawRoundedRectangle(Brushes.White, null,
                    new Rect(rect.Left - 2, rect.Top + 6, 5, rect.Height - 12), 2, 2);
                context.DrawRoundedRectangle(Brushes.White, null,
                    new Rect(rect.Right - 3, rect.Top + 6, 5, rect.Height - 12), 2, 2);
            }
        }
    }

    private void DrawWaveform(DrawingContext context, TimelineClip clip, MediaAsset? asset, Rect rectangle, double dpi)
    {
        if (asset is { WaveformPeaks.Count: > 0 })
        {
            var wholeWaveformArea = new Rect(rectangle.Left + 4, rectangle.Top + 21,
                Math.Max(0, rectangle.Width - 8), Math.Max(0, rectangle.Height - 26));
            var waveformArea = GetVisibleContentRectangle(wholeWaveformArea, 3);
            if (waveformArea.IsEmpty) return;
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(74, 5, 48, 35)), null,
                waveformArea, 3, 3);

            // Адаптивная плотность: около 100 отдельных столбиков в видимой части клипа.
            // При увеличении таймлайна каждый столбик охватывает меньший фрагмент исходного звука.
            var columnCount = Math.Max(1, Math.Min(120, (int)Math.Ceiling(waveformArea.Width / 3.2)));
            var step = waveformArea.Width / columnCount;
            var barWidth = Math.Max(1, Math.Min(2.2, step * 0.62));
            var visibleClipStartRatio = rectangle.Width <= 0 ? 0 : Math.Clamp((waveformArea.Left - rectangle.Left) / rectangle.Width, 0, 1);
            var visibleClipEndRatio = rectangle.Width <= 0 ? 1 : Math.Clamp((waveformArea.Right - rectangle.Left) / rectangle.Width, 0, 1);
            var sourceStartRatio = asset.Duration <= 0 ? 0 : Math.Clamp((clip.SourceStart + visibleClipStartRatio * clip.Duration) / asset.Duration, 0, 1);
            var sourceEndRatio = asset.Duration <= 0 ? 1 : Math.Clamp((clip.SourceStart + visibleClipEndRatio * clip.Duration) / asset.Duration, 0, 1);
            var visiblePeaks = TimelineMediaCacheService.AggregateVisiblePeaks(
                asset.WaveformPeaks, sourceStartRatio, sourceEndRatio, columnCount);
            var barBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
            var quietBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            for (var column = 0; column < columnCount; column++)
            {
                var peak = visiblePeaks[column];
                var height = Math.Max(1.5, peak * (waveformArea.Height - 2));
                var x = waveformArea.Left + column * step + (step - barWidth) / 2;
                context.DrawRoundedRectangle(peak < 0.13 ? quietBrush : barBrush, null,
                    new Rect(x, waveformArea.Bottom - height - 1, barWidth, height), 0.8, 0.8);
            }
            return;
        }

        if (asset?.WaveformPath is { } path && TryLoadImage(path) is { } waveform)
        {
            var startRatio = asset.Duration <= 0 ? 0 : Math.Clamp(clip.SourceStart / asset.Duration, 0, 1);
            var widthRatio = asset.Duration <= 0 ? 1 : Math.Clamp(clip.Duration / asset.Duration, 0.0001, 1 - startRatio);
            var brush = new ImageBrush(waveform)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(startRatio, 0, widthRatio, 1),
                AlignmentY = AlignmentY.Bottom,
                Opacity = 1
            };
            var waveformArea = new Rect(rectangle.Left + 3, rectangle.Top + 21,
                Math.Max(0, rectangle.Width - 6), Math.Max(0, rectangle.Height - 25));
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(82, 7, 52, 38)), null,
                waveformArea, 3, 3);
            context.DrawRoundedRectangle(brush, null,
                waveformArea, 3, 3);
            context.DrawLine(CreatePen(Color.FromArgb(215, 183, 247, 213), 1),
                new Point(waveformArea.Left, waveformArea.Bottom - 1),
                new Point(waveformArea.Right, waveformArea.Bottom - 1));
            return;
        }

        context.DrawLine(CreatePen(Color.FromArgb(150, 154, 243, 199), 1),
            new Point(rectangle.Left + 5, rectangle.Bottom - 5), new Point(rectangle.Right - 5, rectangle.Bottom - 5));
        if (rectangle.Width >= 110)
        {
            context.DrawText(CreateText("Форма волны готовится…", 8.5, Color.FromArgb(210, 210, 245, 229), dpi),
                new Point(rectangle.Left + 8, rectangle.Bottom - 19));
        }
    }

    private Rect GetVisibleContentRectangle(Rect source, double inset)
    {
        var visibleLeft = HorizontalViewportOffset + LeftGutterWidth + inset;
        var visibleRight = HorizontalViewportWidth > 0
            ? HorizontalViewportOffset + HorizontalViewportWidth - inset
            : RenderSize.Width - inset;
        var left = Math.Max(source.Left, visibleLeft);
        var right = Math.Min(source.Right, visibleRight);
        return right > left ? new Rect(left, source.Top, right - left, source.Height) : Rect.Empty;
    }

    private ImageSource? TryLoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        if (_imageCache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _imageCache[path] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void DrawPlayhead(DrawingContext context)
    {
        var x = LeftGutterWidth + PlayheadSeconds * PixelsPerSecond;
        context.DrawLine(_playheadPen, new Point(x, 18), new Point(x, RenderSize.Height));
        var marker = new StreamGeometry();
        using (var geometry = marker.Open())
        {
            geometry.BeginFigure(new Point(x - 6, 17), true, true);
            geometry.LineTo(new Point(x + 6, 17), true, false);
            geometry.LineTo(new Point(x, 25), true, false);
        }
        marker.Freeze();
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(242, 84, 105)), null, marker);
    }

    private void DrawInOutSelection(DrawingContext context, double dpi)
    {
        if (Project is null || (Project.InPoint is null && Project.OutPoint is null))
        {
            return;
        }

        var inPoint = Math.Clamp(Project.InPoint ?? 0, 0, Project.TimelineDisplayDuration);
        var outPoint = Math.Clamp(Project.OutPoint ?? Project.TimelineDisplayDuration, inPoint, Project.TimelineDisplayDuration);
        var inX = LeftGutterWidth + inPoint * PixelsPerSecond;
        var outX = LeftGutterWidth + outPoint * PixelsPerSecond;
        var selectionTop = RulerHeight;
        var selectionHeight = Math.Max(0, RenderSize.Height - selectionTop);
        if (inX > LeftGutterWidth)
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(115, 3, 4, 8)),
                null,
                new Rect(LeftGutterWidth, selectionTop, inX - LeftGutterWidth, selectionHeight));
        }
        if (outX < RenderSize.Width)
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(115, 3, 4, 8)),
                null,
                new Rect(outX, selectionTop, RenderSize.Width - outX, selectionHeight));
        }
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(28, 34, 211, 153)),
            null,
            new Rect(inX, selectionTop, Math.Max(1, outX - inX), selectionHeight));

        if (Project.InPoint.HasValue)
        {
            var pen = CreatePen(Color.FromRgb(52, 211, 153), 2);
            context.DrawLine(pen, new Point(inX, 10), new Point(inX, RenderSize.Height));
            context.DrawText(CreateText("IN", 9, Color.FromRgb(167, 243, 208), dpi, FontWeights.Bold), new Point(inX + 4, 2));
        }
        if (Project.OutPoint.HasValue)
        {
            var pen = CreatePen(Color.FromRgb(251, 191, 36), 2);
            context.DrawLine(pen, new Point(outX, 10), new Point(outX, RenderSize.Height));
            context.DrawText(CreateText("OUT", 9, Color.FromRgb(253, 230, 138), dpi, FontWeights.Bold), new Point(Math.Max(LeftGutterWidth, outX - 28), 2));
        }
    }

    private TimelineClip? HitTestClip(Point point)
        => Project?.Clips
            .OrderByDescending(clip => clip.Track)
            .ThenByDescending(clip => clip.TrackIndex)
            .ThenByDescending(clip => clip.Start)
            .FirstOrDefault(clip => GetClipRectangle(clip).Contains(point));

    private TextOverlay? HitTestTextOverlay(Point point)
        => Project?.TextOverlays
            .OrderByDescending(overlay => overlay.Start)
            .FirstOrDefault(overlay => GetTextOverlayRectangle(overlay).Contains(point));

    private TimelineMarker? HitTestMarker(Point point)
    {
        if (Project is null || point.X < LeftGutterWidth)
        {
            return null;
        }
        var time = (point.X - LeftGutterWidth) / PixelsPerSecond;
        var tolerance = 10 / PixelsPerSecond;
        return Project.Markers
            .Where(marker => MarkerPriority(marker.Kind) == 2)
            .OrderBy(marker => Math.Abs(marker.Start - time))
            .FirstOrDefault(marker => Math.Abs(marker.Start - time) <= tolerance);
    }

    private Rect GetClipRectangle(TimelineClip clip)
    {
        var top = GetTrackTop(clip.Track, clip.TrackIndex);
        var left = LeftGutterWidth + clip.Start * PixelsPerSecond;
        var width = Math.Max(10, clip.Duration * PixelsPerSecond);
        return new Rect(left, top + 3, width, TrackHeight - 6);
    }

    private Rect GetTextOverlayRectangle(TextOverlay overlay)
    {
        var left = LeftGutterWidth + overlay.Start * PixelsPerSecond;
        return new Rect(left, GetTextTrackTop() + 3, Math.Max(10, overlay.Duration * PixelsPerSecond), TrackHeight - 6);
    }

    private TrackAddress? GetTrackAt(double y)
    {
        if (y < TrackAreaTop)
        {
            return null;
        }
        var slot = (int)((y - TrackAreaTop) / (TrackHeight + TrackGap));
        var visualCount = GetTrackCount(TrackKind.Visual);
        var textCount = HasTextTrack ? 1 : 0;
        if (slot < 0)
        {
            return null;
        }
        if (slot < textCount)
        {
            return new TrackAddress(TrackKind.Visual, 0);
        }
        var visualSlot = slot - textCount;
        if (visualSlot < visualCount)
        {
            return new TrackAddress(TrackKind.Visual, visualCount - 1 - visualSlot);
        }
        var audioIndex = visualSlot - visualCount;
        return audioIndex < GetTrackCount(TrackKind.Audio) ? new TrackAddress(TrackKind.Audio, audioIndex) : null;
    }

    private double GetTrackTop(TrackKind kind, int index)
    {
        var textCount = HasTextTrack ? 1 : 0;
        var slot = kind == TrackKind.Visual
            ? textCount + GetTrackCount(TrackKind.Visual) - 1 - index
            : textCount + GetTrackCount(TrackKind.Visual) + index;
        return TrackAreaTop + slot * (TrackHeight + TrackGap);
    }

    private double GetTextTrackTop() => TrackAreaTop;

    private bool HasTextTrack => Project?.TextOverlays.Count > 0;

    private int GetTrackCount(TrackKind kind)
        => kind == TrackKind.Visual ? Project?.VisualTrackCount ?? 2 : Project?.AudioTrackCount ?? 2;

    private double GetRequiredHeight()
        => TrackAreaTop + (GetTrackCount(TrackKind.Visual) + GetTrackCount(TrackKind.Audio) + (HasTextTrack ? 1 : 0)) *
            (TrackHeight + TrackGap) - TrackGap + TrackBottomPadding;

    private TimelineClip? GetPreviousClip(TimelineClip clip)
        => Project?.GetTrackClips(clip.Track, clip.TrackIndex)
            .Where(item => item.Id != clip.Id && item.End <= clip.Start + 0.0001)
            .OrderByDescending(item => item.End)
            .FirstOrDefault();

    private TimelineClip? GetNextClip(TimelineClip clip)
        => Project?.GetTrackClips(clip.Track, clip.TrackIndex)
            .Where(item => item.Id != clip.Id && item.Start >= clip.End - 0.0001)
            .OrderBy(item => item.Start)
            .FirstOrDefault();

    private double SnapTime(double seconds)
    {
        var frameRate = Math.Max(1, Project?.FrameRate ?? 30);
        return Math.Round(seconds * frameRate) / frameRate;
    }

    private double SnapDuration(double seconds) => SnapTime(seconds);

    private void SetPlayheadFromPoint(Point point)
    {
        if (point.X < LeftGutterWidth)
        {
            return;
        }
        PlayheadSeconds = Math.Max(0, (point.X - LeftGutterWidth) / PixelsPerSecond);
        PlayheadChanged?.Invoke(this, new PlayheadChangedEventArgs(PlayheadSeconds));
    }

    private void AttachProject(EditorProject? project)
    {
        if (project is null)
        {
            return;
        }
        project.Clips.CollectionChanged += OnClipsChanged;
        project.Markers.CollectionChanged += OnMarkersChanged;
        project.TextOverlays.CollectionChanged += OnTextOverlaysChanged;
        project.Media.CollectionChanged += OnMediaChanged;
        project.PropertyChanged += OnProjectPropertyChanged;
        foreach (var clip in project.Clips)
        {
            clip.PropertyChanged += OnClipPropertyChanged;
        }
        foreach (var overlay in project.TextOverlays)
        {
            overlay.PropertyChanged += OnTextOverlayPropertyChanged;
        }
        foreach (var asset in project.Media)
        {
            asset.PropertyChanged += OnMediaPropertyChanged;
        }
    }

    private void DetachProject(EditorProject? project)
    {
        if (project is null)
        {
            return;
        }
        project.Clips.CollectionChanged -= OnClipsChanged;
        project.Markers.CollectionChanged -= OnMarkersChanged;
        project.TextOverlays.CollectionChanged -= OnTextOverlaysChanged;
        project.Media.CollectionChanged -= OnMediaChanged;
        project.PropertyChanged -= OnProjectPropertyChanged;
        foreach (var clip in project.Clips)
        {
            clip.PropertyChanged -= OnClipPropertyChanged;
        }
        foreach (var overlay in project.TextOverlays)
        {
            overlay.PropertyChanged -= OnTextOverlayPropertyChanged;
        }
        foreach (var asset in project.Media)
        {
            asset.PropertyChanged -= OnMediaPropertyChanged;
        }
    }

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TimelineClip clip in e.OldItems)
            {
                clip.PropertyChanged -= OnClipPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (TimelineClip clip in e.NewItems)
            {
                clip.PropertyChanged += OnClipPropertyChanged;
            }
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnMarkersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnTextOverlaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TextOverlay overlay in e.OldItems)
            {
                overlay.PropertyChanged -= OnTextOverlayPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (TextOverlay overlay in e.NewItems)
            {
                overlay.PropertyChanged += OnTextOverlayPropertyChanged;
            }
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnTextOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnMediaChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MediaAsset asset in e.OldItems)
            {
                asset.PropertyChanged -= OnMediaPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (MediaAsset asset in e.NewItems)
            {
                asset.PropertyChanged += OnMediaPropertyChanged;
            }
        }
        InvalidateVisual();
    }

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaAsset.TimelineFramePaths) or nameof(MediaAsset.WaveformPath) or nameof(MediaAsset.WaveformPeaks))
        {
            if (Dispatcher.CheckAccess())
            {
                InvalidateVisual();
            }
            else
            {
                _ = Dispatcher.BeginInvoke(InvalidateVisual);
            }
        }
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static double NiceTimeStep(double desiredSeconds)
    {
        if (desiredSeconds <= 0.01)
        {
            return 0.01;
        }
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(desiredSeconds)));
        var normalized = desiredSeconds / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static int MarkerPriority(MarkerKind kind) => kind switch
    {
        MarkerKind.Scene => 0,
        MarkerKind.BlackFrame or MarkerKind.Silence or MarkerKind.Freeze => 1,
        _ => 2
    };

    private static Color MarkerColor(MarkerKind kind) => kind switch
    {
        MarkerKind.Opening => Color.FromRgb(139, 92, 246),
        MarkerKind.Ending => Color.FromRgb(236, 72, 153),
        MarkerKind.PostCredits => Color.FromRgb(245, 158, 11),
        MarkerKind.Preview => Color.FromRgb(16, 185, 129),
        MarkerKind.Recap => Color.FromRgb(59, 130, 246),
        MarkerKind.BlackFrame => Color.FromRgb(107, 114, 128),
        MarkerKind.Silence => Color.FromRgb(34, 211, 238),
        MarkerKind.Freeze => Color.FromRgb(249, 115, 22),
        MarkerKind.Scene => Color.FromRgb(108, 168, 255),
        _ => Color.FromRgb(167, 139, 250)
    };

    private static FormattedText CreateText(string text, double size, Color color, double dpi, FontWeight? weight = null)
        => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            dpi);

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static string FormatRulerTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private static string FormatClipDuration(double seconds)
    {
        var safeSeconds = Math.Max(0, seconds);
        if (safeSeconds < 60)
        {
            return $"{safeSeconds:0.0} с";
        }
        return TimeSpan.FromSeconds(safeSeconds).ToString(safeSeconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");
    }

    private static bool ClipEquals(TimelineClip left, TimelineClip right)
        => left.Track == right.Track &&
           left.TrackIndex == right.TrackIndex &&
           Math.Abs(left.Start - right.Start) < 0.0001 &&
           Math.Abs(left.SourceStart - right.SourceStart) < 0.0001 &&
           Math.Abs(left.Duration - right.Duration) < 0.0001;

    private static bool TextOverlayEquals(TextOverlay left, TextOverlay right)
        => Math.Abs(left.Start - right.Start) < 0.0001 &&
           Math.Abs(left.Duration - right.Duration) < 0.0001;

    private readonly record struct TrackAddress(TrackKind Kind, int Index);

    private enum DragOperation
    {
        None,
        Move,
        TrimLeft,
        TrimRight
    }
}

public sealed class ClipSelectedEventArgs(Guid? clipId) : EventArgs
{
    public Guid? ClipId { get; } = clipId;
}

public sealed class TextOverlaySelectedEventArgs(Guid? overlayId) : EventArgs
{
    public Guid? OverlayId { get; } = overlayId;
}

public sealed class PlayheadChangedEventArgs(double seconds) : EventArgs
{
    public double Seconds { get; } = seconds;
}

public sealed class TimelineEditEventArgs(Guid clipId, bool changed) : EventArgs
{
    public Guid ClipId { get; } = clipId;
    public bool Changed { get; } = changed;
}

public sealed class AssetDroppedEventArgs(Guid assetId, double requestedStart, TrackKind requestedTrack, int requestedTrackIndex) : EventArgs
{
    public Guid AssetId { get; } = assetId;
    public double RequestedStart { get; } = requestedStart;
    public TrackKind RequestedTrack { get; } = requestedTrack;
    public int RequestedTrackIndex { get; } = requestedTrackIndex;
}
