using IWshRuntimeLibrary;
using System;

namespace ArgosyUpdater.Extensions
{
    public static class XShortCut
    {
        public static bool PointsTo(string fullPathToLink, string fullPathToTargetExe)
        {
            if (!System.IO.File.Exists(fullPathToLink)) return false;
            try
            {
                var shell = new WshShell();
                var link = (IWshShortcut)shell.CreateShortcut(fullPathToLink); //loads the existing one
                return String.Equals(link.TargetPath, fullPathToTargetExe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } //corrupt / unreadable link, Create replaces it
        }


        public static void Create(string fullPathToLink, string fullPathToTargetExe, string startIn, string description)
        {
            Create(fullPathToLink, fullPathToTargetExe, startIn, description, fullPathToTargetExe);
        }

        // new link is saved under a temporary name and only then replaces the old one,
        // a failing Save (no rights, disk) leaves the existing shortcut in place instead of none
        public static void Create(string fullPathToLink, string fullPathToTargetExe, string startIn, string description, string fullPathToIcon)
        {
            string tmpLink = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fullPathToLink), "~" + System.IO.Path.GetFileName(fullPathToLink));
            try
            {
                if (System.IO.File.Exists(tmpLink)) { System.IO.File.Delete(tmpLink); }
                var shell = new WshShell();
                var link = (IWshShortcut)shell.CreateShortcut(tmpLink);
                link.IconLocation = fullPathToIcon;
                link.TargetPath = fullPathToTargetExe;
                link.Description = description;
                link.WorkingDirectory = startIn;
                link.Save();

                if (System.IO.File.Exists(fullPathToLink)) { System.IO.File.Replace(tmpLink, fullPathToLink, null); }
                else { System.IO.File.Move(tmpLink, fullPathToLink); }
            }
            finally
            {
                try { if (System.IO.File.Exists(tmpLink)) { System.IO.File.Delete(tmpLink); } } catch { }
            }
        }
    }
}