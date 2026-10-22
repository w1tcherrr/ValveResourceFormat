#if DEBUG
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.GLViewers;

namespace GUI.Automation;

/// <summary>
/// Tools that read and change the controls in a viewer's sidebar. A change goes through the control
/// itself, so the sidebar shows it and the viewer runs the same callback as for a click.
/// </summary>
internal sealed partial class McpTools
{
    /// <param name="Label">The name the sidebar shows, which the tools use to find the control.</param>
    /// <param name="Group">The title of the group box or section it is in, or null outside any.</param>
    private sealed record SidebarControl(Control Control, string Label, string? Group);

    /// <summary>One change of <see cref="SetControls"/>.</summary>
    private sealed record ControlChange(string Name, string? Group = null, string? Item = null, JsonNode? Value = null);

    [Tool("List the controls in a tab's sidebar and toolbar, top to bottom, each with its kind, group and state. Kinds are checkbox, toggle, dropdown, list (a checked list such as world layers or mesh groups), slider (0 to 1), and number (with its range). The others are vector (such as a material or animation graph parameter), button and text. The render mode dropdown is in the Toolbar group.")]
    private static List<object> ListControls(GLBaseControl viewer, [Description("Include the items of dropdowns and lists. An animation list can have hundreds.")] bool items = false)
        => SidebarControls(viewer.UiControl).Select(control => DescribeControl(control, items)).ToList();

    [Tool("Change sidebar or toolbar controls in order, as a user would. Stops at the first change that fails and keeps the changes before it. A choice that moves the camera, such as a map camera, moves it at once. Returns each changed control once.", Redraws = true)]
    private static List<object> SetControls(
        GLBaseControl viewer,
        [Description("'name' is the label from list_controls. Give 'group' only when two controls have the same label. 'item' is the list item to check. 'value' is true or false for a checkbox, toggle or list item, and the item text or index for a dropdown. It is a number for a slider or number, and a number array for a vector. Omit 'value' to press a button.")] List<ControlChange> changes)
    {
        var scene = viewer as GLSceneViewer;
        var location = scene?.Input.Camera.Location;
        var angles = scene?.Input.Camera.GetQAngle();
        try
        {
            return ApplyControls(viewer.UiControl, changes, details: false);
        }
        finally
        {
            if (scene != null && (scene.Input.Camera.Location != location || scene.Input.Camera.GetQAngle() != angles))
            {
                scene.Input.SaveCameraForTransition(transitionDuration: 0f);
            }
        }
    }

    /// <summary>Applies changes in order, stopping at the first that fails, and describes each changed control once.</summary>
    private static List<object> ApplyControls(Control? root, List<ControlChange> changes, bool details)
    {
        var changed = new List<SidebarControl>();

        for (var i = 0; i < changes.Count; i++)
        {
            SidebarControl control;

            try
            {
                control = ApplyControl(root, changes[i]);
            }
            catch (Exception e) when (e is ToolException or ArgumentException)
            {
                throw new ToolException($"Change {i + 1} of {changes.Count} failed. The changes before it stay applied. {e.Message}");
            }

            if (!changed.Exists(other => other.Control == control.Control))
            {
                changed.Add(control);
            }
        }

        // A checked list item is reported through the list, so the list comes back with its items
        return changed.Select(control => DescribeControl(control, items: control.Control is CheckedListBox, details)).ToList();
    }

    private static SidebarControl ApplyControl(Control? root, ControlChange change)
    {
        var (name, group, item, value) = change;

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Every change needs a control 'name'.");
        }

        var controls = SidebarControls(root);
        var caption = StripMnemonics(name);
        var matches = controls.Where(control => (control.Label.Equals(name, StringComparison.OrdinalIgnoreCase)
                || control.Label.Equals(caption, StringComparison.OrdinalIgnoreCase)
                || control.Control.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            && (group == null || string.Equals(control.Group, group, StringComparison.OrdinalIgnoreCase))).ToList();

        if (matches.Count == 0)
        {
            var inGroup = group == null ? null : $" in group '{group}'";
            var changeable = controls.Where(static control => control.Control is not Label).Select(static control => control.Label).Distinct();
            throw new ToolException($"No control named '{name}'{inGroup}. The controls are: {string.Join(", ", changeable)}.");
        }

        // A hidden control with the same name does not count
        if (matches.Count > 1)
        {
            var visible = matches.FindAll(static match => match.Control.Visible);

            if (visible.Count == 1)
            {
                matches = visible;
            }
        }

        if (matches.Count > 1)
        {
            throw new ToolException($"'{name}' is in more than one group: {string.Join(", ", matches.Select(static match => match.Group ?? "none"))}. Pass 'group'.");
        }

        var control = matches[0];

        if (!control.Control.Visible || !control.Control.Enabled)
        {
            var state = control.Control.Visible ? "disabled" : "hidden";
            throw new ToolException($"'{name}' is {state}, so a user cannot change it.");
        }

        T Value<T>(string expected)
        {
            try
            {
                return (value ?? throw new JsonException()).Deserialize<T>(Json) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ArgumentException($"'value' must be {expected} for '{name}'.");
            }
        }

        switch (control.Control)
        {
            case CheckBox checkBox:
                checkBox.Checked = Value<bool>("true or false");
                break;

            case CheckedListBox list:
                if (item == null)
                {
                    throw new ArgumentException($"Pass the 'item' of '{name}' to change.");
                }

                var index = list.FindStringExact(item);

                if (index < 0)
                {
                    throw new ToolException($"'{name}' has no item '{item}'.");
                }

                list.SetItemChecked(index, Value<bool>("true or false"));
                break;

            case ComboBox comboBox when value?.GetValueKind() == JsonValueKind.Number:
                var selected = Value<int>("an index");

                if (selected < 0 || selected >= comboBox.Items.Count || IsHeader(comboBox, selected))
                {
                    throw new ToolException($"'{name}' has no item at index {selected}.");
                }

                comboBox.SelectedIndex = selected;
                break;

            case ComboBox comboBox:
                var text = Value<string>("an item or its index");
                var found = FindItem(comboBox, text);

                if (found < 0)
                {
                    throw new ToolException($"'{name}' has no item '{text}'.");
                }

                comboBox.SelectedIndex = found;
                break;

            case Slider slider:
                // The knob clamps to 0 to 1, and reports the change as a drag does
                slider.Value = Value<float>("a number from 0 to 1");
                slider.ValueChanged?.Invoke(slider.Value);
                break;

            case ThemedFloatNumeric number:
                number.Value = Math.Clamp(Value<float>("a number"), number.MinValue, number.MaxValue);
                break;

            case ThemedIntNumeric number:
                number.Value = Math.Clamp(Value<int>("an integer"), number.MinValue, number.MaxValue);
                break;

            case PropertyRow vector:
                var components = vector.Editors.Count;
                var values = Value<float[]>($"an array of {components} numbers");

                if (values.Length != components)
                {
                    throw new ArgumentException($"'value' must be an array of {components} numbers for '{name}'.");
                }

                for (var i = 0; i < values.Length; i++)
                {
                    ((ThemedFloatNumeric)vector.Editors[i]).Value = values[i];
                }

                break;

            case ThemedToggleButton toggle:
                if (value == null || toggle.Checked != Value<bool>("true or false"))
                {
                    toggle.PerformClick();
                }

                break;

            case Button button:
                button.PerformClick();
                break;

            case RadioButton radio:
                if (value != null && !Value<bool>("true"))
                {
                    throw new ArgumentException($"'{name}' is cleared by selecting another option.");
                }

                radio.PerformClick();
                break;

            case TextBoxBase textBox:
                textBox.Text = Value<string>("a string");
                break;

            default:
                throw new ToolException($"'{name}' is read-only text.");
        }

        return control;
    }

    /// <param name="details">Add what a dialog's controls need: the control's own name, its text, tab index and focus.</param>
    private static object DescribeControl(SidebarControl control, bool items, bool details = false)
    {
        var input = control.Control;

        return new
        {
            Kind = KindOf(input),
            control.Label,
            Name = details && input.Name != control.Label ? input.Name : null,
            control.Group,
            Value = ValueOf(input),
            Text = details && input is not (TextBoxBase or ListControl) && CaptionOf(input) != control.Label ? input.Text : null,
            Items = items ? ListItems(input) : null,
            Disabled = Flag(!input.Enabled),
            Hidden = Flag(!input.Visible),
            TabIndex = details ? input.TabIndex : (int?)null,
            Focused = Flag(details && input == FocusedControl(input.FindForm())),
        };
    }

    private static string KindOf(Control input) => input switch
    {
        CheckBox => "checkbox",
        RadioButton => "radio",
        CheckedListBox => "list",
        ComboBox => "dropdown",
        Slider => "slider",
        ThemedFloatNumeric or ThemedIntNumeric => "number",
        PropertyRow => "vector",
        ThemedToggleButton => "toggle",
        Button => "button",
        TextBoxBase => "textbox",
        _ => "text",
    };

    private static object? ValueOf(Control input) => input switch
    {
        CheckBox checkBox => checkBox.Checked,
        RadioButton radio => radio.Checked,
        ComboBox comboBox when comboBox.SelectedIndex >= 0 => ItemText(comboBox, comboBox.Items[comboBox.SelectedIndex]),
        Slider slider => slider.Value,
        ThemedFloatNumeric number => new { number.Value, Min = Bound(number.MinValue), Max = Bound(number.MaxValue) },
        ThemedIntNumeric number => new { number.Value, Min = number.MinValue, Max = number.MaxValue },
        PropertyRow vector => vector.Editors.Cast<ThemedFloatNumeric>().Select(static number => number.Value).ToArray(),
        ThemedToggleButton toggle => toggle.Checked,
        TextBoxBase textBox => textBox.Text,
        _ => null,
    };

    /// <summary>The innermost active control of a form, which gets its keys, or the form itself when none is active.</summary>
    private static Control? FocusedControl(Form? form)
    {
        Control? focused = form;

        while (focused is ContainerControl { ActiveControl: { } active })
        {
            focused = active;
        }

        return focused;
    }

    /// <summary>The items of a dropdown or checked list, or null for any other control.</summary>
    private static object? ListItems(Control input) => input switch
    {
        CheckedListBox list => ListItems(list),
        ComboBox comboBox => ListItems(comboBox),
        _ => null,
    };

    /// <summary>Each item of a checked list, and whether it is checked.</summary>
    private static Dictionary<string, bool> ListItems(CheckedListBox list)
    {
        var items = new Dictionary<string, bool>(list.Items.Count);

        for (var i = 0; i < list.Items.Count; i++)
        {
            items[ItemText(list, list.Items[i])] = list.GetItemChecked(i);
        }

        return items;
    }

    /// <summary>Each item of a dropdown, with each header between dashes.</summary>
    private static List<string> ListItems(ComboBox comboBox)
    {
        var items = new List<string>(comboBox.Items.Count);

        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            var text = ItemText(comboBox, comboBox.Items[i]);

            if (IsHeader(comboBox, i))
            {
                text = $"-- {text} --";
            }

            items.Add(text);
        }

        return items;
    }

    /// <summary>A bound of a number input, or null for the end of the float range, which bounds nothing.</summary>
    private static float? Bound(float value) => value > float.MinValue && value < float.MaxValue ? value : null;

    private static string ItemText(ListControl list, object? item) => list.GetItemText(item) ?? string.Empty;

    private static bool IsHeader(ComboBox comboBox, int index) => comboBox.Items[index] is ThemedComboBoxItem { IsHeader: true };

    /// <summary>The index of the selectable item with this text, ignoring case, or -1.</summary>
    private static int FindItem(ComboBox comboBox, string text)
    {
        var first = comboBox.FindStringExact(text);
        var index = first;

        // The search starts after the given index and wraps around, so a return to the first match
        // means every match is a header
        while (index >= 0 && IsHeader(comboBox, index))
        {
            index = comboBox.FindStringExact(text, index);

            if (index == first)
            {
                return -1;
            }
        }

        return index;
    }

    /// <summary>Every control a user can see in a sidebar or window, top to bottom.</summary>
    private static List<SidebarControl> SidebarControls(Control? root)
    {
        var controls = new List<SidebarControl>();

        void Walk(Control parent, string? group)
        {
            var all = parent.Controls.Cast<Control>();

            // A sidebar docks every control to the top and brings it to the front as it is added, so its
            // collections hold them bottom to top. Controls placed by hand go in tab order.
            var children = parent switch
            {
                TableLayoutPanel table => all.OrderBy(child => table.GetRow(child)).ThenBy(child => table.GetColumn(child)),
                _ when all.All(static child => child.Dock == DockStyle.None) => all.OrderBy(static child => child.TabIndex),
                _ => all.Reverse(),
            };

            // A row without a name takes the name of the row above it, as the fields of a vector do
            var rowName = string.Empty;

            foreach (var child in children)
            {
                if (child is GLControl || IsOutOfSight(parent, child))
                {
                    continue;
                }

                var sectionTitle = SectionTitle(child);

                if (sectionTitle != null)
                {
                    group = sectionTitle;
                    continue;
                }

                if (child is PropertyRow row)
                {
                    var ownName = RowName(row);
                    rowName = ownName ?? rowName;
                    AddRowInputs(controls, row, ownName, rowName, group);
                    continue;
                }

                // Inputs are named where they are created
                if (!string.IsNullOrEmpty(child.AccessibleName))
                {
                    controls.Add(new SidebarControl(child, child.AccessibleName, group));
                    continue;
                }

                // Other buttons and labels use their text as the name, but a label that only repeats the name of an input beside it is skipped
                if (child is ButtonBase or Label && child.Text.Length > 0)
                {
                    if (child is ButtonBase || !HasInputNamed(parent, CaptionOf(child)))
                    {
                        controls.Add(new SidebarControl(child, CaptionOf(child).ReplaceLineEndings(", "), group));
                    }

                    continue;
                }

                // A dialog's input without a caption goes by its control name
                if (child is TextBoxBase or ListControl && child.Name.Length > 0)
                {
                    controls.Add(new SidebarControl(child, child.Name, group));
                    continue;
                }

                var childGroup = group;

                // A group box titles the controls in it, unless it frames a single input of that name, as a checked list's does
                if (child is GroupBox groupBox && !HasInputNamed(groupBox, CaptionOf(groupBox)))
                {
                    childGroup = CaptionOf(groupBox);
                }
                else if (child.AccessibleRole == AccessibleRole.ToolBar)
                {
                    childGroup = "Toolbar";
                }

                Walk(child, childGroup);
            }
        }

        if (root != null)
        {
            Walk(root, null);
        }

        return controls;
    }

    /// <summary>The text of a control as the screen shows it, without the ampersands that mark its access key.</summary>
    private static string CaptionOf(Control control)
        => control is ButtonBase { UseMnemonic: true } or Label { UseMnemonic: true } or GroupBox ? StripMnemonics(control.Text) : control.Text;

    /// <summary>Drops the ampersand that marks an access key, and turns a doubled one into a single ampersand.</summary>
    private static string StripMnemonics(string text) => Regex.Replace(text, "&(&?)", "$1");

    /// <summary>The text of a section header, which titles the controls below it, or null for any other control.</summary>
    private static string? SectionTitle(Control control) => control switch
    {
        Label { AccessibleRole: AccessibleRole.Grouping } header => header.Text,
        Panel { Controls: [Label { AccessibleRole: AccessibleRole.Grouping } header] } => header.Text,
        _ => null,
    };

    /// <summary>Whether <paramref name="container"/> holds an input named <paramref name="caption"/>, ignoring a trailing colon.</summary>
    private static bool HasInputNamed(Control container, string caption)
    {
        var name = caption.TrimEnd(':');

        foreach (Control child in container.Controls)
        {
            if (child.AccessibleName == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a control placed by hand, not docked, is out of sight. Its parent can clip it, or a
    /// sibling at the same spot in front of it can cover it. Panels swapped within one host do this.
    /// </summary>
    private static bool IsOutOfSight(Control parent, Control child)
    {
        if (child.Dock != DockStyle.None || !child.Visible)
        {
            return false;
        }

        if (!parent.DisplayRectangle.IntersectsWith(child.Bounds))
        {
            return true;
        }

        // The front of the z-order comes first in the collection
        var index = parent.Controls.GetChildIndex(child);

        for (var i = 0; i < index; i++)
        {
            var sibling = parent.Controls[i];

            if (sibling.Visible && sibling.Dock == DockStyle.None && sibling.Location == child.Location)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The name of a property row: its own, or the caption of its leading checkbox, or null for neither.</summary>
    private static string? RowName(PropertyRow row)
    {
        if (row.AccessibleName != null)
        {
            return row.AccessibleName;
        }

        if (row.Editors is [CheckBox { Text.Length: > 0 } leadingCheckBox])
        {
            return CaptionOf(leadingCheckBox);
        }

        return null;
    }

    /// <summary>Adds the inputs of a property row with names from the row, and a vector as one control with the row's name.</summary>
    /// <param name="ownName">The name of the row from <see cref="RowName"/>, which names its inputs, or null.</param>
    /// <param name="rowName">The name of a vector. For a row without a name, this is the name of the row above.</param>
    private static void AddRowInputs(List<SidebarControl> controls, PropertyRow row, string? ownName, string rowName, string? group)
    {
        // Only a vector has more than one editor, one for each component
        if (row.Editors.Count > 1)
        {
            controls.Add(new SidebarControl(row, rowName, group));
        }

        // Laid out left to right in the order they were added
        foreach (Control input in row.Controls)
        {
            var name = ownName == null ? input.AccessibleName : RowInputName(row, input, ownName);

            if (!string.IsNullOrEmpty(name))
            {
                controls.Add(new SidebarControl(input, name, group));
            }
        }
    }

    /// <summary>The name of an input in a named row: the row's name, plus the input's caption when it has its own.</summary>
    private static string? RowInputName(PropertyRow row, Control input, string rowName)
    {
        var isEditor = row.Editors.Contains(input);

        // The fields of a vector are one value, which the row stands for
        var isVectorField = isEditor && row.Editors.Count > 1;

        // The label beside the editors shows the name itself
        var isNameLabel = !isEditor && input is Label;

        if (isVectorField || isNameLabel)
        {
            return input.AccessibleName;
        }

        switch (input)
        {
            // It moves with the number beside it, which carries the value
            case Slider:
                return input.AccessibleName;

            // An icon or a color swatch, rather than an action of the row
            case Button { Text.Length: 0 }:
                return input.AccessibleName;

            // The caption tells it apart from the row's other inputs
            case CheckBox or Button when input.Text.Length > 0 && CaptionOf(input) != rowName:
                return $"{rowName} {CaptionOf(input)}";

            default:
                return rowName;
        }
    }
}
#endif
