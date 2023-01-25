using System.IO;
using System.Linq;

public class Script
{
	public static void Main() {
		var s = new Script();
		string ver = s.GetVersion("D:\\_ARGOSY");
		string[] sh = s.GetExeForShortcut("D:\\_ARGOSY");
		string sss = s.AfterSync("D:\\_ARGOSY");
		
	}
	
    public string GetVersion(string locFolder)
    {
	   string locFolExe = Path.Combine(locFolder, "EXEDIR");
                DirectoryInfo di = new DirectoryInfo(locFolExe);
                var dirs = di.GetDirectories().OrderByDescending(d => d.Name).ToList();
                DirectoryInfo diExedirOne = dirs.FirstOrDefault();
                string argExe = Path.Combine(diExedirOne.FullName, "Argosy.exe");
                var ass = System.Reflection.Assembly.LoadFrom(argExe);
                var name = ass.GetName();
                var version = name.Version.ToString();
                return version;
    }
	
	public string[] GetExeForShortcut(string locFolder)
    {
	   string[] ret = new string[3];
                ret[0] = Path.Combine(locFolder, "ArgosyBoot.bat");
				ret[1] = "ARGOSY_PROD";
				ret[2] = Path.Combine(locFolder, "_scripts\\Argosy.ico");
                return ret;
    }
	
	public string AfterSync(string locFolder)
    {
	   System.Windows.Forms.MessageBox.Show("AfterSync JEEEEEEEEEEEE","AfterSync title");
	   return "JEEEEEEEE";
    }
	
	
}