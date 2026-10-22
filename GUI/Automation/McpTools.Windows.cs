#if DEBUG
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SkiaSharp;
using ValveResourceFormat.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace GUI.Automation;

/// <summary>Tools that capture the viewer's windows.</summary>
internal sealed partial class McpTools
{
    private const PRINT_WINDOW_FLAGS RenderFullContent = (PRINT_WINDOW_FLAGS)2;

    [Tool("List the controls of an open window, such as a dialog, in tab order. Each has its kind, label, control name, value, text, tab index and state. Kinds are textbox, checkbox, radio, toggle, dropdown, list, button and text, and the sidebar kinds of list_controls. 'focused' marks the control that gets the keys.")]
    private static object ListWindowControls(
        [Description("Title or control name of the window, such as Find. Defaults to the topmost dialog, or the main window when none is open.")] string? window = null,
        [Description("Include the items of dropdowns and lists.")] bool items = false)
        => DescribeWindow(FindWindow(window), items);

    [Tool("Change the controls of an open window in order, as a user would. Stops at the first change that fails and keeps the changes before it. A button that closes the dialog, such as Find, closes it. A button that opens another dialog holds this call until that dialog closes, so press it with send_keys Space instead. Returns each changed control once.")]
    private static List<object> SetWindowControls(
        [Description("Title or control name of the window, as list_window_controls gives it.")] string window,
        [Description("'name' is the label or control name from list_window_controls. Give 'group' only when two controls have the same label. 'value' is the text for a textbox, true or false for a checkbox or toggle, and the item text or index for a dropdown. Omit 'value' to press a button, select a radio option or flip a toggle.")] List<ControlChange> changes)
        => ApplyControls(FindWindow(window), changes, details: true);

    [Tool("Type into an open window as the keyboard does. The keys go to its focused control through the window's own key handling. So Tab and Shift+Tab move the focus, Enter presses the default button and Escape cancels. Space presses the focused button or checkbox, and arrows move within a group of options. 'text' is typed before the keys. Returns the focused control afterwards.")]
    private static async Task<object> SendKeys(
        [Description("Title or control name of the window. Defaults to the topmost dialog, or the main window when none is open.")] string? window = null,
        [Description("Keys to press in order, such as Tab, Shift+Tab, Enter, Escape, Space, Down or Ctrl+A.")] List<string>? keys = null,
        [Description("Text to type into the focused control first.")] string? text = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = (keys ?? []).Select(ParseKey).ToList();

        if (text != null)
        {
            await OnUi(() =>
            {
                var target = (HWND)FocusedControl(FindWindow(window))!.Handle;

                foreach (var character in text)
                {
                    PInvoke.PostMessage(target, PInvoke.WM_CHAR, character, 1);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in parsed)
        {
            var state = await OnUi(() => PressKey(FindWindow(window), key), cancellationToken).ConfigureAwait(false);

            // Runs after the posted keys, which hold the modifiers until then
            await OnUi(() => PInvoke.SetKeyboardState(state), cancellationToken).ConfigureAwait(false);
        }

        return await OnUi<object>(() =>
        {
            if (TryFindWindow(window) is not { } form)
            {
                return new { Closed = true };
            }

            var focused = FocusedControl(form)!;

            return new { Window = form.Text, Focused = new { Kind = KindOf(focused), focused.Name, focused.Text } };
        }, cancellationToken).ConfigureAwait(false);
    }
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

    private static object DescribeWindow(Form form, bool items) => new
    {
        Window = form.Text,
        Type = form.GetType().Name,
        Controls = SidebarControls(form).Select(control => DescribeControl(control, items, details: true)).ToList(),
    };

    private static Keys ParseKey(string key)
    {
        try
        {
            return (Keys)new KeysConverter().ConvertFromInvariantString(key)!;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidEnumArgumentException)
        {
            throw new ArgumentException($"'{key}' is not a key. Name keys as Tab, Shift+Tab, Enter, Escape, Space, Down or Ctrl+A.");
        }
    }

    /// <summary>
    /// Posts a key press to a window's focused control, holding its modifiers in the thread's keyboard
    /// state, which modifier checks read. Returns the keyboard state to restore once the keys are handled.
    /// </summary>
    private static byte[] PressKey(Form form, Keys key)
    {
        var target = (HWND)FocusedControl(form)!.Handle;
        var state = new byte[256];
        PInvoke.GetKeyboardState(state);
        var saved = state.ToArray();

        state[(int)Keys.ShiftKey] = (key & Keys.Shift) != 0 ? (byte)0x80 : (byte)0;
        state[(int)Keys.ControlKey] = (key & Keys.Control) != 0 ? (byte)0x80 : (byte)0;
        state[(int)Keys.Menu] = (key & Keys.Alt) != 0 ? (byte)0x80 : (byte)0;
        PInvoke.SetKeyboardState(state);

        var code = (uint)(key & Keys.KeyCode);
        var alt = (key & Keys.Alt) != 0;
        var down = 1u | (PInvoke.MapVirtualKey(code, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC) << 16) | (alt ? 1u << 29 : 0u);
        var up = down | (3u << 30);

        PInvoke.PostMessage(target, alt ? PInvoke.WM_SYSKEYDOWN : PInvoke.WM_KEYDOWN, code, (nint)down);
        PInvoke.PostMessage(target, alt ? PInvoke.WM_SYSKEYUP : PInvoke.WM_KEYUP, code, (nint)up);

        return saved;
    }
}
#endif
