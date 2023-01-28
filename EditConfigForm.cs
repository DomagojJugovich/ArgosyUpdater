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
    public partial class EditConfigForm : Form
    {
        public EditConfigForm(Settings confForEdit)
        {
            InitializeComponent();

            propertyGrid1.SelectedObject= confForEdit;
        }
    }
}
