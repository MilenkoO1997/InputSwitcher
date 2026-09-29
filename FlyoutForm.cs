using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace InputSwitcher;

internal sealed class FlyoutForm : Form
{
    const int CornerRadius = 12;

    static readonly Color Bg = Color.FromArgb(32, 32, 32);
    static readonly Color Row = Color.FromArgb(45, 45, 45);
    static readonly Color RowHover = Color.FromArgb(58, 58, 58);
    static readonly Color Accent = Color.FromArgb(0, 120, 212);
    static readonly Color TextPrimary = Color.FromArgb(250, 250, 250);
    static readonly Color TextMuted = Color.FromArgb(180, 180, 180);
    static readonly Color Border = Color.FromArgb(70, 70, 70);

    readonly Panel _root = new();
    readonly Label _status = new();
    bool _busy;

    public event Action<Display, InputSource>? InputSelected;

    public FlyoutForm()
    {
        Text = "Input Switcher";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10f);
        MinimumSize = new Size(300, 80);
        Width = 336;
        Padding = new Padding(0);

        _root.Dock = DockStyle.Fill;
        _root.AutoScroll = true;
        _root.BackColor = Bg;
        _root.Padding = Padding.Empty;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 32;
        _status.ForeColor = TextMuted;
        _status.Font = new Font("Segoe UI", 8.5f);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(20, 0, 20, 10);
        _status.Text = "";

        Controls.Add(_root);
        Controls.Add(_status);

        Resize += (_, _) => ApplyRoundedRegion();
        HandleCreated += (_, _) => ApplyRoundedRegion();
        Paint += OnPaintBorder;
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visible = !string.IsNullOrEmpty(text);
    }

    public void Render(IReadOnlyList<Display> displays, string? status)
    {
        _busy = status is not null &&
                (status.StartsWith("Looking", StringComparison.Ordinal) ||
                 status.StartsWith("Switching", StringComparison.Ordinal));
        SetStatus(status ?? "");

        _root.SuspendLayout();
        _root.Controls.Clear();

        // Panel.Padding does not offset absolutely positioned children — inset manually.
        const int insetX = 20;
        const int insetTop = 16;
        const int insetBottom = 14;
        var left = insetX;
        var y = insetTop;
        var contentWidth = Math.Max(240, ClientSize.Width - insetX * 2);

        var title = new Label
        {
            Text = "Monitor input",
            AutoSize = false,
            Width = contentWidth,
            Height = 28,
            Font = new Font("Segoe UI Semibold", 12f),
            ForeColor = TextPrimary,
            Location = new Point(left, y),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _root.Controls.Add(title);
        y += title.Height + 10;

        if (displays.Count == 0)
        {
            var empty = Wrap(
                status is { Length: > 0 } && !status.StartsWith("Looking", StringComparison.Ordinal)
                    ? status
                    : "No DDC/CI monitor found. Enable DDC/CI in the monitor menu, then Refresh from the tray icon.",
                TextMuted,
                contentWidth);
            empty.Location = new Point(left, y);
            _root.Controls.Add(empty);
            y += empty.Height + 10;
        }
        else
        {
            foreach (var display in displays)
            {
                var header = new Label
                {
                    Text = display.Title,
                    AutoSize = false,
                    Width = contentWidth,
                    Height = 22,
                    Font = new Font("Segoe UI Semibold", 9.5f),
                    ForeColor = TextPrimary,
                    Location = new Point(left, y),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                _root.Controls.Add(header);
                y += header.Height + 2;

                var link = display.LinkSummary;
                if (link.Length > 0)
                {
                    var sub = new Label
                    {
                        Text = link,
                        AutoSize = false,
                        Width = contentWidth,
                        Height = 18,
                        Font = new Font("Segoe UI", 8.5f),
                        ForeColor = TextMuted,
                        Location = new Point(left, y),
                    };
                    _root.Controls.Add(sub);
                    y += sub.Height + 8;
                }
                else
                {
                    y += 6;
                }

                if (display.InputError.Length > 0 && display.Inputs.Count == 0)
                {
                    var warn = Wrap(display.InputError, Color.FromArgb(243, 215, 161), contentWidth);
                    warn.Location = new Point(left, y);
                    _root.Controls.Add(warn);
                    y += warn.Height + 14;
                    continue;
                }

                if (display.Inputs.Count == 0)
                {
                    var none = Wrap("No inputs reported by this monitor.", TextMuted, contentWidth);
                    none.Location = new Point(left, y);
                    _root.Controls.Add(none);
                    y += none.Height + 14;
                    continue;
                }

                foreach (var source in display.Inputs)
                {
                    var row = BuildRow(display, source, contentWidth);
                    row.Location = new Point(left, y);
                    _root.Controls.Add(row);
                    y += row.Height + 6;
                }

                y += 12;
            }
        }

        _root.ResumeLayout();

        var statusH = _status.Visible ? _status.Height : 0;
        Height = Math.Min(560, Math.Max(110, y + insetBottom + statusH));
        Width = 336;
        ApplyRoundedRegion();
        Invalidate();
    }

    Control BuildRow(Display display, InputSource source, int width)
    {
        var active = display.CurrentCode == source.Code;
        var panel = new Panel
        {
            Width = width,
            Height = 42,
            BackColor = active ? Color.FromArgb(52, 52, 52) : Row,
            Cursor = active ? Cursors.Default : Cursors.Hand,
            Tag = (display, source),
        };

        var accent = new Panel
        {
            Width = 3,
            Dock = DockStyle.Left,
            BackColor = active ? Accent : Color.Transparent,
        };

        var label = new Label
        {
            Text = source.Name,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 8, 0),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 10f, active ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Color.Transparent,
            Cursor = panel.Cursor,
        };

        var code = new Label
        {
            Text = source.HexCode,
            AutoSize = false,
            Width = 52,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 12, 0),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f),
            BackColor = Color.Transparent,
            Cursor = panel.Cursor,
        };

        panel.Controls.Add(label);
        panel.Controls.Add(code);
        panel.Controls.Add(accent);

        // Already-active input must not call SetVCP — monitors often blank for seconds.
        if (active)
            return panel;

        void ClickHandler(object? _, EventArgs __)
        {
            if (_busy)
                return;
            InputSelected?.Invoke(display, source);
        }

        void Enter(object? _, EventArgs __) => panel.BackColor = RowHover;
        void Leave(object? _, EventArgs __) => panel.BackColor = Row;

        panel.Click += ClickHandler;
        label.Click += ClickHandler;
        code.Click += ClickHandler;
        accent.Click += ClickHandler;
        panel.MouseEnter += Enter;
        panel.MouseLeave += Leave;
        label.MouseEnter += Enter;
        label.MouseLeave += Leave;
        code.MouseEnter += Enter;
        code.MouseLeave += Leave;
        return panel;
    }

    void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        var old = Region;
        var region = CreateRoundRectRegion(0, 0, Width, Height, CornerRadius);
        Region = region;
        old?.Dispose();
    }

    void OnPaintBorder(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundRectPath(1, 1, Width - 3, Height - 3, CornerRadius - 1);
        using var pen = new Pen(Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    static GraphicsPath CreateRoundRectPath(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Region CreateRoundRectRegion(int x, int y, int width, int height, int radius)
    {
        var hrgn = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, radius * 2, radius * 2);
        var region = Region.FromHrgn(hrgn);
        DeleteObject(hrgn);
        return region;
    }

    static Label Wrap(string text, Color color, int width) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color,
        Font = new Font("Segoe UI", 9f),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _root.Controls.Clear();
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern bool DeleteObject(IntPtr ho);
}
