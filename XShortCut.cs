using IWshRuntimeLibrary;
using System;

namespace ArgosyUpdater.Extensions
{
    public static class XShortCut
    {
        public static string CreateShortCutInStartUpFolder(string exeName, string startIn, string description)
        {
            var startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var linkPath = startupFolderPath + @"\" + exeName + "-Shortcut.lnk";
            var targetPath = startIn + @"\" + exeName;
            Create(linkPath, targetPath, startIn, description);
            return startupFolderPath;
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