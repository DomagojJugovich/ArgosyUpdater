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
        //folders with side by side versions (EXEDIR), synced by VersionSync (only newest version, by deltas) when server has manifests for them
        public BindingList<string> VersionedDirs { get; set; }
        public string VersionPrefix { get; set; } = "Argosy";
        //relative to SharePath, written by ArgosyDeltaBuilder, never synced itself
        public string DeltaDir { get; set; } = "_DELTA";
        //True = VersionedDirs never fall back to plain file sync (all versions), missing delta infrastructure is an error
        public Boolean DeltaSyncRequired { get; set; } = false;
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
        public Boolean ShowProgress { get; set; } = false;
        public Boolean ShowNotifications { get; set; } = true;
        public int TimerInterval { get; set; } = 1200;
        public string ConnectionString { get; set; }
        public List<FolderPair> FolderPairs { get; set; }
    }
}
