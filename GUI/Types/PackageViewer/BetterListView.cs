using System.Drawing;
using System.Windows.Forms;
using GUI.Utils;
using Windows.Win32;

namespace GUI.Types.PackageViewer
{
    /// <inheritdoc/>
    internal class BetterListView : ListView
    {
        public Color BorderColor { get; set; } = Color.White;
        public Color Highlight { get; set; } = Color.White;
        public VrfGuiContext? VrfGuiContext { get; set; }
        internal List<ListViewItem>? VirtualItems { get; set; }

        /// <summary>Gets or sets a column that shares the flexible width with the first column, or -1 for none.</summary>
        internal int SharedFlexibleColumn { get; set; } = -1;

        /// <summary>Gets or sets the text drawn in place of the items while the list is empty.</summary>
        internal string? EmptyText
        {
            get;
            set
            {
                field = value;
                Invalidate();
            }
        }
        private bool isAdjustingColumns;
        public event ScrollEventHandler? Scroll;

        public BetterListView() : base()
        {
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
            HotTracking = false;
        }

        protected virtual void OnScroll(ScrollEventArgs e)
        {
            Scroll?.Invoke(this, e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x0201;

            // On Shift+click, range-select from the anchor in reading order instead of the native 2D region.
            if (m.Msg == WM_LBUTTONDOWN
                && (ModifierKeys & (Keys.Shift | Keys.Control)) == Keys.Shift
                && FocusedItem != null
                && HitTest(PointToClient(Cursor.Position)).Item is { } clicked)
            {
                SelectIndexRange(FocusedItem.Index, clicked.Index);
                return;
            }

            base.WndProc(ref m);

            if (m.Msg == PInvoke.WM_PAINT && Items.Count == 0 && !string.IsNullOrEmpty(EmptyText))
            {
                using var graphics = CreateGraphics();
                var top = this.AdjustForDPI(View == View.Details ? 40 : 16);
                const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                var bounds = new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
                var size = TextRenderer.MeasureText(graphics, EmptyText, Font, bounds.Size, Flags);
                var padding = this.AdjustForDPI(8);

                using (var background = new SolidBrush(BackColor))
                {
                    graphics.FillRectangle(background, (bounds.Width - size.Width) / 2 - padding, top - padding, size.Width + padding * 2, size.Height + padding * 2);
                }

                TextRenderer.DrawText(graphics, EmptyText, Font, bounds, Themer.CurrentThemeColors.ContrastSoft, Flags);
            }

            if (m.Msg == PInvoke.WM_VSCROLL)
            {
                OnScroll(new ScrollEventArgs((ScrollEventType)(m.WParam.ToInt32() & 0xffff), 0));
            }

            if (m.Msg == PInvoke.WM_MOUSEWHEEL)
            {
                OnScroll(new ScrollEventArgs(
                    ScrollEventType.EndScroll,
                    0 // no idea how to get scroll pos
                ));
            }
        }

        private void SelectIndexRange(int a, int b)
        {
            var min = Math.Min(a, b);
            var max = Math.Max(a, b);

            BeginUpdate();
            try
            {
                SelectedIndices.Clear();

                for (var i = min; i <= max; i++)
                {
                    SelectedIndices.Add(i);
                }
            }
            finally
            {
                EndUpdate();
            }
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            base.OnDrawItem(e);
            e.DrawDefault = true;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            base.OnDrawSubItem(e);
            e.DrawDefault = true;
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            AdjustColumnWidths();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustColumnWidths();

            if (Items.Count == 0 && EmptyText != null)
            {
                Invalidate();
            }
        }

        internal List<ListViewItem> GetSelectedVirtualItems()
        {
            var selected = new List<ListViewItem>();

            if (VirtualItems != null)
            {
                foreach (int index in SelectedIndices)
                {
                    selected.Add(VirtualItems[index]);
                }
            }

            return selected;
        }

        internal void AdjustColumnWidths(int flexibleColumnIndex = 0, int fixedColumnWidth = 100)
        {
            if (isAdjustingColumns || Columns.Count == 0 || View != View.Details)
            {
                return;
            }

            isAdjustingColumns = true;

            try
            {
                var scaledFixedWidth = this.AdjustForDPI(fixedColumnWidth);
                var availableWidth = ClientSize.Width;
                var fixedColumnsWidth = 0;

                var sharedColumn = SharedFlexibleColumn < Columns.Count ? SharedFlexibleColumn : -1;
                var flexibleColumns = sharedColumn >= 0 ? 2 : 1;

                for (var i = 0; i < Columns.Count; i++)
                {
                    if (i != flexibleColumnIndex && i != sharedColumn)
                    {
                        fixedColumnsWidth += scaledFixedWidth;
                    }
                }

                var flexibleWidth = Math.Max((availableWidth - fixedColumnsWidth) / flexibleColumns, scaledFixedWidth);

                for (var i = 0; i < Columns.Count; i++)
                {
                    var targetWidth = i == flexibleColumnIndex || i == sharedColumn ? flexibleWidth : scaledFixedWidth;

                    if (Columns[i].Width != targetWidth)
                    {
                        Columns[i].Width = targetWidth;
                    }
                }
            }
            finally
            {
                isAdjustingColumns = false;
            }
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using var borderPen = new Pen(BorderColor);
            borderPen.Width = this.AdjustForDPI(1);

            e.Graphics.DrawLine(borderPen,
                e.Bounds.Left, e.Bounds.Bottom - 1,
                e.Bounds.Right, e.Bounds.Bottom - 1);

            if (e.Header?.DisplayIndex < Columns.Count - 1)
            {
                var pos = e.Bounds.Right - 2;
                e.Graphics.DrawLine(borderPen,
                    pos, e.Bounds.Top,
                    pos, e.Bounds.Bottom - 1);
            }

            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", e.Font,
                e.Bounds, ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            if (ListViewItemSorter is ListViewColumnSorter sorter && e.ColumnIndex == sorter.SortColumn && sorter.Order != SortOrder.None)
            {
                var iconName = sorter.Order == SortOrder.Ascending ? "SortUp" : "SortDown";

                var icon = AppIcons.Icons[iconName];
                var size = AppIcons.ImageList.ImageSize;
                var x = e.Bounds.Right - size.Width - this.AdjustForDPI(4);
                var y = e.Bounds.Top + (e.Bounds.Height - size.Height) / 2;

                AppIcons.ImageList.Draw(e.Graphics, x, y, size.Width, size.Height, icon);
            }
        }
    }
}
