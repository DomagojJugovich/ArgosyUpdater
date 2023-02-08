using System.IO;
using System.Linq;

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
				ret[1] = "ARGOSY PROD";
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
	   return "JEEEEEEEE";
    }
	
}