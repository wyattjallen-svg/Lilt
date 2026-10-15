namespace Lilt.Core.Layout;

// Pinned: docked, takes layout space, Open toggles visibility.
// AutoHide: overlays the page, Open means revealed. HidePending asks the UI to run the hide delay.
public sealed class SidebarState(DockEdge edge, bool autoHide)
{
    bool pointerInside, focusInside;

    public DockEdge Edge { get; set; } = edge;
    public bool AutoHide { get; private set; } = autoHide;
    public bool Open { get; private set; } = !autoHide;
    public bool HidePending { get; private set; }

    public void SetAutoHide(bool on)
    {
        if (AutoHide == on) return;
        AutoHide = on;
        Close();
        Open = !on;
    }

    public bool PointerAtEdge(bool buttonsPressed)
    {
        if (!AutoHide || Open || buttonsPressed) return false;
        Open = true;
        HidePending = false;
        return true;
    }

    public void PointerEntered()
    {
        pointerInside = true;
        HidePending = false;
    }

    public void PointerExited()
    {
        pointerInside = false;
        RequestHideIfIdle();
    }

    public void FocusEntered()
    {
        focusInside = true;
        HidePending = false;
    }

    public void FocusExited()
    {
        focusInside = false;
        RequestHideIfIdle();
    }

    public void HideDelayElapsed()
    {
        if (!HidePending) return;
        HidePending = false;
        if (!pointerInside && !focusInside) Open = false;
    }

    public void Toggle()
    {
        if (Open) Close();
        else
        {
            Open = true;
            HidePending = false;
        }
    }

    public void Dismiss()
    {
        if (AutoHide && Open) Close();
    }

    void RequestHideIfIdle()
    {
        if (AutoHide && Open && !pointerInside && !focusInside) HidePending = true;
    }

    void Close()
    {
        Open = false;
        HidePending = false;
        pointerInside = false;
        focusInside = false;
    }
}
