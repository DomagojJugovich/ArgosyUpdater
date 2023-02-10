using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArgosyUpdater
{
    public partial class Progress : Form
    {
        private StringBuilderExt _chg;
        private StringBuilderExt _err;

        public Progress(StringBuilderExt chg, StringBuilderExt err)
        {
            InitializeComponent();

            this.StartPosition= FormStartPosition.CenterScreen;

            _chg = chg;
            _err = err;
            chg.NotifyProgress += OnChangeChange;
            err.NotifyProgress += OnErrorChange;
        }

       

        public void OnChangeChange(string value)
        {
            this.textLogCh.AppendText(value + Environment.NewLine);
            this.Update();
        }

        public void OnErrorChange(string value)
        {
            this.textLogErr.AppendText(value + Environment.NewLine );
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
