﻿using ArgosyUpdater.Properties;
using CSScriptLibrary;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;



namespace ArgosyUpdater
{
    internal static class Program
    {
        static bool debug = false;
        static string appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ArgosyUpdater_1_0");
        static NotifyIcon trayIcon;
        static System.Windows.Forms.Timer timer;
        static DateTime lastSync = new DateTime(1979, 5, 19); //default
        static bool thereWereErros = false;
        static bool thereWereChanges = false;
        static System.Collections.Specialized.StringCollection networkShare;
        static System.Collections.Specialized.StringCollection localPath;

        static string connectionString;

        static string programData;

        [STAThread]
        static void Main()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += new ThreadExceptionEventHandler(ErrorHandler);
                AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(UndandledErrorHandler);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                CheckProgramDataFolder();

                CheckArgs(); //Exits here if there is an install

                //Sleep tako da se ima vremena da se ubije proces dok novi starta, tj da odgodim provjeru postojeće instance da ne detetektira ovaj prijelaz sa "running" exeom
                Thread.Sleep(5000);

                //check is there running argosy updater allready
                CheckOtherProcesses();
                if (!debug) MakeRunningCopy(); //Exits here if this is not running copy
                CheckSettings();
                //CheckShortcut(); it is done in install

                // Initialize tray icon
                trayIcon = new NotifyIcon()
                {

                    Icon = new System.Drawing.Icon("ArgosyUpdater.ico"),
                    ContextMenu = new ContextMenu(new MenuItem[]
                    {
                    new MenuItem("CHECK NOW", new EventHandler(MenuCheckNow)),
                    new MenuItem("OPEN SYNC FOLDER", new EventHandler(MenuOpen)),
                    new MenuItem("SETTINGS", new EventHandler(MenuSettings)),
                    new MenuItem("OPEN LOG FOLDER", new EventHandler(MenuOpenLogFolder)),
                    new MenuItem("EXIT", new EventHandler(MenuExit))
                    }),
                    Text = Properties.Settings.Default.TrayIconText,
                    Visible = true
                };

                //after checks because therer is no point before
                trayIcon.BalloonTipClicked += BalloonTip_Click;

                // Initialize timer
                timer = new System.Windows.Forms.Timer();
                timer.Interval = Properties.Settings.Default.TimerInterval * 1000;
                timer.Tick += new EventHandler(CheckNetworkShare);
                timer.Start();

                //Application.Run(new Form1());
                //Run application - without form as it is just tray
                Application.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Exception: " + ex.GetType().ToString() + "MSG:" + ex.Message + Environment.NewLine + "STACK: " + ex.StackTrace, "ArgosyUpdater : Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                Environment.Exit(-1);
            }
            finally {
                MessageBox.Show("finally");
                timer.Stop();
                trayIcon.Dispose();
            }

        }

        private static void CheckArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 0)
            {
                foreach (string arg in args)
                {
                    if (arg == "debug")
                    {
                        debug= true;
                    }
                    else if (arg == "install")
                    {
                        InstallApp();
                    }
                }
            }
        }

        private static void InstallApp()
        {
            try
            {
                // no delete , it will be locked
                //if (Directory.Exists(appPath))
                //{
                //    Console.WriteLine("Delete : " + appPath);
                //    Directory.Delete(appPath, true);
                //}
                //Console.WriteLine("Create : " + appPath);
                //Directory.CreateDirectory(appPath);

                //copy files
                string strExeFilePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                DirectoryInfo di = new DirectoryInfo(strExeFilePath);
                FileInfo[] files = di.GetFiles("*.*");

                foreach (FileInfo file in files)
                {
                    string fullFname = Path.Combine(appPath, file.Name);
                    Console.WriteLine("Copy : " + fullFname);
                    file.CopyTo(fullFname, true);
                }


                //dozvoli izmjenu settingsa, ma svega na kraju i program datga radi logova
                GrantAccess(appPath);
                GrantAccess(programData);

                CheckShortcut();

                Environment.Exit(5); //INSTALL SUCCESSFULL

            } catch (Exception ex)
            {
                Console.WriteLine("Exception : ");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Inner : ");
                    Console.WriteLine(ex.InnerException.Message);
                    Console.WriteLine(ex.InnerException.StackTrace);
                }
                Environment.Exit(-1);
            }
        }

        private static void GrantAccess(string fullPath)
        {
            DirectoryInfo dInfo = new DirectoryInfo(fullPath);
            DirectorySecurity dSecurity = dInfo.GetAccessControl();
            dSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit |
                   InheritanceFlags.ContainerInherit,
                PropagationFlags.NoPropagateInherit,
                AccessControlType.Allow));

            dInfo.SetAccessControl(dSecurity);
        }

        //ARGS if install
        private static void CheckProgramDataFolder()
        {
            programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ArgosyWatcher");
            if (!Directory.Exists(programData)) { Directory.CreateDirectory(programData); }
        }

        private static void CheckShortcut()
        {
            Extensions.XShortCut.CreateShortCutInStartUpFolder("ArgosyUpdater.exe", appPath, "Argosy updater, maintains local app");

            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ArgosyWatcher.lnk");
            string fullExe = Path.Combine(appPath, "ArgosyUpdater.exe");
            Extensions.XShortCut.Create(desktopLink, fullExe, appPath, "Argosy updater, maintains local app");
        }

        private static void CheckSettings()
        {
            // Network share path
            networkShare = Properties.Settings.Default.SharePath;
            // Local download path
            localPath = Properties.Settings.Default.LocalPath;


            if (localPath==null || networkShare==null )
            {
                trayIcon.ShowBalloonTip(3000, "Setting errors", "Setting/paths are empty !", ToolTipIcon.Error);
                Environment.Exit(-1);
            }


            //check is there same number of entrys
            int cnt = networkShare.Count;
            if (localPath.Count != cnt )
            {
                trayIcon.ShowBalloonTip(3000, "Setting errors", "Not same number of entrys in string collection !", ToolTipIcon.Error);
                Environment.Exit(-1);
            }

            if (String.IsNullOrEmpty(Properties.Settings.Default.TrayIconText))
            {
                trayIcon.ShowBalloonTip(3000, "Setting errors", "TrayIconText setting is empty !", ToolTipIcon.Error);
                Environment.Exit(-1);
            }

            if (Properties.Settings.Default.TimerInterval < 30)
            {
                trayIcon.ShowBalloonTip(3000, "Setting errors", "TimerInterval is less that 30 sec !", ToolTipIcon.Error);
                Environment.Exit(-1);
            }

        }

        private static void MakeRunningCopy()
        {
            string exeName = Process.GetCurrentProcess().MainModule.FileName;
            if (exeName.Contains("Program Files"))
            {
                string strExeFilePath = Path.GetDirectoryName(exeName);
                DirectoryInfo di = new DirectoryInfo(strExeFilePath);
                FileInfo[] files = di.GetFiles("*.*");

                foreach (FileInfo file in files)
                {
                    string fullFname = Path.Combine(programData, file.Name);
                    Console.WriteLine("Copy : " + fullFname);
                    file.CopyTo(fullFname, true);
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.UseShellExecute = false;
                psi.FileName = Path.Combine(programData, Path.GetFileName(exeName));
                psi.WorkingDirectory = programData;

                Process.Start(psi);

                Environment.Exit(0);
            }
        }

        //private static void MakeRunningCopy()
        //{
        //    string exeName = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        //    if (!exeName.Contains("Running.exe"))
        //    {
        //        string runningExeName = exeName.Substring(0, exeName.Length - 4) + "Running.exe";
        //        File.Copy(exeName, runningExeName, true);

        //        //jos config
        //        string currDir = Path.GetDirectoryName(runningExeName);
        //        string configFile = Path.Combine(currDir, "ArgosyUpdater.exe.config");
        //        string runningConfigFile = Path.Combine(currDir, "ArgosyUpdaterRunning.exe.config");
        //        File.Copy(configFile, runningConfigFile, true);


        //        ProcessStartInfo psi = new ProcessStartInfo();
        //        psi.UseShellExecute = false;
        //        psi.FileName = runningExeName;
        //        psi.WorkingDirectory = currDir;

        //        Process.Start(psi);

        //        Environment.Exit(0);
        //    }
        //}


        private static void CheckOtherProcesses()
        {
            Process[] pname = Process.GetProcessesByName("ArgosyUpdater");
            if (pname.Length > 0)
            {
                if (!(pname[0].Id == Process.GetCurrentProcess().Id))
                {
                    MessageBox.Show("Please close existing app", "ArgosyUpdater : Another instance detected", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(0);
                }
            }
        }

        private static void MenuExit(object sender, EventArgs e)
        {
            Application.Exit();
            Environment.Exit(0);
        }

        private static void MenuSettings(object sender, EventArgs e)
        {
            ProcessStartInfo  processStartInfo= new ProcessStartInfo();
            processStartInfo.FileName = "notepad.exe";
            processStartInfo.Arguments = Path.Combine(appPath, "ArgosyUpdater.exe.config");
            Process.Start(processStartInfo);
        }

        private static void MenuOpen(object sender, EventArgs e)
        {
            string[] strArray = new string[localPath.Count];
            localPath.CopyTo(strArray, 0);  

            ProcessStartInfo  processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = "explorer.exe";
            var ret = SelectCombo(strArray);
            if (ret != null) { processStartInfo.Arguments = ret; }
            else { return; }
            Process.Start(processStartInfo);
        }

        private static void MenuCheckNow(object sender, EventArgs e)
        {
            CheckNetworkShare(null,null);
        }

        private static void MenuOpenLogFolder(object sender, EventArgs e)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = "explorer.exe";
            processStartInfo.Arguments = programData;
            Process.Start(processStartInfo);
        }



        private static string SelectCombo(string[] selectionData)
        {
            string ret; 

            var form = new MsgSelect(selectionData)
            {
                Name = "Select Folder :"
            };
            
            if (form.ShowDialog() == DialogResult.OK)
            {
                ret = form.selectedVal;
            }
            else
            {
                ret = null;
            }
            form.Dispose();

            return ret;
        }

        static void CheckNetworkShare(object sender, EventArgs e)
        {
            try
            {
                StringBuilder errors = new StringBuilder();
                StringBuilder changes = new StringBuilder();
                StringBuilder versions = new StringBuilder();


                trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdaterBusy.ico");

                //last Sync 
                //laod from file
                //if no file download all
                LoadLastSync(errors); //zbog novog nacine usporedbe maltene nepotrebno
                //spremi odmah da nebude da poslije ne pokupimo u vremenskoj ruspi sto je snimljeno
                //opet ako pokne copy teba ponistiti ovajsave AAAAAAAAAAAAAAAAAAAAA

                var netShEnum = networkShare.GetEnumerator();
                var locFolEnum = localPath.GetEnumerator();


                while (netShEnum.MoveNext())
                {
                    locFolEnum.MoveNext();
                    string locFolCurr = locFolEnum.Current;
                    string netShCurr = netShEnum.Current;
                    
                    //this sync root folder end subfolders/files
                    DirectoryCopy(netShCurr, locFolCurr, errors, changes);
                    DirectoryClean(netShCurr, locFolCurr, errors, changes);
                    DoScripts(errors, versions, locFolCurr);

                }


                if (errors.Length > 0)
                {
                    thereWereErros = true;
                    string errorFile = Path.Combine(programData, "SyncErrors.txt");
                    File.WriteAllText(errorFile, errors.ToString());
                    trayIcon.ShowBalloonTip(3000, "Erros while syncing", "New version of Argosy has been tryed to download with errors, see : " + errorFile, ToolTipIcon.Warning);
                }
                else
                {
                    thereWereErros = false;
                }

                if (changes.Length > 0)
                {
                    thereWereChanges = true;
                    string changeFile = Path.Combine(programData, "SyncChanges.txt");
                    File.WriteAllText(changeFile, changes.ToString());
                    trayIcon.ShowBalloonTip(3000, "New Version", "New version of APP(s) has been downloaded." + Environment.NewLine + versions, ToolTipIcon.Info);

                    if (versions.Length > 0)
                    {
                        string versionFile = Path.Combine(programData, "Versions.txt");
                        File.WriteAllText(versionFile, versions.ToString());
                    }

                }
                else
                {
                    thereWereChanges = false;
                }


                DateTime now = SaveLastSync(errors); //zbog novog nacine usporedbe maltene nepotrebno

                if (thereWereChanges || thereWereErros) { UpdateDb(errors, changes, versions, now); }
                

            }
            catch (Exception ex)
            {
                string errorFile = Path.Combine(programData, "AppError.txt");
                File.WriteAllText(errorFile, ex.Message + Environment.NewLine + ex.StackTrace);
                trayIcon.ShowBalloonTip(3000, "Error syncing", ex.Message + Environment.NewLine + ex.StackTrace, ToolTipIcon.Error);
            } finally {
                trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdater.ico");
            }
        }

        private static void DoScripts(StringBuilder errors, StringBuilder versions, string locFolCurr)
        {
            try {
                string scriptPath = Path.Combine(locFolCurr, "_scripts");
                if (!Directory.Exists(scriptPath)) { return; }
                scriptPath = Path.Combine(scriptPath, "ArgCScript.cs");     //CONVENTION !!!!
                if (!File.Exists(scriptPath)) { return; }
                string script = File.ReadAllText(scriptPath); 
                dynamic dynObject = CSScript.LoadCode(script).CreateObject("*");
                versions.AppendLine( dynObject.GetVersion(locFolCurr) );

                //shortcut
                if (HasMethod(dynObject, "GetExeForShortcut"))
                {
                    string[] shResult = dynObject.GetExeForShortcut(locFolCurr);
                    string exePath = shResult[0];
                    string shName = shResult[1];
                    string iconPath = shResult[2];
                    string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), shName + ".lnk");
                    string fullExe = Path.Combine(appPath, "ArgosyUpdater.exe");
                    string startIn = Path.GetDirectoryName(exePath);
                    Extensions.XShortCut.Create(desktopLink, exePath, startIn, shName, iconPath);
                }

                //after sync
                if (HasMethod(dynObject, "AfterSync")) { dynObject.AfterSync(locFolCurr); }


            } catch (Exception ex)
            {
                AddError(ex, errors, locFolCurr);
            }
        }

        public static bool HasMethod(this object objectToCheck, string methodName)
        {
            var type = objectToCheck.GetType();
            return type.GetMethod(methodName) != null;
        }


        private static void UpdateDb(StringBuilder errors, StringBuilder changes, StringBuilder versions, DateTime now)
        {
            try
            {
                if (!String.IsNullOrEmpty(Properties.Settings.Default.ConnectionString))
                {
                    string connectionString = Properties.Settings.Default.ConnectionString;

                    string queryString = @"SELECT [MachineName]
      ,[IPadress]
      ,[UserName]
      ,[LastSync]
      ,[AppFolderVersions]
      ,[LogChanges]
      ,[LogErrors]
      ,[UpdaterTerminalError]
  FROM [dbo].[ArgosyUpdaterMachines]
  WHERE [MachineName] = @machineName";

                    string insertStrig = @"INSERT INTO [dbo].[ArgosyUpdaterMachines]
           ([MachineName]
           ,[IPadress]
           ,[UserName]
           ,[LastSync]
           ,[AppFolderVersions]
           ,[LogChanges]
           ,[LogErrors]
           ,[UpdaterTerminalError])
     VALUES
           (@MachineName
           ,@IPadress
           ,@UserName
           ,@LastSync
           ,@AppFolderVersions
           ,@LogChanges
           ,@LogErrors
           ,@UpdaterTerminalError)";


                    string updateStrig = @"UPDATE [dbo].[ArgosyUpdaterMachines]
   SET [IPadress] = @IPadress
      ,[UserName] = @UserName
      ,[LastSync] = @LastSync
      ,[AppFolderVersions] = @AppFolderVersions
      ,[LogChanges] = @LogChanges
      ,[LogErrors] = @LogErrors
      ,[UpdaterTerminalError] = @UpdaterTerminalError
 WHERE MachineName = @MachineNameWhere";


                    IPHostEntry machine = Dns.GetHostEntry(Environment.MachineName);
                    if (machine == null) { return; }
                    if (machine.HostName == null) { return; }

                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        SqlDataReader reader=null;
                        try
                        {
                            SqlCommand command = new SqlCommand(queryString, connection);
                            command.Parameters.AddWithValue("@machineName", machine.HostName.Trim());
                            connection.Open();
                            reader = command.ExecuteReader();

                            bool machineExixts = false;
                            if (reader.HasRows)  { machineExixts = true; }
                            reader.Close();

                            string ipAdrss = "";
                            foreach (var ip in machine.AddressList) { ipAdrss = ipAdrss + ip.ToString() + " , ";  }
                            ipAdrss = ipAdrss.Substring(0, ipAdrss.Length - 2); //remove last comma

                            if (machineExixts)  //update --------------------
                            {
                                SqlCommand commandU = new SqlCommand(updateStrig, connection);
                                commandU.Parameters.AddWithValue("@IPadress", ipAdrss);
                                commandU.Parameters.AddWithValue("@UserName", System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                                commandU.Parameters.AddWithValue("@LastSync", now);
                                commandU.Parameters.AddWithValue("@AppFolderVersions", versions.ToString());
                                commandU.Parameters.AddWithValue("@LogChanges", changes.ToString());
                                commandU.Parameters.AddWithValue("@LogErrors", errors.ToString());
                                commandU.Parameters.AddWithValue("@UpdaterTerminalError", "");
                                commandU.Parameters.AddWithValue("@MachineNameWhere", machine.HostName.Trim());

                                commandU.ExecuteNonQuery();


                            }
                            else //insert ----------------------------------
                            {
                                SqlCommand commandI = new SqlCommand(insertStrig, connection);
                                commandI.Parameters.AddWithValue("@MachineName", machine.HostName.Trim());
                                commandI.Parameters.AddWithValue("@IPadress", ipAdrss);
                                commandI.Parameters.AddWithValue("@UserName", System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                                commandI.Parameters.AddWithValue("@LastSync", now);
                                commandI.Parameters.AddWithValue("@AppFolderVersions", versions.ToString());
                                commandI.Parameters.AddWithValue("@LogChanges", changes.ToString());
                                commandI.Parameters.AddWithValue("@LogErrors", errors.ToString());
                                commandI.Parameters.AddWithValue("@UpdaterTerminalError", "");

                                commandI.ExecuteNonQuery();

                            }

                            //while (reader.Read())
                            //{
                            //    if (reader.GetString( "MachineName"] Trim() == machine.HostName.Trim())
                            //}
                        }
                        finally
                        {
                            connection.Close();
                        }
                        return;
                    }
                }
            } catch (Exception ex)
            {
                string errorFile = Path.Combine(programData, "DbError.txt");
                File.WriteAllText(errorFile, ex.Message + Environment.NewLine + ex.StackTrace);
                return;
            }
        }

        private static void LoadLastSync(StringBuilder errors)
        {
            string lastSyncFile = Path.Combine(programData, "lastSync.txt");

            try
            {
                if (File.Exists(lastSyncFile))
                {
                    string[] lines = System.IO.File.ReadAllLines(lastSyncFile);
                    bool parsed = DateTime.TryParse(lines[0], out lastSync);

                    if (!parsed)
                    {  //if not parsed it is myabe corupted so delete it, and leave default, it wil lcopy everithng again just in case
                        File.Delete(lastSyncFile);
                    }
                }
            }
            catch (Exception ex)
            {
                AddError(ex, errors, lastSyncFile);
            }
        }

        private static DateTime SaveLastSync(StringBuilder errors)
        {
            string lastSyncFile = Path.Combine(programData, "lastSync.txt");
            DateTime dateTime = DateTime.Now;

            try
            {
                if (File.Exists(lastSyncFile))  { File.Delete(lastSyncFile); }
                File.WriteAllText(lastSyncFile, dateTime.ToString());

            }
            catch (Exception ex)
            {
                AddError(ex, errors, lastSyncFile);
            }

            return dateTime;
        }

        private static void BalloonTip_Click(object sender, EventArgs e)
        {
            if (thereWereErros)
            {
                //show error in default TXT viewer
                string errorFile = Path.Combine(programData, "SyncErrors.txt");
                System.Diagnostics.Process.Start(errorFile);
            }
            if (thereWereChanges)
            {
                string changeFile = Path.Combine(programData, "SyncChanges.txt");
                System.Diagnostics.Process.Start(changeFile);
            }

            //show folder
            //ProcessStartInfo startInfo = new ProcessStartInfo {
            //    Arguments = Properties.Settings.Default.LocalPath,  FileName = "explorer.exe"
            //};
            //Process.Start(startInfo);
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, StringBuilder errors, StringBuilder changes)
        {
            try
            {
                // Get the subdirectories for the specified directory.
                DirectoryInfo dir = new DirectoryInfo(sourceDirName);

                //If the destination directory doesn't exist, create it.
                if (!Directory.Exists(destDirName))
                {
               
                        changes.AppendLine("CREATE DIR : " + destDirName);
                        Directory.CreateDirectory(destDirName);
               
                }

                //Get the files in the directory and copy them to the new location.
                FileInfo[] files = dir.GetFiles();
                foreach (FileInfo file in files)
                {
                    string temppath="";
                    try
                    {
                        temppath = Path.Combine(destDirName, file.Name);

                        if (File.Exists(temppath)) //ako postoje obadvije fajle kopiraj ako je na shareu novija
                        {
                            if (file.LastWriteTime > File.GetLastWriteTime(temppath))
                            {
                                changes.AppendLine("COPY NEWER FILE : " + temppath);
                                file.CopyTo(temppath, true);
                                //CopyFile(file.FullName, temppath, true); //ovo bi mozda bolje hendlalo lockove ali ovo iznad radi ok tako da je ok
                            }
                        }
                        else //ima samo na shareu dakle kopiraj bez provjere
                        {
                            changes.AppendLine("COPY MISSING FILE : " + temppath);
                            file.CopyTo(temppath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddError(ex, errors, temppath);
                        continue;
                    }
                }

                //copy subdirectories also , copy them and their contents to new location.
                DirectoryInfo[] dirs = dir.GetDirectories();
                foreach (DirectoryInfo subdir in dirs)
                {
                    string temppath="";
                    try { 
                        temppath = Path.Combine(destDirName, subdir.Name);
                        DirectoryCopy(subdir.FullName, temppath, errors, changes);
                    }
                    catch (Exception ex)
                    {
                        AddError(ex, errors, temppath);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                AddError(ex, errors, destDirName);
                return;
            }
        }

        private static void DirectoryClean(string sourceDirName, string destDirName, StringBuilder errors, StringBuilder changes)
        {
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(destDirName);

            //Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string temppath = "";
                try
                {
                    temppath = Path.Combine(sourceDirName, file.Name);

                    if (!File.Exists(temppath)) //ako ne postoji na sourceu obrisi ju i lokalno
                    {
                         changes.AppendLine("DELETE MISSING FILE : " + file.FullName);
                         file.Delete();
                    }
                }
                catch (Exception ex)
                {
                    AddError(ex, errors, file.FullName);
                    continue;
                }
            }

            //delete subdirectories also if missing
            DirectoryInfo[] dirs = dir.GetDirectories();

            string temppathDir = "";

            foreach (DirectoryInfo subdir in dirs)
            {
                try
                {
                    temppathDir = Path.Combine(sourceDirName, subdir.Name);

                    if (!Directory.Exists(temppathDir)) //ako ne postoji na sourceu obrisi ju i lokalno
                    {
                        changes.AppendLine("DELETE MISSING FOLDER : " + subdir.FullName);
                        subdir.Delete(true);
                    } else
                    {
                        string temppathDirDest = Path.Combine(destDirName, subdir.Name);
                        DirectoryClean(temppathDir, temppathDirDest, errors, changes);
                    }
                }
                catch (Exception ex)
                {
                    AddError(ex, errors, temppathDir);
                    continue;
                }
            }
        }

        private static void CopyFile(string path1, string path2, bool v)
        {
            using (var inputFile = new FileStream(
                path1,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
                {
                    using (var outputFile = new FileStream(path2, FileMode.Create))
                    {
                        var buffer = new byte[0x100000];
                        int bytes;
    
                        while ((bytes = inputFile.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outputFile.Write(buffer, 0, bytes);
                        }
                        }
                }
        }

        private static void AddError(Exception ex, StringBuilder sb, string path)
        {
            sb.AppendLine(DateTime.Now.ToString() + " - PATH: " + path + "  MSG:" + ex.Message + Environment.NewLine + ex.StackTrace);
            if (ex.InnerException != null)
            {
                sb.AppendLine("     INNER: " + ex.InnerException.Message + Environment.NewLine + ex.InnerException.StackTrace);
            }
        }

        static void UndandledErrorHandler(object sender, UnhandledExceptionEventArgs args)
        {
            MessageBox.Show("Exception: " + ((Exception)args.ExceptionObject).GetType().ToString() + Environment.NewLine + "MSG:" + ((Exception)args.ExceptionObject).Message + Environment.NewLine + "STACK: " + ((Exception)args.ExceptionObject).StackTrace, "ArgosyUpdater : Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            //Application.Exit();
            //Environment.Exit(-1);
        }

        static void ErrorHandler(object sender, ThreadExceptionEventArgs args)
        {
            MessageBox.Show("Exception: " + ((Exception)args.Exception).GetType().ToString() + Environment.NewLine +  "MSG:" + ((Exception)args.Exception).Message + Environment.NewLine + "STACK: " + ((Exception)args.Exception).StackTrace, "ArgosyUpdater : Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            //Application.Exit();
            //Environment.Exit(-1);
        }
    }
}