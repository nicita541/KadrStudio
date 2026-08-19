using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Application.Editing;
using KadrStudio.Models;
using KadrStudio.Services;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;
using TimelineTime = KadrStudio.Core.Domain.TimelineTime;

namespace KadrStudio.Controls;

public sealed class TimelineControl : FrameworkElement
{
    private readonly WaveformRenderer _waveformRenderer = new();
    private readonly ThumbnailRenderer _thumbnailRenderer;
    private readonly TimelineInteractionController _interaction = new();
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
    private TimelineReadModel? _document;
    private Guid? _selectedClipId;
    private double _playheadSeconds;
    private double _pixelsPerSecond = 72;
    private Point _dragOrigin;
    private TimelineClip? _dragClip;
    private TimelineClip? _dragOriginal;
    private readonly List<(TimelineClip Clip, TimelineClip Original)> _dragLinkedClips = [];
    private TextOverlay? _dragTextOverlay;
    private TextOverlay? _dragTextOriginal;
    private Guid? _selectedTextOverlayId;
    private bool _dragChanged;

    public TimelineControl()
    {
        _thumbnailRenderer = new ThumbnailRenderer(Dispatcher, InvalidateVisual);
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
    public event EventHandler<TimelineEditRequestedEventArgs>? EditRequested;
    public event EventHandler<AssetDroppedEventArgs>? AssetDropped;

    public Func<Guid, TimelineTime, CancellationToken, Task<string?>>? ThumbnailRequest
    {
        get => _thumbnailRenderer.Request;
        set => _thumbnailRenderer.Request = value;
    }

    public ProjectViewState? Project
    {
        set
        {
            _document = value is null ? null : TimelineReadModel.From(value);
            _thumbnailRenderer.BeginViewportGeneration();
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
            var bounded = Math.Clamp(value, 0, Math.Max(0, _document?.TimelineDisplayDuration ?? 0));
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

    private double _horizontalViewportOffset;
    private double _horizontalViewportWidth;
    public double HorizontalViewportOffset
    {
        get => _horizontalViewportOffset;
        set
        {
            if (Math.Abs(_horizontalViewportOffset - value) < 0.1) return;
            _horizontalViewportOffset = value;
            _thumbnailRenderer.BeginViewportGeneration();
        }
    }
    public double HorizontalViewportWidth
    {
        get => _horizontalViewportWidth;
        set
        {
            if (Math.Abs(_horizontalViewportWidth - value) < 0.1) return;
            _horizontalViewportWidth = value;
            _thumbnailRenderer.BeginViewportGeneration();
        }
    }
    public double VerticalViewportOffset { get; set; }
    public double VerticalViewportHeight { get; set; }
    private TimelineViewport Viewport => new(PixelsPerSecond, HorizontalViewportOffset, HorizontalViewportWidth, LeftGutterWidth);
    private TrackLayout Layout => new(GetTrackCount(TrackKind.Visual), GetTrackCount(TrackKind.Audio), HasTextTrack,
        TrackAreaTop, TrackHeight, TrackGap, TrackBottomPadding);

    protected override Size MeasureOverride(Size availableSize)
    {
        var duration = Math.Max(1, _document?.TimelineDisplayDuration ?? 0);
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
            if (IsTrackVisible(Layout.TextTrackTop)) DrawTextTrack(context, dpi);
        }
        for (var index = 0; index < visualCount; index++)
        {
            if (IsTrackVisible(Layout.GetTrackTop(TrackKind.Visual, index)))
                DrawTrack(context, TrackKind.Visual, index, dpi);
        }
        for (var index = 0; index < audioCount; index++)
        {
            if (IsTrackVisible(Layout.GetTrackTop(TrackKind.Audio, index)))
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
        var playheadX = Viewport.TimeToContentX(PlayheadSeconds);
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
        _dragClip = hit.Clone();
        _dragOriginal = hit.Clone();
        _dragLinkedClips.Clear();
        if (hit.LinkGroupId is Guid linkGroupId && _document is not null)
        {
            _dragLinkedClips.AddRange(_document.Clips
                .Where(clip => clip.Id != hit.Id && clip.LinkGroupId == linkGroupId)
                .Select(clip => (clip.Clone(), clip.Clone())));
        }
        _dragOrigin = point;
        _dragChanged = false;
        var rectangle = GetClipRectangle(hit);
        var pointerOffset = Math.Max(0, (point.X - rectangle.Left) / PixelsPerSecond);
        var operation = Math.Abs(point.X - rectangle.Left) <= ClipEdgeGrip
            ? TimelineDragOperation.TrimLeft
            : Math.Abs(point.X - rectangle.Right) <= ClipEdgeGrip
                ? TimelineDragOperation.TrimRight
                : TimelineDragOperation.Move;
        _interaction.BeginDrag(operation, pointerOffset);
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
        if (_interaction.IsDraggingPlayhead && e.LeftButton == MouseButtonState.Pressed)
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
        if (_interaction.IsDraggingPlayhead)
        {
            _interaction.EndPlayheadDrag();
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
            return;
        }
        if (_dragTextOverlay is not null)
        {
            var draft = _dragTextOverlay.Clone();
            var overlayId = draft.Id;
            var changed = _dragChanged;
            var textOperation = _interaction.DragOperation;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            _dragTextOverlay = null;
            _dragTextOriginal = null;
            _interaction.EndDrag();
            if (changed)
            {
                EditRequested?.Invoke(this, new TimelineEditRequestedEventArgs(new TextTimelineEditIntent(
                    draft.Id,
                    MapOperation(textOperation),
                    TimelineTime.FromSeconds(draft.Start),
                    TimelineTime.FromSeconds(draft.Duration))));
            }
            EditCompleted?.Invoke(this, new TimelineEditEventArgs(overlayId, changed));
            _dragChanged = false;
            e.Handled = true;
            return;
        }
        if (_dragClip is null)
        {
            return;
        }
        var draftClip = _dragClip.Clone();
        var clipId = draftClip.Id;
        var operation = _interaction.DragOperation;
        var changedClip = _dragChanged;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _dragClip = null;
        _dragOriginal = null;
        _dragLinkedClips.Clear();
        _interaction.EndDrag();
        if (changedClip)
        {
            EditRequested?.Invoke(this, new TimelineEditRequestedEventArgs(new MediaTimelineEditIntent(
                draftClip.Id,
                MapOperation(operation),
                draftClip.Track == TrackKind.Visual ? CoreTrackKind.Visual : CoreTrackKind.Audio,
                draftClip.TrackIndex,
                TimelineTime.FromSeconds(draftClip.Start),
                TimelineTime.FromSeconds(draftClip.SourceStart),
                TimelineTime.FromSeconds(draftClip.Duration))));
        }
        EditCompleted?.Invoke(this, new TimelineEditEventArgs(clipId, changedClip));
        _dragChanged = false;
        e.Handled = true;
    }

    private void BeginPlayheadDrag()
    {
        _interaction.BeginPlayheadDrag();
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
        _dragTextOverlay = overlay.Clone();
        _dragTextOriginal = overlay.Clone();
        _dragOrigin = point;
        _dragChanged = false;
        var rectangle = GetTextOverlayRectangle(overlay);
        var pointerOffset = Math.Max(0, (point.X - rectangle.Left) / PixelsPerSecond);
        var operation = Math.Abs(point.X - rectangle.Left) <= ClipEdgeGrip
            ? TimelineDragOperation.TrimLeft
            : Math.Abs(point.X - rectangle.Right) <= ClipEdgeGrip
                ? TimelineDragOperation.TrimRight
                : TimelineDragOperation.Move;
        _interaction.BeginDrag(operation, pointerOffset);
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
        switch (_interaction.DragOperation)
        {
            case TimelineDragOperation.TrimLeft:
            {
                var applied = Math.Clamp(delta, -_dragTextOriginal.Start, _dragTextOriginal.Duration - MinimumClipDuration);
                _dragTextOverlay.Start = _dragTextOriginal.Start + applied;
                _dragTextOverlay.Duration = _dragTextOriginal.Duration - applied;
                break;
            }
            case TimelineDragOperation.TrimRight:
                _dragTextOverlay.Duration = Math.Max(MinimumClipDuration, _dragTextOriginal.Duration + delta);
                break;
            case TimelineDragOperation.Move:
                _dragTextOverlay.Start = SnapTime(Math.Max(0,
                    Viewport.ContentXToTime(point.X) - _interaction.PointerOffsetSeconds));
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
        var time = Viewport.ContentXToTime(point.X);
        var target = GetTrackAt(point.Y) ?? new TrackAddress(TrackKind.Visual, 0);
        AssetDropped?.Invoke(this, new AssetDroppedEventArgs(assetId, time, target.Kind, target.Index));
        e.Handled = true;
    }

    private void ApplyDrag(Point point)
    {
        if (_dragClip is null || _dragOriginal is null || _document is null)
        {
            return;
        }
        var deltaSeconds = (point.X - _dragOrigin.X) / PixelsPerSecond;
        var asset = _document.FindAsset(_dragClip.AssetId);
        switch (_interaction.DragOperation)
        {
            case TimelineDragOperation.TrimLeft:
            {
                var minimumDelta = Math.Max(-_dragOriginal.SourceStart, -_dragOriginal.Start);
                var previous = GetPreviousClip(_dragOriginal);
                if (previous is not null)
                {
                    minimumDelta = Math.Max(minimumDelta, previous.End - _dragOriginal.Start);
                }
                foreach (var (_, linkedOriginal) in _dragLinkedClips)
                {
                    minimumDelta = Math.Max(minimumDelta,
                        Math.Max(-linkedOriginal.SourceStart, -linkedOriginal.Start));
                    var linkedPrevious = GetPreviousClip(linkedOriginal);
                    if (linkedPrevious is not null)
                        minimumDelta = Math.Max(minimumDelta, linkedPrevious.End - linkedOriginal.Start);
                }
                var maximumDelta = _dragOriginal.Duration - MinimumClipDuration;
                var applied = Math.Clamp(SnapDuration(deltaSeconds), minimumDelta, maximumDelta);
                _dragClip.SourceStart = _dragOriginal.SourceStart + applied;
                _dragClip.Duration = _dragOriginal.Duration - applied;
                _dragClip.Start = Math.Max(0, _dragOriginal.Start + applied);
                break;
            }
            case TimelineDragOperation.TrimRight:
            {
                var maximumDuration = asset?.Kind == MediaKind.Image
                    ? 3600
                    : Math.Max(MinimumClipDuration, (asset?.Duration ?? _dragOriginal.Duration) - _dragOriginal.SourceStart);
                var next = GetNextClip(_dragOriginal);
                if (next is not null)
                {
                    maximumDuration = Math.Min(maximumDuration, Math.Max(MinimumClipDuration, next.Start - _dragOriginal.Start));
                }
                foreach (var (_, linkedOriginal) in _dragLinkedClips)
                {
                    var linkedAsset = _document.FindAsset(linkedOriginal.AssetId);
                    var linkedMaximum = linkedAsset?.Kind == MediaKind.Image
                        ? 3600
                        : Math.Max(MinimumClipDuration,
                            (linkedAsset?.Duration ?? linkedOriginal.Duration) - linkedOriginal.SourceStart);
                    var linkedNext = GetNextClip(linkedOriginal);
                    if (linkedNext is not null)
                        linkedMaximum = Math.Min(linkedMaximum,
                            Math.Max(MinimumClipDuration, linkedNext.Start - linkedOriginal.Start));
                    maximumDuration = Math.Min(maximumDuration,
                        _dragOriginal.Duration + linkedMaximum - linkedOriginal.Duration);
                }
                _dragClip.Duration = Math.Clamp(
                    _dragOriginal.Duration + SnapDuration(deltaSeconds),
                    MinimumClipDuration,
                    maximumDuration);
                break;
            }
            case TimelineDragOperation.Move:
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
        if (_document is null || _dragClip is null)
        {
            return;
        }
        if (GetTrackAt(point.Y) is { } target && target.Kind == _dragClip.Track)
        {
            _dragClip.TrackIndex = target.Index;
        }
        var desiredStart = SnapTime(Math.Max(0, Viewport.ContentXToTime(point.X) - _interaction.PointerOffsetSeconds));
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
        if (_document is null)
        {
            return Math.Max(0, desiredStart);
        }
        var movingIds = _dragLinkedClips.Select(item => item.Clip.Id).Append(moving.Id).ToHashSet();
        var candidate = Math.Max(0, desiredStart);
        var memberCount = _dragLinkedClips.Count + 1;
        for (var attempt = 0; attempt <= Math.Max(4, _document.Clips.Count * memberCount); attempt++)
        {
            var adjusted = false;
            var members = new[] { (Clip: moving, Original: _dragOriginal ?? moving) }
                .Concat(_dragLinkedClips);
            foreach (var (member, original) in members)
            {
                var relativeStart = original.Start - (_dragOriginal?.Start ?? moving.Start);
                var memberStart = candidate + relativeStart;
                if (memberStart < 0)
                {
                    candidate -= memberStart;
                    adjusted = true;
                    break;
                }
                var others = _document.GetTrackClips(member.Track, member.TrackIndex)
                    .Where(clip => !movingIds.Contains(clip.Id));
                var overlap = others.FirstOrDefault(clip =>
                    memberStart < clip.End - 0.0001 && memberStart + member.Duration > clip.Start + 0.0001);
                if (overlap is null) continue;
                candidate = memberStart + member.Duration / 2 < overlap.Start + overlap.Duration / 2
                    ? overlap.Start - member.Duration - relativeStart
                    : overlap.End - relativeStart;
                candidate = Math.Max(0, candidate);
                adjusted = true;
                break;
            }
            if (!adjusted) break;
        }
        return SnapTime(candidate);
    }

    private void DrawRuler(DrawingContext context, double dpi)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 21, 26)), null, new Rect(0, 0, RenderSize.Width, RulerHeight));
        context.DrawLine(_gridPen, new Point(0, RulerHeight), new Point(RenderSize.Width, RulerHeight));
        var majorStep = NiceTimeStep(92 / PixelsPerSecond);
        var minorStep = majorStep / 5;
        var maximumTime = Math.Max(1, Viewport.ContentXToTime(RenderSize.Width));
        for (var time = 0.0; time <= maximumTime + majorStep; time += minorStep)
        {
            var x = Viewport.TimeToContentX(time);
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
        var hasClips = _document?.Clips.Any(clip => clip.Track == kind && clip.TrackIndex == index) ?? false;
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
        var visibleTime = Viewport.VisibleTimelineStart;
        context.DrawText(CreateText(FormatRulerTime(visibleTime), 9.5, Color.FromRgb(167, 168, 176), dpi), new Point(left + 8, 1));
        if (HasTextTrack && IsTrackVisible(GetTextTrackTop()))
            DrawStickyTrackHeader(context, left, GetTextTrackTop(), "T1", Color.FromRgb(216, 180, 254), dpi);
        for (var index = 0; index < GetTrackCount(TrackKind.Visual); index++)
            if (IsTrackVisible(GetTrackTop(TrackKind.Visual, index)))
                DrawStickyTrackHeader(context, left, GetTrackTop(TrackKind.Visual, index), $"V{index + 1}", Color.FromRgb(89, 145, 245), dpi);
        for (var index = 0; index < GetTrackCount(TrackKind.Audio); index++)
            if (IsTrackVisible(GetTrackTop(TrackKind.Audio, index)))
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
        if (_document is null)
        {
            return;
        }
        foreach (var storedClip in _document.Clips.OrderBy(item => item.Track).ThenBy(item => item.TrackIndex).ThenBy(item => item.Start))
        {
            var clip = ResolveClipDraft(storedClip);
            var rectangle = GetClipRectangle(clip);
            if (!IsTrackVisible(rectangle.Top, rectangle.Height)) continue;
            var visibleLeft = Viewport.VisibleContentLeft;
            var visibleRight = Viewport.VisibleContentRight;
            if (rectangle.Right < visibleLeft || rectangle.Left > visibleRight)
            {
                continue;
            }
            var asset = _document.FindAsset(clip.AssetId);
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
        if (asset is null)
        {
            return;
        }

        var visible = GetVisibleContentRectangle(rectangle, 1);
        if (visible.IsEmpty) return;
        _thumbnailRenderer.Draw(context, clip, asset, rectangle, visible);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent is null) _thumbnailRenderer.BeginViewportGeneration();
    }

    private void DrawClipAnalysisOverlays(DrawingContext context, TimelineClip clip, Rect rectangle, double dpi)
    {
        if (_document is null)
        {
            return;
        }
        foreach (var marker in _document.Markers.Where(marker => marker.End > clip.Start && marker.Start < clip.End).OrderBy(marker => MarkerPriority(marker.Kind)))
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
        if (_document is null)
        {
            return;
        }
        foreach (var marker in _document.Markers.Where(marker => MarkerPriority(marker.Kind) == 2).OrderBy(marker => marker.Start))
        {
            var x = Viewport.TimeToContentX(marker.Start);
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
        if (_document is null || _document.TextOverlays.Count == 0)
        {
            return;
        }

        foreach (var storedOverlay in _document.TextOverlays.OrderBy(item => item.Start))
        {
            var overlay = _dragTextOverlay?.Id == storedOverlay.Id ? _dragTextOverlay : storedOverlay;
            var rect = GetTextOverlayRectangle(overlay);
            if (!IsTrackVisible(rect.Top, rect.Height)) continue;
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
        if (asset is { Waveform.IsEmpty: false })
        {
            var wholeWaveformArea = new Rect(rectangle.Left + 4, rectangle.Top + 21,
                Math.Max(0, rectangle.Width - 8), Math.Max(0, rectangle.Height - 26));
            var waveformArea = GetVisibleContentRectangle(wholeWaveformArea, 3);
            if (waveformArea.IsEmpty) return;
            var visibleClipStartRatio = rectangle.Width <= 0 ? 0 : Math.Clamp((waveformArea.Left - rectangle.Left) / rectangle.Width, 0, 1);
            var visibleClipEndRatio = rectangle.Width <= 0 ? 1 : Math.Clamp((waveformArea.Right - rectangle.Left) / rectangle.Width, 0, 1);
            var sourceStartRatio = asset.Duration <= 0 ? 0 : Math.Clamp((clip.SourceStart + visibleClipStartRatio * clip.Duration) / asset.Duration, 0, 1);
            var sourceEndRatio = asset.Duration <= 0 ? 1 : Math.Clamp((clip.SourceStart + visibleClipEndRatio * clip.Duration) / asset.Duration, 0, 1);
            _waveformRenderer.Draw(context, asset.Waveform, waveformArea,
                sourceStartRatio, sourceEndRatio, VisualTreeHelper.GetDpi(this).DpiScaleX);
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
        return Viewport.ClipToVisible(source, inset);
    }

    private void DrawPlayhead(DrawingContext context)
    {
        var x = Viewport.TimeToContentX(PlayheadSeconds);
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
        if (_document is null || (_document.InPoint is null && _document.OutPoint is null))
        {
            return;
        }

        var inPoint = Math.Clamp(_document.InPoint ?? 0, 0, _document.TimelineDisplayDuration);
        var outPoint = Math.Clamp(_document.OutPoint ?? _document.TimelineDisplayDuration, inPoint, _document.TimelineDisplayDuration);
        var inX = Viewport.TimeToContentX(inPoint);
        var outX = Viewport.TimeToContentX(outPoint);
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

        if (_document.InPoint.HasValue)
        {
            var pen = CreatePen(Color.FromRgb(52, 211, 153), 2);
            context.DrawLine(pen, new Point(inX, 10), new Point(inX, RenderSize.Height));
            context.DrawText(CreateText("IN", 9, Color.FromRgb(167, 243, 208), dpi, FontWeights.Bold), new Point(inX + 4, 2));
        }
        if (_document.OutPoint.HasValue)
        {
            var pen = CreatePen(Color.FromRgb(251, 191, 36), 2);
            context.DrawLine(pen, new Point(outX, 10), new Point(outX, RenderSize.Height));
            context.DrawText(CreateText("OUT", 9, Color.FromRgb(253, 230, 138), dpi, FontWeights.Bold), new Point(Math.Max(LeftGutterWidth, outX - 28), 2));
        }
    }

    private TimelineClip? HitTestClip(Point point)
        => _document?.Clips
            .OrderByDescending(clip => clip.Track)
            .ThenByDescending(clip => clip.TrackIndex)
            .ThenByDescending(clip => clip.Start)
            .FirstOrDefault(clip => GetClipRectangle(clip).Contains(point));

    private TextOverlay? HitTestTextOverlay(Point point)
        => _document?.TextOverlays
            .OrderByDescending(overlay => overlay.Start)
            .FirstOrDefault(overlay => GetTextOverlayRectangle(overlay).Contains(point));

    private TimelineMarker? HitTestMarker(Point point)
    {
        if (_document is null || point.X < LeftGutterWidth)
        {
            return null;
        }
        var time = Viewport.ContentXToTime(point.X);
        var tolerance = Viewport.PixelsToDuration(10);
        return _document.Markers
            .Where(marker => MarkerPriority(marker.Kind) == 2)
            .OrderBy(marker => Math.Abs(marker.Start - time))
            .FirstOrDefault(marker => Math.Abs(marker.Start - time) <= tolerance);
    }

    private Rect GetClipRectangle(TimelineClip clip)
    {
        var top = Layout.GetTrackTop(clip.Track, clip.TrackIndex);
        var left = Viewport.TimeToContentX(clip.Start);
        var width = Math.Max(10, Viewport.DurationToPixels(clip.Duration));
        return new Rect(left, top + 3, width, TrackHeight - 6);
    }

    private Rect GetTextOverlayRectangle(TextOverlay overlay)
    {
        var left = Viewport.TimeToContentX(overlay.Start);
        return new Rect(left, Layout.TextTrackTop + 3, Math.Max(10, Viewport.DurationToPixels(overlay.Duration)), TrackHeight - 6);
    }

    private TrackAddress? GetTrackAt(double y)
    {
        return Layout.GetTrackAt(y);
    }

    private double GetTrackTop(TrackKind kind, int index)
    {
        return Layout.GetTrackTop(kind, index);
    }

    private double GetTextTrackTop() => Layout.TextTrackTop;

    private bool HasTextTrack => _document?.TextOverlays.Count > 0;

    private int GetTrackCount(TrackKind kind)
        => kind == TrackKind.Visual ? _document?.VisualTrackCount ?? 2 : _document?.AudioTrackCount ?? 2;

    private double GetRequiredHeight()
        => Layout.RequiredHeight;

    private bool IsTrackVisible(double top, double height = TrackHeight)
    {
        return Layout.IntersectsViewport(top, VerticalViewportOffset,
            VerticalViewportHeight <= 0 ? 0 : VerticalViewportHeight + Math.Max(0, height - TrackHeight));
    }

    private TimelineClip? GetPreviousClip(TimelineClip clip)
        => _document?.GetTrackClips(clip.Track, clip.TrackIndex)
            .Where(item => item.Id != clip.Id && item.End <= clip.Start + 0.0001)
            .OrderByDescending(item => item.End)
            .FirstOrDefault();

    private TimelineClip? GetNextClip(TimelineClip clip)
        => _document?.GetTrackClips(clip.Track, clip.TrackIndex)
            .Where(item => item.Id != clip.Id && item.Start >= clip.End - 0.0001)
            .OrderBy(item => item.Start)
            .FirstOrDefault();

    private double SnapTime(double seconds)
    {
        var frameRate = Math.Max(1, _document?.FrameRateValue.FramesPerSecond ?? 30);
        return Math.Round(seconds * frameRate) / frameRate;
    }

    private double SnapDuration(double seconds) => SnapTime(seconds);

    private void SetPlayheadFromPoint(Point point)
    {
        if (point.X < LeftGutterWidth)
        {
            return;
        }
        PlayheadSeconds = Viewport.ContentXToTime(point.X);
        PlayheadChanged?.Invoke(this, new PlayheadChangedEventArgs(PlayheadSeconds));
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

    private TimelineClip ResolveClipDraft(TimelineClip stored)
    {
        if (_dragClip?.Id == stored.Id) return _dragClip;
        return _dragLinkedClips.FirstOrDefault(item => item.Clip.Id == stored.Id).Clip ?? stored;
    }

    private static TimelineEditOperation MapOperation(TimelineDragOperation operation) => operation switch
    {
        TimelineDragOperation.Move => TimelineEditOperation.Move,
        TimelineDragOperation.TrimLeft => TimelineEditOperation.TrimLeft,
        TimelineDragOperation.TrimRight => TimelineEditOperation.TrimRight,
        _ => throw new InvalidOperationException("Timeline gesture ended without an edit operation.")
    };

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

public sealed class TimelineEditRequestedEventArgs(TimelineEditIntent intent) : EventArgs
{
    public TimelineEditIntent Intent { get; } = intent;
}

public sealed class AssetDroppedEventArgs(Guid assetId, double requestedStart, TrackKind requestedTrack, int requestedTrackIndex) : EventArgs
{
    public Guid AssetId { get; } = assetId;
    public double RequestedStart { get; } = requestedStart;
    public TrackKind RequestedTrack { get; } = requestedTrack;
    public int RequestedTrackIndex { get; } = requestedTrackIndex;
}
