using IWshRuntimeLibrary;
using System;

namespace ArgosyUpdater.Extensions
{
    public static class XShortCut
    {
        public static bool PointsTo(string fullPathToLink, string fullPathToTargetExe)
        {
            if (!System.IO.File.Exists(fullPathToLink)) return false;
            var shell = new WshShell();
            var link = (IWshShortcut)shell.CreateShortcut(fullPathToLink); //loads the existing one
            return String.Equals(link.TargetPath, fullPathToTargetExe, StringComparison.OrdinalIgnoreCase);
        }


        public static void Create(string fullPathToLink, string fullPathToTargetExe, string startIn, string description)
        {
            if (System.IO.File.Exists(fullPathToLink)) { System.IO.File.Delete(fullPathToLink); }
            var shell = new WshShell();
            var link = (IWshShortcut)shell.CreateShortcut(fullPathToLink);
            link.IconLocation = fullPathToTargetExe;
            link.TargetPath = fullPathToTargetExe;
            link.Description = description;
            link.WorkingDirectory = startIn;
            link.Save();
        }

        public static void Create(string fullPathToLink, string fullPathToTargetExe, string startIn, string description, string fullPathToIcon)
        {
            if (System.IO.File.Exists(fullPathToLink)) { System.IO.File.Delete(fullPathToLink); }
            var shell = new WshShell();
            var link = (IWshShortcut)shell.CreateShortcut(fullPathToLink);
            link.IconLocation = fullPathToIcon;
            link.TargetPath = fullPathToTargetExe;
            link.Description = description;
            link.WorkingDirectory = startIn;
            link.Save();
        }
    }
}