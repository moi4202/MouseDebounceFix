using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MouseDebounceFix.UI;

/// <summary>
/// Generates crisp tray icons programmatically without external .ico assets
/// and properly frees native GDI HICON handles to prevent handle leaks.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon CreateStatusIcon(bool isActive)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Mouse body silhouette
        Color bodyColor = isActive ? Color.FromArgb(30, 41, 59) : Color.FromArgb(100, 116, 139);
        Color accentColor = isActive ? Color.FromArgb(16, 185, 129) : Color.FromArgb(245, 158, 11);

        using var bodyBrush = new SolidBrush(bodyColor);
        using var borderPen = new Pen(Color.White, 2f);
        using var mousePath = CreateRoundedRectangle(new Rectangle(6, 2, 20, 28), 9);

        g.FillPath(bodyBrush, mousePath);
        g.DrawPath(borderPen, mousePath);

        // Button divider & scroll wheel accent
        using var dividerPen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
        g.DrawLine(dividerPen, 7, 13, 25, 13);
        g.DrawLine(dividerPen, 16, 3, 16, 13);

        // Status indicator dot (bottom-right)
        using var badgeBg = new SolidBrush(Color.White);
        using var badgeFill = new SolidBrush(accentColor);
        g.FillEllipse(badgeBg, 18, 18, 13, 13);
        g.FillEllipse(badgeFill, 20, 20, 9, 9);

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
