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
            findTextBox = new System.Windows.Forms.TextBox();
            matchCaseToggle = new ThemedToggleButton();
            wholeWordToggle = new ThemedToggleButton();
            regexToggle = new ThemedToggleButton();
            hexBytesToggle = new ThemedToggleButton();
            targetLabel = new System.Windows.Forms.Label();
            nameRadioButton = new System.Windows.Forms.RadioButton();
            pathRadioButton = new System.Windows.Forms.RadioButton();
            contentsRadioButton = new System.Windows.Forms.RadioButton();
            onlyInFolderCheckBox = new System.Windows.Forms.CheckBox();
            extensionLabel = new System.Windows.Forms.Label();
            extensionComboBox = new ThemedComboBox();
            filterKeyLabel = new System.Windows.Forms.Label();
            filterKeyComboBox = new ThemedComboBox();
            filterValueComboBox = new ThemedComboBox();
            errorLabel = new System.Windows.Forms.Label();
            findButton = new ThemedButton();
            cancelButton = new ThemedButton();
            toolTip = new System.Windows.Forms.ToolTip(components);
            SuspendLayout();
            //
            // findTextBox
            //
            findTextBox.Location = new System.Drawing.Point(13, 14);
            findTextBox.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            findTextBox.Name = "findTextBox";
            findTextBox.Size = new System.Drawing.Size(306, 25);
            findTextBox.TabIndex = 0;
            //
            // matchCaseToggle
            //
            matchCaseToggle.AccessibleName = "Match case";
            matchCaseToggle.Location = new System.Drawing.Point(325, 14);
            matchCaseToggle.Name = "matchCaseToggle";
            matchCaseToggle.Size = new System.Drawing.Size(34, 25);
            matchCaseToggle.TabIndex = 1;
            matchCaseToggle.Text = "Aa";
            toolTip.SetToolTip(matchCaseToggle, "Match case");
            //
            // wholeWordToggle
            //
            wholeWordToggle.AccessibleName = "Match whole word";
            wholeWordToggle.Location = new System.Drawing.Point(363, 14);
            wholeWordToggle.Name = "wholeWordToggle";
            wholeWordToggle.Size = new System.Drawing.Size(34, 25);
            wholeWordToggle.TabIndex = 2;
            wholeWordToggle.Text = "ab";
            toolTip.SetToolTip(wholeWordToggle, "Match whole word");
            //
            // regexToggle
            //
            regexToggle.AccessibleName = "Use regular expression";
            regexToggle.Location = new System.Drawing.Point(401, 14);
            regexToggle.Name = "regexToggle";
            regexToggle.Size = new System.Drawing.Size(34, 25);
            regexToggle.TabIndex = 3;
            regexToggle.Text = ".*";
            toolTip.SetToolTip(regexToggle, "Use regular expression");
            //
            // hexBytesToggle
            //
            hexBytesToggle.AccessibleName = "Hex bytes";
            hexBytesToggle.Location = new System.Drawing.Point(439, 14);
            hexBytesToggle.Name = "hexBytesToggle";
            hexBytesToggle.Size = new System.Drawing.Size(34, 25);
            hexBytesToggle.TabIndex = 4;
            hexBytesToggle.Text = "0x";
            toolTip.SetToolTip(hexBytesToggle, "Search file contents for hex bytes, such as 56 42 4B 56");
            //
            // targetLabel
            //
            targetLabel.AutoSize = true;
            targetLabel.Location = new System.Drawing.Point(13, 54);
            targetLabel.Name = "targetLabel";
            targetLabel.Size = new System.Drawing.Size(63, 19);
            targetLabel.TabIndex = 5;
            targetLabel.Text = "Search in";
            //
            // nameRadioButton
            //
            nameRadioButton.AutoSize = true;
            nameRadioButton.Checked = true;
            nameRadioButton.Location = new System.Drawing.Point(110, 52);
            nameRadioButton.Name = "nameRadioButton";
            nameRadioButton.Size = new System.Drawing.Size(63, 23);
            nameRadioButton.TabIndex = 6;
            nameRadioButton.TabStop = true;
            nameRadioButton.Text = "Name";
            //
            // pathRadioButton
            //
            pathRadioButton.AutoSize = true;
            pathRadioButton.Location = new System.Drawing.Point(185, 52);
            pathRadioButton.Name = "pathRadioButton";
            pathRadioButton.Size = new System.Drawing.Size(55, 23);
            pathRadioButton.TabIndex = 7;
            pathRadioButton.Text = "Path";
            //
            // contentsRadioButton
            //
            contentsRadioButton.AutoSize = true;
            contentsRadioButton.Location = new System.Drawing.Point(252, 52);
            contentsRadioButton.Name = "contentsRadioButton";
            contentsRadioButton.Size = new System.Drawing.Size(82, 23);
            contentsRadioButton.TabIndex = 8;
            contentsRadioButton.Text = "Contents";
            //
            // onlyInFolderCheckBox
            //
            onlyInFolderCheckBox.AutoEllipsis = true;
            onlyInFolderCheckBox.Location = new System.Drawing.Point(13, 84);
            onlyInFolderCheckBox.Name = "onlyInFolderCheckBox";
            onlyInFolderCheckBox.Size = new System.Drawing.Size(460, 24);
            onlyInFolderCheckBox.TabIndex = 9;
            onlyInFolderCheckBox.Text = "Only in selected folder";
            //
            // extensionLabel
            //
            extensionLabel.AutoSize = true;
            extensionLabel.Location = new System.Drawing.Point(13, 121);
            extensionLabel.Name = "extensionLabel";
            extensionLabel.Size = new System.Drawing.Size(66, 19);
            extensionLabel.TabIndex = 10;
            extensionLabel.Text = "Extension";
            //
            // extensionComboBox
            //
            extensionComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            extensionComboBox.Location = new System.Drawing.Point(110, 118);
            extensionComboBox.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            extensionComboBox.Name = "extensionComboBox";
            extensionComboBox.Size = new System.Drawing.Size(363, 26);
            extensionComboBox.TabIndex = 11;
            //
            // filterKeyLabel
            //
            filterKeyLabel.AutoSize = true;
            filterKeyLabel.Location = new System.Drawing.Point(13, 155);
            filterKeyLabel.Name = "filterKeyLabel";
            filterKeyLabel.Size = new System.Drawing.Size(70, 19);
            filterKeyLabel.TabIndex = 12;
            filterKeyLabel.Text = "Asset info";
            //
            // filterKeyComboBox
            //
            filterKeyComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            filterKeyComboBox.Location = new System.Drawing.Point(110, 152);
            filterKeyComboBox.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            filterKeyComboBox.Name = "filterKeyComboBox";
            filterKeyComboBox.Size = new System.Drawing.Size(363, 26);
            filterKeyComboBox.TabIndex = 13;
            //
            // filterValueComboBox
            //
            filterValueComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            filterValueComboBox.Location = new System.Drawing.Point(110, 184);
            filterValueComboBox.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            filterValueComboBox.Name = "filterValueComboBox";
            filterValueComboBox.Size = new System.Drawing.Size(363, 26);
            filterValueComboBox.TabIndex = 14;
            filterValueComboBox.Visible = false;
            //
            // errorLabel
            //
            errorLabel.AutoEllipsis = true;
            errorLabel.Location = new System.Drawing.Point(13, 222);
            errorLabel.Name = "errorLabel";
            errorLabel.Size = new System.Drawing.Size(272, 38);
            errorLabel.TabIndex = 15;
            //
            // findButton
            //
            findButton.DialogResult = System.Windows.Forms.DialogResult.OK;
            findButton.Location = new System.Drawing.Point(293, 226);
            findButton.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            findButton.Name = "findButton";
            findButton.Size = new System.Drawing.Size(88, 31);
            findButton.TabIndex = 16;
            findButton.Text = "Find";
            //
            // cancelButton
            //
            cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            cancelButton.Location = new System.Drawing.Point(385, 226);
            cancelButton.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            cancelButton.Name = "cancelButton";
            cancelButton.Size = new System.Drawing.Size(88, 31);
            cancelButton.TabIndex = 17;
            cancelButton.Text = "Cancel";
            //
            // SearchForm
            //
            AcceptButton = findButton;
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            CancelButton = cancelButton;
            ClientSize = new System.Drawing.Size(486, 270);
            Controls.Add(findTextBox);
            Controls.Add(matchCaseToggle);
            Controls.Add(wholeWordToggle);
            Controls.Add(regexToggle);
            Controls.Add(hexBytesToggle);
            Controls.Add(targetLabel);
            Controls.Add(nameRadioButton);
            Controls.Add(pathRadioButton);
            Controls.Add(contentsRadioButton);
            Controls.Add(onlyInFolderCheckBox);
            Controls.Add(extensionLabel);
            Controls.Add(extensionComboBox);
            Controls.Add(filterKeyLabel);
            Controls.Add(filterKeyComboBox);
            Controls.Add(filterValueComboBox);
            Controls.Add(errorLabel);
            Controls.Add(findButton);
            Controls.Add(cancelButton);
            Font = new System.Drawing.Font("Segoe UI", 10F);
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SearchForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            Text = "Find";
            Load += SearchForm_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
        private System.Windows.Forms.TextBox findTextBox;
        private ThemedToggleButton matchCaseToggle;
        private ThemedToggleButton wholeWordToggle;
        private ThemedToggleButton regexToggle;
        private ThemedToggleButton hexBytesToggle;
        private System.Windows.Forms.Label targetLabel;
        private System.Windows.Forms.RadioButton nameRadioButton;
        private System.Windows.Forms.RadioButton pathRadioButton;
        private System.Windows.Forms.RadioButton contentsRadioButton;
        private System.Windows.Forms.CheckBox onlyInFolderCheckBox;
        private System.Windows.Forms.Label extensionLabel;
        private ThemedComboBox extensionComboBox;
        private System.Windows.Forms.Label filterKeyLabel;
        private ThemedComboBox filterKeyComboBox;
        private ThemedComboBox filterValueComboBox;
        private System.Windows.Forms.Label errorLabel;
        private ThemedButton findButton;
        private ThemedButton cancelButton;
        private System.Windows.Forms.ToolTip toolTip;
    }
}
