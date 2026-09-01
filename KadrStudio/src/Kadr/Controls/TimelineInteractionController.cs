namespace KadrStudio.Controls;

public enum TimelineDragOperation
{
    None,
    Move,
    TrimLeft,
    TrimRight
}

public enum TimelineToolMode
{
    Selection,
    Razor
}

/// <summary>Transient pointer state, independent from project and renderer state.</summary>
public sealed class TimelineInteractionController
{
    public TimelineToolMode ToolMode { get; set; } = TimelineToolMode.Selection;
    public TimelineDragOperation DragOperation { get; private set; }
    public double PointerOffsetSeconds { get; private set; }
    public bool IsDraggingPlayhead { get; private set; }

    public void BeginPlayheadDrag() => IsDraggingPlayhead = true;
    public void EndPlayheadDrag() => IsDraggingPlayhead = false;

    public void BeginDrag(TimelineDragOperation operation, double pointerOffsetSeconds)
    {
        DragOperation = operation;
        PointerOffsetSeconds = Math.Max(0, pointerOffsetSeconds);
    }

    public void EndDrag()
    {
        DragOperation = TimelineDragOperation.None;
        PointerOffsetSeconds = 0;
    }
}
