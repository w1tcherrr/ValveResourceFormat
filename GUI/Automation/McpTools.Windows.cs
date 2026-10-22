#if DEBUG
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Forms;
using GUI.Types.PackageViewer;
using SkiaSharp;
using ValveResourceFormat.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace GUI.Automation;

/// <summary>Tools that drive the viewer's windows and dialogs, and the tree and listing of a package tab.</summary>
internal sealed partial class McpTools
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private const PRINT_WINDOW_FLAGS RenderFullContent = (PRINT_WINDOW_FLAGS)2;

    [Tool("Select a file or folder in a package tab's tree, as a click does, expanding the folders on the way. A folder fills the listing, and a file shows its preview. Returns the selected path, the folder the listing belongs to and its item count.")]
    private static object SelectPackageItem(TabPage tab, [Description("Path in the package, such as materials/dev or materials/dev/a.vmat_c. An empty path selects the root.")] string path)
    {
        var view = PackageView(tab);
        var node = (BetterTreeNode)view.mainTreeView.Nodes[0];

        foreach (var name in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            node.Expand();
            node = node.Nodes.Cast<BetterTreeNode>().FirstOrDefault(child => child.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"'{ItemPath(node)}' has no '{name}'.");
        }

        view.mainTreeView.SelectedNode = node;
        RaiseEvent(view.mainTreeView, "OnAfterSelect", new TreeViewEventArgs(node, TreeViewAction.ByMouse));

        var folder = node.PkgNode ?? ((BetterTreeNode)node.Parent!).PkgNode!;

        return new
        {
            Selected = ItemPath(node),
            Folder = PackageViewer.GetFolderPath(folder),
            ListingCount = PackageList(view).VirtualListSize,
        };
    }

    [Tool("Read the listing of a package tab: a folder's contents, or the results of a search. Items are in the order shown, with path, size in bytes and type, and folders marked. 'busy' means a dialog, such as the progress of a contents search, is still open.")]
    private static async Task<object> GetPackageListing(
        TabPage tab,
        [Description("Items to skip, from an earlier 'next_offset'.")][Range(0, int.MaxValue)] int offset = 0,
        [Description("Maximum number of items to return.")][Range(1, int.MaxValue)] int limit = 100,
        [Description("First wait up to a minute for open dialogs to close, so a search started from the search dialog has finished.")] bool wait = true,
        CancellationToken cancellationToken = default)
    {
        var waited = Stopwatch.StartNew();

        while (wait && waited.Elapsed < UiTimeout && await OnUi(HasDialog, cancellationToken).ConfigureAwait(false))
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return await OnUi(() =>
        {
            var items = PackageList(PackageView(tab)).VirtualItems ?? [];

            return new
            {
                Total = items.Count,
                Items = items.Skip(offset).Take(limit).Cast<BetterListViewItem>().Select(DescribeListItem).ToList(),
                NextOffset = offset + limit < items.Count ? offset + limit : (int?)null,
                Busy = Flag(HasDialog()),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    [Tool("Open the search dialog of a package tab, as Ctrl+F does, and wait for it. Returns the dialog's controls. Change them with set_window_controls or send_keys, then press Find, and read the results with get_package_listing.")]
    private static async Task<object> OpenSearch(
        TabPage tab,
        [Description("Open it with Search in folder from the context menu of the folder selected in the tree, as select_package_item selects it.")] bool folderMenu = false,
        CancellationToken cancellationToken = default)
    {
        var crash = ReportedErrors.NextAsync();

        await OnUi(() =>
        {
            var view = PackageView(tab);

            if (HasDialog())
            {
                throw new ToolException("A dialog is already open. Close it first, such as with send_keys Escape.");
            }

            Program.MainForm.Tabs.SelectTab(tab);

            var item = folderMenu ? FolderMenuItem(view.mainTreeView) : MenuItem(Keys.Control | Keys.F);

            // The dialog runs its own message loop, so it opens after this call has returned
            Program.MainForm.BeginInvoke(item.PerformClick);
        }, cancellationToken).ConfigureAwait(false);

        var waited = Stopwatch.StartNew();

        while (true)
        {
            var dialog = await OnUi(() => Application.OpenForms.OfType<SearchForm>().FirstOrDefault(static form => form.Visible) is { } form ? DescribeWindow(form, items: true) : null, cancellationToken).ConfigureAwait(false);

            if (dialog != null)
            {
                return dialog;
            }

            if (crash.IsCompleted)
            {
                throw new ToolException($"Error while opening the search dialog: {await crash.ConfigureAwait(false)}");
            }

            if (waited.Elapsed > UiTimeout)
            {
                throw new ToolException("The search dialog did not open.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

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

    [Tool("Resize an open window as dragging its frame does, so a window with a minimum or maximum size keeps within it. Returns the size it got.")]
    private static object SetWindowSize(
        [Description("Title or control name of the window. Defaults to the topmost dialog, or the main window when none is open.")] string? window = null,
        [Description("Width in pixels, frame included. Omit to keep the width.")][Range(1, int.MaxValue)] int? width = null,
        [Description("Height in pixels, frame included. Omit to keep the height.")][Range(1, int.MaxValue)] int? height = null)
    {
        var form = FindWindow(window);
        form.Size = new Size(width ?? form.Width, height ?? form.Height);

        return new { form.Width, form.Height };
    }

    [Tool("Read the message box that is open, such as a warning after a search, with its title, text and buttons. 'button' presses one of them, which closes it. Answers while the call that opened the message box still waits for it.", Concurrent = true)]
    private static Task<object> MessageBoxWindow(
        [Description("Text of the button to press, such as OK or Yes. Omit to only read the message box.")] string? button = null,
        CancellationToken cancellationToken = default) => OnUi<object>(() =>
    {
        const int MessageTextId = 0xFFFF;
        const uint BM_CLICK = 0x00F5;

        HWND dialog = default;

        PInvoke.EnumThreadWindows(PInvoke.GetCurrentThreadId(), (hwnd, _) =>
        {
            if (PInvoke.IsWindowVisible(hwnd) && ClassNameOf(hwnd) == "#32770")
            {
                dialog = hwnd;
                return false;
            }

            return true;
        }, default);

        if (dialog == default)
        {
            throw new ToolException("No message box is open.");
        }

        var buttons = new List<(HWND Handle, string Text)>();

        PInvoke.EnumChildWindows(dialog, (child, _) =>
        {
            if (ClassNameOf(child) == "Button")
            {
                buttons.Add((child, StripMnemonics(TextOf(child))));
            }

            return true;
        }, default);

        if (button != null)
        {
            var target = buttons.FirstOrDefault(candidate => candidate.Text.Equals(StripMnemonics(button), StringComparison.OrdinalIgnoreCase));

            if (target.Handle == default)
            {
                throw new ToolException($"The message box has no button '{button}'. Its buttons are: {string.Join(", ", buttons.Select(static candidate => candidate.Text))}.");
            }

            PInvoke.PostMessage(target.Handle, BM_CLICK, default, default);
        }

        return new
        {
            Title = TextOf(dialog),
            Text = TextOf(PInvoke.GetDlgItem(dialog, MessageTextId)),
            Buttons = buttons.Select(static candidate => candidate.Text).ToList(),
            Pressed = button,
        };
    }, cancellationToken);

    private static string ClassNameOf(HWND hwnd)
    {
        Span<char> name = stackalloc char[64];
        var length = PInvoke.GetClassName(hwnd, name);

        return name[..length].ToString();
    }

    private static string TextOf(HWND hwnd)
    {
        var buffer = new char[PInvoke.GetWindowTextLength(hwnd) + 1];
        var length = PInvoke.GetWindowText(hwnd, buffer);

        return new string(buffer, 0, length);
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

    private static TreeViewWithSearchResults PackageView(TabPage tab)
        => tab.Controls[nameof(TreeViewWithSearchResults)] as TreeViewWithSearchResults ?? throw new ToolException($"Tab '{tab.Text}' is not a package.");

    private static BetterListView PackageList(TreeViewWithSearchResults view) => (BetterListView)view.Controls.Find("mainListView", searchAllChildren: true)[0];

    private static string ItemPath(BetterTreeNode node) => node.PackageEntry?.GetFullPath() ?? PackageViewer.GetFolderPath(node.PkgNode!);

    private static object DescribeListItem(BetterListViewItem item)
    {
        if (item.PackageEntry is { } entry)
        {
            return new { Path = entry.GetFullPath(), Size = entry.TotalLength, Type = entry.TypeName };
        }

        var folder = item.PkgNode!;

        return new { Path = PackageViewer.GetFolderPath(folder), Size = folder.TotalSize, Folder = true, Up = Flag(item.Tag is BetterListViewItem.ParentNavigationTag) };
    }

    /// <summary>Raises a control's event through its protected handler, as the control does for user input.</summary>
    private static void RaiseEvent(Control control, string handler, EventArgs e)
        => control.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic, [e.GetType()])!.Invoke(control, BindingFlags.DoNotWrapExceptions, null, [e], null);

    /// <summary>The item of the main menu with this shortcut.</summary>
    private static ToolStripMenuItem MenuItem(Keys shortcut)
        => Program.MainForm.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().First(item => item.ShortcutKeys == shortcut);

    /// <summary>Opens the context menu of the folder selected in a package tree, as a right click does, and returns its Search in folder item.</summary>
    private static ToolStripItem FolderMenuItem(BetterTreeView tree)
    {
        if (tree.SelectedNode is not BetterTreeNode { IsFolder: true, Parent: not null } folder)
        {
            throw new ToolException("Select a folder other than the root with select_package_item first.");
        }

        RaiseEvent(tree, "OnNodeMouseClick", new TreeNodeMouseClickEventArgs(folder, MouseButtons.Right, 1, folder.Bounds.X, folder.Bounds.Y));

        ContextMenuStrip? menu = null;

        PInvoke.EnumThreadWindows(PInvoke.GetCurrentThreadId(), (hwnd, _) =>
        {
            if (Control.FromHandle(hwnd) is ContextMenuStrip { Visible: true } shown)
            {
                menu = shown;
            }

            return true;
        }, default);

        return menu?.Items.Cast<ToolStripItem>().FirstOrDefault(static item => item is { Text: "Search in folder", Available: true })
            ?? throw new ToolException("The folder's context menu has no Search in folder.");
    }

    /// <summary>Whether a modal dialog other than the main window is open.</summary>
    private static bool HasDialog() => Application.OpenForms.Cast<Form>().Any(static form => form != Program.MainForm && form.Visible && form.Modal);

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
