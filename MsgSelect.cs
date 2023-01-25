using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Menu;

namespace ArgosyUpdater
{
    public partial class MsgSelect : Form
    {
        public string selectedVal;

        public MsgSelect(string[] value)
        {
            InitializeComponent();

            this.comboBox1.Items.AddRange(value);
            this.comboBox1.SelectedIndex= 0;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            selectedVal = (string)this.comboBox1.SelectedItem;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
