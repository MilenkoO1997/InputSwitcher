namespace InputSwitcher;

internal sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly FlyoutForm _flyout;
    readonly object _gate = new();
    List<Display> _displays = new();
    int _generation;
    bool _refreshing;
    bool _switching;
    bool _opening;
    Point? _anchor;

    public TrayApp()
    {
        _flyout = new FlyoutForm();
        _flyout.InputSelected += OnInputSelected;
        _flyout.Deactivate += (_, _) =>
        {
            if (!_opening)
            {
                _flyout.Hide();
                _anchor = null;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, (_, _) => RefreshDisplays(keepFlyoutOpen: _flyout.Visible));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        Icon? icon = null;
        try
        {
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Fall back to the system application icon below.
        }

        _tray = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Input Switcher",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseUp += OnTrayMouseUp;

        _ = _flyout.Handle;
        RefreshDisplays(keepFlyoutOpen: false);
    }

    void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        if (_flyout.Visible)
        {
            _flyout.Hide();
            _anchor = null;
            return;
        }

        // Anchor once to the click point so later refresh/resize does not chase the cursor.
        _anchor = Control.MousePosition;
        OpenFlyout(forceRefresh: true);
    }

    void OpenFlyout(bool forceRefresh)
    {
        List<Display> snapshot;
        lock (_gate)
            snapshot = _displays;

        var status = _refreshing ? "Looking for displays…" : null;
        _flyout.Render(snapshot, status);
        PlaceFlyout();
        PresentFlyout();

        if (forceRefresh || snapshot.Count == 0)
            RefreshDisplays(keepFlyoutOpen: true);
    }

    void PresentFlyout()
    {
        _opening = true;
        try
        {
            if (!_flyout.Visible)
                _flyout.Show();
            _flyout.Activate();
        }
        finally
        {
            BeginInvokeUi(() => _opening = false);
        }
    }

    void PlaceFlyout()
    {
        _flyout.PerformLayout();
        var size = _flyout.Size;
        var anchor = _anchor ?? Control.MousePosition;
        var area = Screen.FromPoint(anchor).WorkingArea;
        const int gap = 12;
        const int margin = 10;

        // Prefer above the tray click, bottom edge fixed so height changes grow upward.
        var x = anchor.X - size.Width / 2;
        var y = anchor.Y - size.Height - gap;

        if (x + size.Width > area.Right - margin)
            x = area.Right - size.Width - margin;
        if (x < area.Left + margin)
            x = area.Left + margin;

        if (y < area.Top + margin)
            y = Math.Min(anchor.Y + gap, area.Bottom - size.Height - margin);
        if (y + size.Height > area.Bottom - margin)
            y = area.Bottom - size.Height - margin;
        if (y < area.Top + margin)
            y = area.Top + margin;

        _flyout.Location = new Point(x, y);
    }

    void RefreshDisplays(bool keepFlyoutOpen)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        var generation = ++_generation;
        List<Display> previous;
        lock (_gate)
            previous = _displays;

        if (_flyout.Visible)
            _flyout.SetStatus("Looking for displays…");

        Task.Run(() =>
        {
            List<Display> displays;
            string error;
            try
            {
                displays = Native.LoadDisplays();
                error = "";
            }
            catch (Exception ex)
            {
                displays = new();
                error = ex.Message;
            }

            BeginInvokeUi(() =>
            {
                Native.Release(previous);
                if (generation != _generation)
                {
                    Native.Release(displays);
                    return;
                }

                _refreshing = false;
                lock (_gate)
                    _displays = displays;

                if (error.Length > 0)
                {
                    _tray.Text = "Input Switcher — error";
                    if (_flyout.Visible || keepFlyoutOpen)
                    {
                        var bottom = _flyout.Visible ? _flyout.Bottom : (int?)null;
                        _flyout.Render(displays, error);
                        ReanchorAfterResize(bottom);
                        PresentFlyout();
                    }
                    return;
                }

                _tray.Text = displays.Count == 0
                    ? "Input Switcher — no DDC monitor"
                    : $"Input Switcher — {displays.Count} display(s)";

                if (_flyout.Visible || keepFlyoutOpen)
                {
                    var bottom = _flyout.Visible ? _flyout.Bottom : (int?)null;
                    _flyout.Render(displays, null);
                    ReanchorAfterResize(bottom);
                    if (!_flyout.Visible)
                        PresentFlyout();
                }
            });
        });
    }

    /// <summary>
    /// Keep the bottom edge fixed when content height changes (tray flyout grows upward).
    /// </summary>
    void ReanchorAfterResize(int? previousBottom)
    {
        if (previousBottom is int bottom && _flyout.Visible)
        {
            var area = Screen.FromPoint(_flyout.Location).WorkingArea;
            var x = _flyout.Left;
            var y = bottom - _flyout.Height;
            if (y < area.Top + 10)
                y = area.Top + 10;
            _flyout.Location = new Point(x, y);
            return;
        }

        PlaceFlyout();
    }

    void OnInputSelected(Display display, InputSource source)
    {
        if (_switching || _refreshing)
            return;
        if (display.CurrentCode == source.Code)
            return;

        _switching = true;
        _flyout.SetStatus($"Switching to {source.Name}…");
        var bottom = _flyout.Bottom;
        Task.Run(() =>
        {
            string message;
            int? confirmed = null;
            try
            {
                confirmed = Native.SetInput(display, source.Code);
                message = confirmed is int actual && actual != source.Code
                    ? $"Sent {source.Name}, monitor reports {InputCatalog.Label(actual)}."
                    : $"Switched to {source.Name}.";
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }

            BeginInvokeUi(() =>
            {
                _switching = false;
                if (confirmed is int code)
                {
                    if (display.Inputs.All(item => item.Code != code))
                        display.Inputs.Add(new InputSource(code, InputCatalog.Label(code)));
                    display.CurrentCode = code;
                    display.Inputs = InputCatalog.Sort(display.Inputs);
                }
                else if (message.StartsWith("Switched ", StringComparison.Ordinal))
                {
                    display.CurrentCode = source.Code;
                }

                List<Display> snapshot;
                lock (_gate)
                    snapshot = _displays;
                _flyout.Render(snapshot, message);
                ReanchorAfterResize(bottom);
            });
        });
    }

    void BeginInvokeUi(Action action)
    {
        if (_flyout.IsHandleCreated)
            _flyout.BeginInvoke(action);
        else
            action();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _flyout.Hide();
        _flyout.Dispose();
        List<Display> open;
        lock (_gate)
            open = _displays;
        Native.Release(open);
        base.ExitThreadCore();
    }
}
