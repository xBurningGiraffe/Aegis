using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Aegis.Services;

/// <summary>
/// Detects the Windows "Apps" colour theme (light/dark) from the registry
/// and applies matching colours to a WinForms control tree.
/// </summary>
public enum AppTheme { Light, Dark }

public static class ThemeManager
{
    // ── Current theme ─────────────────────────────────────────────────────────
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    // ── Colour palette ────────────────────────────────────────────────────────
    public static Color Background   => Current == AppTheme.Dark ? Color.FromArgb(32,  32,  32 ) : SystemColors.Control;
    public static Color Surface      => Current == AppTheme.Dark ? Color.FromArgb(48,  48,  48 ) : SystemColors.Window;
    public static Color ButtonFace   => Current == AppTheme.Dark ? Color.FromArgb(62,  62,  62 ) : SystemColors.Control;
    public static Color ButtonBorder => Current == AppTheme.Dark ? Color.FromArgb(100, 100, 100) : SystemColors.ControlDark;
    public static Color Foreground   => Current == AppTheme.Dark ? Color.FromArgb(220, 220, 220) : SystemColors.WindowText;
    public static Color ToolStripBg  => Current == AppTheme.Dark ? Color.FromArgb(40,  40,  40 ) : SystemColors.Control;

    // ── Detection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the registry and updates <see cref="Current"/>.
    /// Returns <c>true</c> when the theme changed.
    /// </summary>
    public static bool Detect()
    {
        var detected = IsSystemDark() ? AppTheme.Dark : AppTheme.Light;
        if (detected == Current) return false;
        Current = detected;
        return true;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ── Managed-layer application ──────────────────────────────────────────────

    /// <summary>
    /// Recursively applies the current theme colours to <paramref name="root"/> and all
    /// descendants, then invalidates the tree to force a repaint.
    /// Safe to call before or after the form handle is created.
    /// </summary>
    public static void Apply(Control root)
    {
        ApplyTo(root);
        root.Invalidate(true);
    }

    private static void ApplyTo(Control ctrl)
    {
        switch (ctrl)
        {
            // ToolStrip items are not in ctrl.Controls — handle them here and return.
            case ToolStrip ts:
                ApplyToolStrip(ts);
                return;

            // SplitContainer's Panel1/Panel2 may not be in Controls — recurse explicitly.
            case SplitContainer sc:
                sc.BackColor = Background;
                sc.ForeColor = Foreground;
                ApplyTo(sc.Panel1);
                ApplyTo(sc.Panel2);
                return;

            // Input controls — slightly lighter "surface" colour.
            case TextBox or RichTextBox or ListBox or ListView
                         or NumericUpDown or ComboBox or DateTimePicker:
                ctrl.BackColor = Surface;
                ctrl.ForeColor = Foreground;
                break;

            // Buttons: UseVisualStyleBackColor must be false in dark mode or the OS
            // visual-style renderer ignores our BackColor (visible inside GroupBoxes).
            case Button btn:
                btn.UseVisualStyleBackColor = Current != AppTheme.Dark;
                btn.BackColor = ButtonFace;
                btn.ForeColor = Foreground;
                btn.FlatStyle = Current == AppTheme.Dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (Current == AppTheme.Dark)
                    btn.FlatAppearance.BorderColor = ButtonBorder;
                break;

            // Transparent so the parent background shows through rather than a grey patch.
            case CheckBox or Label:
                ctrl.BackColor = Color.Transparent;
                ctrl.ForeColor = Foreground;
                break;

            // Catch-all: Form, Panel, GroupBox, TableLayoutPanel, FlowLayoutPanel, …
            default:
                ctrl.BackColor = Background;
                ctrl.ForeColor = Foreground;
                break;
        }

        foreach (Control child in ctrl.Controls)
            ApplyTo(child);
    }

    private static void ApplyToolStrip(ToolStrip ts)
    {
        if (Current == AppTheme.Dark)
        {
            ts.BackColor = ToolStripBg;
            ts.ForeColor = Foreground;
            ts.Renderer  = new DarkToolStripRenderer();
        }
        else
        {
            ts.BackColor = SystemColors.Control;
            ts.ForeColor = SystemColors.ControlText;
            ts.Renderer  = new ToolStripProfessionalRenderer();
        }

        foreach (ToolStripItem item in ts.Items)
        {
            item.BackColor = ts.BackColor;
            item.ForeColor = ts.ForeColor;
        }
    }

    // ── Native (uxtheme) application ──────────────────────────────────────────
    // SetWindowTheme tells Windows to render a control using a named visual-style
    // sub-app.  The "DarkMode_*" names are built into Windows 10/11 and make
    // OS-drawn control parts (ComboBox arrow, ListView rows, spin buttons) dark.
    // Requires the control handle to already exist — call from Shown or later.

    [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string? pszSubIdList);

    /// <summary>
    /// Recursively calls <c>SetWindowTheme</c> on controls whose OS-drawn parts
    /// (dropdown arrows, spin buttons, row highlights) need a dark-mode theme name.
    /// Only has effect in dark mode; does nothing in light mode.
    /// Must be called after the form handle is created (e.g. from the Shown event).
    /// </summary>
    public static void ApplyNativeThemes(Control root)
    {
        if (Current != AppTheme.Dark) return;
        ApplyNativeThemesTo(root);
    }

    private static void ApplyNativeThemesTo(Control ctrl)
    {
        if (ctrl.IsHandleCreated)
        {
            switch (ctrl)
            {
                case ListView:
                    // "DarkMode_Explorer" gives dark row-highlight and scrollbars.
                    SetWindowTheme(ctrl.Handle, "DarkMode_Explorer", null);
                    break;

                case ComboBox or ListBox:
                    // "DarkMode_CFD" (Common-File-Dialog) themes the dropdown button
                    // and list background for ComboBox and ListBox.
                    SetWindowTheme(ctrl.Handle, "DarkMode_CFD", null);
                    break;

                case NumericUpDown:
                    // The outer UPDOWN window — themes the spin arrows.
                    SetWindowTheme(ctrl.Handle, "DarkMode_Explorer", null);
                    break;
            }
        }

        // SplitContainer's panels are not always in Controls — visit explicitly.
        if (ctrl is SplitContainer sc)
        {
            ApplyNativeThemesTo(sc.Panel1);
            ApplyNativeThemesTo(sc.Panel2);
            return;
        }

        foreach (Control child in ctrl.Controls)
            ApplyNativeThemesTo(child);
    }
}

// ── Dark-mode ToolStrip renderer ──────────────────────────────────────────────

internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    internal DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        => e.Graphics.Clear(Color.FromArgb(40, 40, 40));

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected || e.Item.Pressed)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            r.Inflate(-2, -2);
            using var brush = new SolidBrush(Color.FromArgb(72, 72, 72));
            e.Graphics.FillRectangle(brush, r);
        }
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin       => Color.FromArgb(40, 40, 40);
    public override Color ToolStripGradientMiddle      => Color.FromArgb(40, 40, 40);
    public override Color ToolStripGradientEnd         => Color.FromArgb(40, 40, 40);
    public override Color ToolStripBorder              => Color.FromArgb(60, 60, 60);
    public override Color ButtonSelectedHighlight      => Color.FromArgb(72, 72, 72);
    public override Color ButtonSelectedGradientBegin  => Color.FromArgb(72, 72, 72);
    public override Color ButtonSelectedGradientMiddle => Color.FromArgb(72, 72, 72);
    public override Color ButtonSelectedGradientEnd    => Color.FromArgb(72, 72, 72);
    public override Color ButtonPressedGradientBegin   => Color.FromArgb(90, 90, 90);
    public override Color ButtonPressedGradientMiddle  => Color.FromArgb(90, 90, 90);
    public override Color ButtonPressedGradientEnd     => Color.FromArgb(90, 90, 90);
}
