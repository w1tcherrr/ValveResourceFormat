#if DEBUG
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SkiaSharp;
using ValveResourceFormat.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;

namespace GUI.Automation;

/// <summary>Tools that capture the viewer's windows.</summary>
internal sealed partial class McpTools
{
    private const PRINT_WINDOW_FLAGS RenderFullContent = (PRINT_WINDOW_FLAGS)2;

    [Tool("Capture an open window as the screen shows it, frame included, even while other windows cover it. Returns a JPEG. 'path' also writes a PNG.")]
    private static ImageReply ScreenshotWindow(
        [Description("Title or control name of the window, such as Find. Defaults to the topmost dialog, or the main window when none is open.")] string? window = null,
        [Description("Absolute path of a .png file for the capture.")] string? path = null)
    {
        RequireAbsolute(path, nameof(path));

        var form = FindWindow(window);

        if (form.WindowState == FormWindowState.Minimized)
        {
            throw new ToolException($"'{form.Text}' is minimized, so there is nothing to capture. Restore it.");
        }

        form.Refresh();

        using var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppRgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();

            try
            {
                if (!PInvoke.PrintWindow((HWND)form.Handle, (HDC)hdc, RenderFullContent))
                {
                    throw new ToolException($"Could not capture '{form.Text}'.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var pixels = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

        try
        {
            using var captured = new SKBitmap();
            captured.InstallPixels(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), pixels.Scan0, pixels.Stride);

            if (path != null)
            {
                File.WriteAllBytes(path, TextureExtract.ToPngImage(captured));
            }

            using var jpeg = captured.Encode(SKEncodedImageFormat.Jpeg, 85);

            return new ImageReply(new { form.Text, bitmap.Width, bitmap.Height, Path = path }, jpeg.ToArray());
        }
        finally
        {
            bitmap.UnlockBits(pixels);
        }
    }

    /// <summary>The visible window with this title or control name, or the topmost dialog, or the main window when none is open.</summary>
    private static Form? TryFindWindow(string? window)
    {
        var forms = Application.OpenForms.Cast<Form>().Where(static form => form.Visible);

        if (window == null)
        {
            return forms.LastOrDefault(static form => form.Modal) ?? Program.MainForm;
        }

        return forms.LastOrDefault(form => form.Text.Equals(window, StringComparison.OrdinalIgnoreCase) || form.Name.Equals(window, StringComparison.OrdinalIgnoreCase));
    }

    private static Form FindWindow(string? window)
        => TryFindWindow(window) ?? throw new ToolException($"No open window '{window}'. The open windows are: {string.Join(", ", Application.OpenForms.Cast<Form>().Where(static form => form.Visible).Select(static form => $"'{form.Text}'"))}.");
}
#endif
