using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Windows.Forms;

public class Script
{
    public string GetVersion(string locFolder)
    {
        string locFolExe = Path.Combine(locFolder, "EXEDIR");
        DirectoryInfo di = new DirectoryInfo(locFolExe);
        var dirs = di.GetDirectories().OrderByDescending(d => d.Name).ToList();
        DirectoryInfo diExedirOne = dirs.FirstOrDefault();
        string argExe = Path.Combine(diExedirOne.FullName, "Argosy.exe");
        var ass = System.Reflection.Assembly.Load(File.ReadAllBytes(argExe));
        var name = ass.GetName();
        var version = "ARGOSY " + name.Version.ToString();

        return version;
    }

    public string[] GetExeForShortcut1(string locFolder)
    {
        string[] ret = new string[3];
        ret[0] = Path.Combine(locFolder, "ArgosyBoot.bat");
        ret[1] = "LAUS ARGOSY";
        ret[2] = Path.Combine(locFolder, "_scripts\\Argosy.ico");
        return ret;
    }

    public string[] GetExeForShortcut2(string locFolder)
    {
        string[] ret = new string[3];
        ret[0] = Path.Combine(locFolder, "ArgosyBoot_HELPDESK.bat");
        ret[1] = "ARGOSY HELPDESK";
        ret[2] = Path.Combine(locFolder, "_scripts\\ArgosyHELPDESK.ico");
        return ret;
    }

    public string AfterSync(string locFolder)
    {
        Process[] pname = Process.GetProcessesByName("Argosy");
        if (pname.Length > 0)
        {
            foreach (Process proc in pname)
            {
                //kill if older than two days
                if (proc.StartTime.AddHours(48) < DateTime.Now)
                {
                    //MessageBox.Show("kill process it is older", "x", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    proc.Kill();
                    proc.WaitForExit();
                }
                else
                {
                    //MessageBox.Show("process is not older than 48 hrs", "x", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        return "";
    }

}