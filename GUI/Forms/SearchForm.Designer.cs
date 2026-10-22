using GUI.Controls;

namespace GUI.Forms
{
    partial class SearchForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            findTextBoxPanel = new UnstyledPanel();
            findTextBox = new System.Windows.Forms.TextBox();
            matchCaseToggle = new ThemedToggleButton();
            wholeWordToggle = new ThemedToggleButton();
            regexToggle = new ThemedToggleButton();
            hexBytesToggle = new ThemedToggleButton();
            hintLabel = new System.Windows.Forms.Label();
            targetLabel = new System.Windows.Forms.Label();
            nameRadioButton = new System.Windows.Forms.RadioButton();
            pathRadioButton = new System.Windows.Forms.RadioButton();
            contentsRadioButton = new System.Windows.Forms.RadioButton();
            onlyInFolderCheckBox = new System.Windows.Forms.CheckBox();
            extensionLabel = new System.Windows.Forms.Label();
            extensionComboBox = new ThemedComboBox();
            filterKeyLabel = new System.Windows.Forms.Label();
            filterTable = new System.Windows.Forms.TableLayoutPanel();
            filterKeyComboBox = new ThemedComboBox();
            filterValueComboBox = new ThemedComboBox();
            findButton = new ThemedButton();
            cancelButton = new ThemedButton();
            toolTip = new System.Windows.Forms.ToolTip(components);
            findTextBoxPanel.SuspendLayout();
            filterTable.SuspendLayout();
            SuspendLayout();
            //
            // findTextBoxPanel
            //
            findTextBoxPanel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            findTextBoxPanel.Controls.Add(findTextBox);
            findTextBoxPanel.Location = new System.Drawing.Point(12, 12);
            findTextBoxPanel.Name = "findTextBoxPanel";
            findTextBoxPanel.Padding = new System.Windows.Forms.Padding(6, 4, 4, 3);
            findTextBoxPanel.Size = new System.Drawing.Size(308, 26);
            findTextBoxPanel.TabIndex = 0;
            findTextBoxPanel.Paint += FindTextBoxPanel_Paint;
            //
            // findTextBox
            //
            findTextBox.AccessibleName = "Search text";
            findTextBox.BorderStyle = System.Windows.Forms.BorderStyle.None;
            findTextBox.Dock = System.Windows.Forms.DockStyle.Fill;
            findTextBox.Name = "findTextBox";
            findTextBox.TabIndex = 0;
            //
            // matchCaseToggle
            //
            matchCaseToggle.AccessibleName = "Match case";
            matchCaseToggle.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            matchCaseToggle.Location = new System.Drawing.Point(326, 12);
            matchCaseToggle.Name = "matchCaseToggle";
            matchCaseToggle.Size = new System.Drawing.Size(34, 26);
            matchCaseToggle.TabIndex = 1;
            matchCaseToggle.Text = "Aa";
            toolTip.SetToolTip(matchCaseToggle, "Match case (Alt+C)");
            //
            // wholeWordToggle
            //
            wholeWordToggle.AccessibleName = "Match whole word";
            wholeWordToggle.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            wholeWordToggle.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Underline);
            wholeWordToggle.Location = new System.Drawing.Point(364, 12);
            wholeWordToggle.Name = "wholeWordToggle";
            wholeWordToggle.Size = new System.Drawing.Size(34, 26);
            wholeWordToggle.TabIndex = 2;
            wholeWordToggle.Text = "ab";
            toolTip.SetToolTip(wholeWordToggle, "Match whole word (Alt+W)");
            //
            // regexToggle
            //
            regexToggle.AccessibleName = "Use regular expression";
            regexToggle.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            regexToggle.Location = new System.Drawing.Point(402, 12);
            regexToggle.Name = "regexToggle";
            regexToggle.Size = new System.Drawing.Size(34, 26);
            regexToggle.TabIndex = 3;
            regexToggle.Text = ".*";
            toolTip.SetToolTip(regexToggle, "Use regular expression, ^name$ matches a whole name exactly (Alt+R)");
            //
            // hexBytesToggle
            //
            hexBytesToggle.AccessibleName = "Hex bytes";
            hexBytesToggle.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            hexBytesToggle.Location = new System.Drawing.Point(440, 12);
            hexBytesToggle.Name = "hexBytesToggle";
            hexBytesToggle.Size = new System.Drawing.Size(34, 26);
            hexBytesToggle.TabIndex = 4;
            hexBytesToggle.Text = "0x";
            toolTip.SetToolTip(hexBytesToggle, "Search file contents for hex bytes, such as 56 42 4B 56 (Alt+X)");
            //
            // hintLabel
            //
            hintLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            hintLabel.AutoEllipsis = true;
            hintLabel.Location = new System.Drawing.Point(12, 41);
            hintLabel.Name = "hintLabel";
            hintLabel.Size = new System.Drawing.Size(462, 19);
            hintLabel.TabIndex = 5;
            hintLabel.UseMnemonic = false;
            //
            // targetLabel
            //
            targetLabel.AutoSize = true;
            targetLabel.Location = new System.Drawing.Point(12, 72);
            targetLabel.Name = "targetLabel";
            targetLabel.Size = new System.Drawing.Size(63, 19);
            targetLabel.TabIndex = 6;
            targetLabel.Text = "Search &in";
            //
            // nameRadioButton
            //
            nameRadioButton.AutoSize = true;
            nameRadioButton.Checked = true;
            nameRadioButton.Location = new System.Drawing.Point(110, 70);
            nameRadioButton.Name = "nameRadioButton";
            nameRadioButton.Size = new System.Drawing.Size(63, 23);
            nameRadioButton.TabIndex = 7;
            nameRadioButton.TabStop = true;
            nameRadioButton.Text = "&Name";
            //
            // pathRadioButton
            //
            pathRadioButton.AutoSize = true;
            pathRadioButton.Location = new System.Drawing.Point(185, 70);
            pathRadioButton.Name = "pathRadioButton";
            pathRadioButton.Size = new System.Drawing.Size(55, 23);
            pathRadioButton.TabIndex = 8;
            pathRadioButton.Text = "&Path";
            //
            // contentsRadioButton
            //
            contentsRadioButton.AutoSize = true;
            contentsRadioButton.Location = new System.Drawing.Point(252, 70);
            contentsRadioButton.Name = "contentsRadioButton";
            contentsRadioButton.Size = new System.Drawing.Size(82, 23);
            contentsRadioButton.TabIndex = 9;
            contentsRadioButton.Text = "Con&tents";
            //
            // onlyInFolderCheckBox
            //
            onlyInFolderCheckBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            onlyInFolderCheckBox.Location = new System.Drawing.Point(12, 103);
            onlyInFolderCheckBox.Name = "onlyInFolderCheckBox";
            onlyInFolderCheckBox.Size = new System.Drawing.Size(462, 24);
            onlyInFolderCheckBox.TabIndex = 10;
            onlyInFolderCheckBox.Text = "&Only in selected folder";
            onlyInFolderCheckBox.SizeChanged += OnlyInFolderCheckBox_SizeChanged;
            //
            // extensionLabel
            //
            extensionLabel.AutoSize = true;
            extensionLabel.Location = new System.Drawing.Point(12, 140);
            extensionLabel.Name = "extensionLabel";
            extensionLabel.Size = new System.Drawing.Size(66, 19);
            extensionLabel.TabIndex = 11;
            extensionLabel.Text = "&Extension";
            //
            // extensionComboBox
            //
            extensionComboBox.AccessibleName = "Extension";
            extensionComboBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            extensionComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            extensionComboBox.Location = new System.Drawing.Point(110, 137);
            extensionComboBox.Margin = new System.Windows.Forms.Padding(0);
            extensionComboBox.Name = "extensionComboBox";
            extensionComboBox.Size = new System.Drawing.Size(364, 26);
            extensionComboBox.TabIndex = 12;
            //
            // filterKeyLabel
            //
            filterKeyLabel.AutoSize = true;
            filterKeyLabel.Location = new System.Drawing.Point(12, 176);
            filterKeyLabel.Name = "filterKeyLabel";
            filterKeyLabel.Size = new System.Drawing.Size(70, 19);
            filterKeyLabel.TabIndex = 13;
            filterKeyLabel.Text = "&Asset info";
            toolTip.SetToolTip(filterKeyLabel, "Only files whose searchable user data in the tools asset info has this key, and optionally this value");
            //
            // filterTable
            //
            filterTable.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            filterTable.ColumnCount = 2;
            filterTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 50F));
            filterTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 50F));
            filterTable.Controls.Add(filterKeyComboBox, 0, 0);
            filterTable.Controls.Add(filterValueComboBox, 1, 0);
            filterTable.Location = new System.Drawing.Point(110, 173);
            filterTable.Margin = new System.Windows.Forms.Padding(0);
            filterTable.Name = "filterTable";
            filterTable.RowCount = 1;
            filterTable.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            filterTable.Size = new System.Drawing.Size(364, 26);
            filterTable.TabIndex = 14;
            //
            // filterKeyComboBox
            //
            filterKeyComboBox.AccessibleName = "Asset info key";
            filterKeyComboBox.AutoCompleteMode = System.Windows.Forms.AutoCompleteMode.SuggestAppend;
            filterKeyComboBox.AutoCompleteSource = System.Windows.Forms.AutoCompleteSource.ListItems;
            filterKeyComboBox.Dock = System.Windows.Forms.DockStyle.Fill;
            filterKeyComboBox.Margin = new System.Windows.Forms.Padding(0, 0, 3, 0);
            filterKeyComboBox.Name = "filterKeyComboBox";
            filterKeyComboBox.TabIndex = 0;
            filterKeyComboBox.TrimStart = true;
            //
            // filterValueComboBox
            //
            filterValueComboBox.AccessibleName = "Asset info value";
            filterValueComboBox.AutoCompleteMode = System.Windows.Forms.AutoCompleteMode.SuggestAppend;
            filterValueComboBox.AutoCompleteSource = System.Windows.Forms.AutoCompleteSource.ListItems;
            filterValueComboBox.Dock = System.Windows.Forms.DockStyle.Fill;
            filterValueComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            filterValueComboBox.Enabled = false;
            filterValueComboBox.Margin = new System.Windows.Forms.Padding(3, 0, 0, 0);
            filterValueComboBox.Name = "filterValueComboBox";
            filterValueComboBox.TabIndex = 1;
            filterValueComboBox.TrimStart = true;
            //
            // findButton
            //
            findButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            findButton.DialogResult = System.Windows.Forms.DialogResult.OK;
            findButton.Location = new System.Drawing.Point(292, 215);
            findButton.Name = "findButton";
            findButton.Size = new System.Drawing.Size(88, 31);
            findButton.TabIndex = 15;
            findButton.Text = "Find";
            //
            // cancelButton
            //
            cancelButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            cancelButton.Location = new System.Drawing.Point(386, 215);
            cancelButton.Name = "cancelButton";
            cancelButton.Size = new System.Drawing.Size(88, 31);
            cancelButton.TabIndex = 16;
            cancelButton.Text = "Cancel";
            //
            // SearchForm
            //
            AcceptButton = findButton;
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            CancelButton = cancelButton;
            ClientSize = new System.Drawing.Size(486, 258);
            Controls.Add(findTextBoxPanel);
            Controls.Add(matchCaseToggle);
            Controls.Add(wholeWordToggle);
            Controls.Add(regexToggle);
            Controls.Add(hexBytesToggle);
            Controls.Add(hintLabel);
            Controls.Add(targetLabel);
            Controls.Add(nameRadioButton);
            Controls.Add(pathRadioButton);
            Controls.Add(contentsRadioButton);
            Controls.Add(onlyInFolderCheckBox);
            Controls.Add(extensionLabel);
            Controls.Add(extensionComboBox);
            Controls.Add(filterKeyLabel);
            Controls.Add(filterTable);
            Controls.Add(findButton);
            Controls.Add(cancelButton);
            Font = new System.Drawing.Font("Segoe UI", 10F);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SearchForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            Text = "Find";
            findTextBoxPanel.ResumeLayout(false);
            findTextBoxPanel.PerformLayout();
            filterTable.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
        private UnstyledPanel findTextBoxPanel;
        private System.Windows.Forms.TextBox findTextBox;
        private ThemedToggleButton matchCaseToggle;
        private ThemedToggleButton wholeWordToggle;
        private ThemedToggleButton regexToggle;
        private ThemedToggleButton hexBytesToggle;
        private System.Windows.Forms.Label hintLabel;
        private System.Windows.Forms.Label targetLabel;
        private System.Windows.Forms.RadioButton nameRadioButton;
        private System.Windows.Forms.RadioButton pathRadioButton;
        private System.Windows.Forms.RadioButton contentsRadioButton;
        private System.Windows.Forms.CheckBox onlyInFolderCheckBox;
        private System.Windows.Forms.Label extensionLabel;
        private ThemedComboBox extensionComboBox;
        private System.Windows.Forms.Label filterKeyLabel;
        private System.Windows.Forms.TableLayoutPanel filterTable;
        private ThemedComboBox filterKeyComboBox;
        private ThemedComboBox filterValueComboBox;
        private ThemedButton findButton;
        private ThemedButton cancelButton;
        private System.Windows.Forms.ToolTip toolTip;
    }
}
