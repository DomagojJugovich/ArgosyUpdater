namespace ArgosyUpdater
{
    partial class Progress
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
            this.textLogCh = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.textLogErr = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // textLogCh
            // 
            this.textLogCh.Location = new System.Drawing.Point(13, 25);
            this.textLogCh.Multiline = true;
            this.textLogCh.Name = "textLogCh";
            this.textLogCh.ReadOnly = true;
            this.textLogCh.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.textLogCh.Size = new System.Drawing.Size(775, 203);
            this.textLogCh.TabIndex = 0;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.ForeColor = System.Drawing.Color.Black;
            this.label1.Location = new System.Drawing.Point(12, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(76, 13);
            this.label1.TabIndex = 1;
            this.label1.Text = "Log Changes :";
            // 
            // textLogErr
            // 
            this.textLogErr.Location = new System.Drawing.Point(13, 249);
            this.textLogErr.Multiline = true;
            this.textLogErr.Name = "textLogErr";
            this.textLogErr.ReadOnly = true;
            this.textLogErr.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.textLogErr.Size = new System.Drawing.Size(775, 194);
            this.textLogErr.TabIndex = 2;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.ForeColor = System.Drawing.Color.Black;
            this.label2.Location = new System.Drawing.Point(12, 232);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(61, 13);
            this.label2.TabIndex = 3;
            this.label2.Text = "Log Errors :";
            // 
            // Progress
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.textLogErr);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.textLogCh);
            this.Name = "Progress";
            this.Text = "Progress";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Progress_FormClosing);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox textLogCh;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TextBox textLogErr;
        private System.Windows.Forms.Label label2;
    }
}