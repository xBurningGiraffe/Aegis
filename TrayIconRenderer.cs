using System.Drawing.Drawing2D;

namespace Aegis;

public enum TrayIconState { Idle, Running, Error }

/// <summary>
/// Generates tray icons on-the-fly using GDI+ — no embedded .ico file needed.
/// Renders a classic heraldic shield shape whose colour reflects the backup state:
///   • Deep blue  — idle / healthy
///   • Gold       — backup running (animated dots)
///   • Crimson    — error
/// </summary>
public static class TrayIconRenderer
{
    private static readonly string[] AnimFrames = { "·", "··", "···" };
    private static int _frame;

    // Pre-rendered icons for stable states
    private static Icon? _idleIcon;
    private static Icon? _errorIcon;

    public static Icon Render(TrayIconState state)
    {
        return state switch
        {
            TrayIconState.Idle    => _idleIcon  ??= Build(TrayIconState.Idle,  "A"),
            TrayIconState.Error   => _errorIcon ??= Build(TrayIconState.Error, "!"),
            TrayIconState.Running => Build(TrayIconState.Running, AnimFrames[_frame % 3]),
            _                    => _idleIcon  ??= Build(TrayIconState.Idle,  "A"),
        };
    }

    /// <summary>Advances the animation frame for the "Running" state.</summary>
    public static void NextFrame() => _frame++;

    /// <summary>Invalidates cached icons (call after DPI change).</summary>
    public static void InvalidateCache()
    {
        _idleIcon?.Dispose();  _idleIcon  = null;
        _errorIcon?.Dispose(); _errorIcon = null;
    }

    private static Icon Build(TrayIconState state, string label)
    {
        const int size = 16;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);

        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // ── Shield fill colour ─────────────────────────────────────────────────
        Color bg = state switch
        {
            TrayIconState.Running => Color.FromArgb(210, 150,   0),   // Athena's gold
            TrayIconState.Error   => Color.FromArgb(190,  30,  40),   // crimson
            _                     => Color.FromArgb( 25,  75, 170),   // Aegis blue
        };

        // ── Classic heraldic shield path ───────────────────────────────────────
        // Flat top, angled shoulders, pointed base — readable at 16 × 16 px.
        var shieldPts = new PointF[]
        {
            new( 1f,  0f),   // top-left
            new(14f,  0f),   // top-right
            new(14f,  9f),   // right shoulder
            new(7.5f,15f),   // bottom point
            new( 1f,  9f),   // left shoulder
        };

        using var path = new GraphicsPath();
        path.AddClosedCurve(shieldPts, 0.2f);   // slight curve softens the corners

        // Fill shield
        using var bgBrush = new SolidBrush(bg);
        g.FillPath(bgBrush, path);

        // Thin white border
        using var borderPen = new Pen(Color.FromArgb(180, Color.White), 0.8f);
        g.DrawPath(borderPen, path);

        // ── Label text ─────────────────────────────────────────────────────────
        bool isRunningAnim = state == TrayIconState.Running && label.Length > 1;
        float fontSize = isRunningAnim ? 4.5f : 7.5f;

        using var font      = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        // Nudge text up slightly so it sits in the wider upper half of the shield
        g.DrawString(label, font, textBrush, new RectangleF(0f, -1f, size, size), sf);

        // ── Convert Bitmap → Icon ──────────────────────────────────────────────
        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
