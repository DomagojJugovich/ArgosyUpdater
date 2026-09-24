using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArgosyUpdater
{
    public partial class Progress : Form
    {
        private StringBuilderExt _chg;
        private StringBuilderExt _err;
        //UI thread context, does not depend on this form's handle (which may be gone when user closes the window during sync)
        private readonly SynchronizationContext _ui;

        public Progress(StringBuilderExt chg, StringBuilderExt err)
        {
            InitializeComponent();

            this.StartPosition= FormStartPosition.CenterScreen;

            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _chg = chg;
            _err = err;
            chg.NotifyProgress += OnChangeChange;
            err.NotifyProgress += OnErrorChange;
        }

        //sync runs on a worker thread (Program.RunPumping), events come from there, always posted to UI thread
        public void OnChangeChange(string value)
        {
            _ui.Post(_ => AppendLine(this.textLogCh, value), null);
        }

        public void OnErrorChange(string value)
        {
            _ui.Post(_ => AppendLine(this.textLogErr, value), null);
        }

        private void AppendLine(TextBox box, string value)
        {
            if (IsDisposed || box.IsDisposed) return;
            box.AppendText(value + Environment.NewLine);
            this.Update();
        }

        private void Progress_FormClosing(object sender, FormClosingEventArgs e)
        {
            _chg.NotifyProgress -= OnChangeChange;
            _chg = null;
            _err.NotifyProgress -= OnErrorChange;
            _err = null;
        }
    }
}
