using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArgosyUpdater
{

    public class FolderPair
    {
        public int ID { get; set; }
        public Boolean Sync { get; set; }
        [EditorAttribute(typeof(FolderNameEditor2), typeof(System.Drawing.Design.UITypeEditor))]
        public string SharePath { get; set; }
        [EditorAttribute(typeof(FolderNameEditor2), typeof(System.Drawing.Design.UITypeEditor))]
        public string LocalPath { get; set; }
        public BindingList<string> IgnorePaths { get; set; }
    }

    public class Config
    {
        public Settings settings { get; set; }
    }

    public class Settings
    {
        public string TrayIconText { get; set; }
        public Boolean ReportIPv4Addr { get; set; } = true;
        public Boolean ReportIPv6Addr { get; set; } = false;
        public Boolean PropagateDeletes { get; set; } = true;
        public Boolean UseNTFSTransactions { get; set; } = true;
        public Boolean ShowProgress { get; set; } = false;
        public int TimerInterval { get; set; }
        public string ConnectionString { get; set; }
        public List<FolderPair> FolderPairs { get; set; }
    }
}
