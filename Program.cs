using ArgosyUpdater.Properties;
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
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Runtime.InteropServices.ComTypes;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

//TODO lock file ili Transactionl NTFS, tj detekcija da li smo povukli cijelu verziju !!!!!!
//    zastitia pokretanja nekompletnog EXEDira treba biti u PowerShellu, ali kako , 
//TODO uptadeDB log, keep last 5 logs !!!!!!!!!!
//https://github.com/goldfix/Transactional-NTFS-TxF-.NET/blob/master/DOCUMENTATION.md


namespace ArgosyUpdater
{
    internal static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        static string appVersion = "";

        static bool debug = false;

        static int errorsShown = 0;

        static string appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ArgosyUpdater_1_0");
        static NotifyIcon trayIcon;
        static System.Windows.Forms.Timer timer;
        static DateTime lastSync = new DateTime(1979, 5, 19); //default
        static bool thereWereErros = false;
        static bool thereWereChanges = false;
        static Config conf = null;

        static string programData;


        [STAThread]
        static void Main()
        {
            try
            {
                AttachConsole(ATTACH_PARENT_PROCESS);

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
                //CheckShortcut(); it is done in install


                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                appVersion = fvi.FileVersion;

                CheckSettings(null);

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
                    Text = conf.settings.TrayIconText,
                    Visible = true
                };


                //after checks because therer is no point before
                trayIcon.BalloonTipClicked += BalloonTip_Click;


                // Initialize timer
                timer = new System.Windows.Forms.Timer();
                timer.Interval = conf.settings.TimerInterval * 1000;
                timer.Tick += new EventHandler(CheckNetworkShare);
                timer.Start();


                //do syncv on startup, later on timer
                CheckNetworkShare(null, null);

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
                timer.Stop();
                trayIcon.Dispose();
            }

        }


        private static void CheckArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool install = false;
            bool uninstall = false;

            if (args.Length > 0)
            {
                foreach (string arg in args)
                {
                    if (arg == "debug")
                    {
                        debug = true;
                    }
                    else if (arg == "install")
                    {
                        install = true;
                    }
                    else if (arg == "uninstall")
                    {
                        uninstall = true;
                    }
                }

                if (install) { 
                    InstallApp(); 
                } else if(uninstall)
                {
                    UnInstall();
                }



            }
        }

      
        private static void InstallApp()
        {
            try
            {
                Console.WriteLine("AppPath : " + appPath);
                // no delete , it will be locked
                if (!Directory.Exists(appPath))
                {
                    Console.WriteLine("Create : " + appPath);
                    Directory.CreateDirectory(appPath);
                }

                string strExeFilePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

              
                //copy files
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



                Environment.Exit(5); //INSTALL SUCCESSFULL  dox da je 5

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


        private static void UnInstall()
        {
            try
            {
                //kill proceses
                Process[] pname = Process.GetProcessesByName("ArgosyUpdater");
                if (pname.Length > 0)
                {
                    foreach (Process proc in pname)
                    {
                        //ne pocini samoubojstvo , to je smrtni grijeh
                        if (!(proc.Id == Process.GetCurrentProcess().Id))
                        {
                            proc.Kill();
                            proc.WaitForExit();
                        }
                    }
                }

                //delete folders
                if (Directory.Exists(appPath))
                {
                    Console.WriteLine("Delete : " + appPath);
                    Directory.Delete(appPath, true);
                }

                if (Directory.Exists(programData))
                {
                    Console.WriteLine("Delete : " + programData);
                    Directory.Delete(programData, true);
                }

                //delete shortcuts
                string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ArgosyWatcher.lnk");
                File.Delete(desktopLink);

                Environment.Exit(5); //UNINSTALL SUCCESSFULL  dox da je 5

            }
            catch (Exception ex)
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
            Console.WriteLine("GrantAccess " + fullPath);
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
            Console.WriteLine("CheckShortcut strtup");
            Extensions.XShortCut.CreateShortCutInStartUpFolder("ArgosyUpdater.exe", appPath, "Argosy updater, maintains local app");

            Console.WriteLine("CheckShortcut desktop");
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ArgosyWatcher.lnk");
            string fullExe = Path.Combine(appPath, "ArgosyUpdater.exe");
            Extensions.XShortCut.Create(desktopLink, fullExe, appPath, "Argosy updater, maintains local app");
        }

        private static void CheckSettings(string jsonSett)
        {
            string jsonString;

            if (jsonSett == null)
            {
                string strExeFilePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string fileName = System.IO.Path.Combine(strExeFilePath, "AppSettings.json");
                jsonString = File.ReadAllText(fileName);
            } else
            {
                jsonString = jsonSett;
            }
            
            conf = (Config)ZeroDep.Json.Deserialize(jsonString, typeof(Config));


            if (conf.settings== null || conf.settings.FolderPairs == null )
            {
                MessageBox.Show("Setting/paths are empty !", "Setting errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(-1);
            }

            foreach (var pair in conf.settings.FolderPairs)
            {
                if ( String.IsNullOrEmpty(pair.SharePath) || String.IsNullOrEmpty(pair.LocalPath))
                {
                    MessageBox.Show("SharePath or  LocalPath is empty !", "Setting errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(-1);
                }
            }


            if (String.IsNullOrEmpty(conf.settings.TrayIconText))
            {
                MessageBox.Show("TrayIconText setting is empty !", "Setting errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(-1);
            }

            if (conf.settings.TimerInterval < 60)
            {
                MessageBox.Show("TimerInterval is less that 60 sec !", "Setting errors", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

                    //ako je config treba ga isprocesirati.
                    if (file.Name == "AppSettings.json")
                    {
                        if (File.Exists(fullFname)) { ProcessSettings(); } else
                        {
                            file.CopyTo(fullFname, true);
                        }
                    } else
                    {
                        file.CopyTo(fullFname, true);
                    }
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.UseShellExecute = false;
                psi.FileName = Path.Combine(programData, Path.GetFileName(exeName));
                psi.WorkingDirectory = programData;

                Process.Start(psi);

                Environment.Exit(0);
            }
        }

        private static void ProcessSettings() {

            var appSettProgramFiles = Path.Combine(appPath, "AppSettings.json");
            var appSettProgramData = Path.Combine(programData, "AppSettings.json");


            //preserve overidable settings (    "ShowProgress": "False",  "ShowNotifications": "True", "TimerInterval": "1200")
            //and preserve local paths in folder pairs
            Config confProgramFiles = null;
            Config confProgramData = null;

            string jsonStringProgramFiles = File.ReadAllText(appSettProgramFiles);
                confProgramFiles = (Config)ZeroDep.Json.Deserialize(jsonStringProgramFiles, typeof(Config));

            string jsonStringProgramData = File.ReadAllText(appSettProgramData);
                confProgramData = (Config)ZeroDep.Json.Deserialize(jsonStringProgramData, typeof(Config));

            //local paths
            foreach (FolderPair fp in confProgramFiles.settings.FolderPairs)
            {
                    foreach (FolderPair fpOld in confProgramData.settings.FolderPairs)
                    {
                        if (fpOld.ID == fp.ID)
                        {
                            fp.LocalPath = fpOld.LocalPath;
                            fp.Sync = fpOld.Sync;
                        }
                    }
            }

            //overidable settigns
            confProgramFiles.settings.ShowProgress = confProgramData.settings.ShowProgress;
            confProgramFiles.settings.ShowNotifications = confProgramData.settings.ShowNotifications;
            confProgramFiles.settings.TimerInterval = confProgramData.settings.TimerInterval;


            //save new config from programFiles updated with config from programData
            string jsonStringNew = ZeroDep.Json.SerializeFormatted(confProgramFiles);
            File.WriteAllText(appSettProgramData, jsonStringNew);

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
                foreach (Process p in pname)
                {
                    if (!(p.Id == Process.GetCurrentProcess().Id))
                    {
                        MessageBox.Show("Please close existing app", "ArgosyUpdater : Another instance detected", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Environment.Exit(0);
                    }
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
            //ProcessStartInfo  processStartInfo= new ProcessStartInfo();
            //processStartInfo.FileName = "notepad.exe";
            //processStartInfo.Arguments = Path.Combine(appPath, "AppSettings.json");
            //Process.Start(processStartInfo);

            string fileNameJson = Path.Combine(programData, "AppSettings.json");
            string jsonString = File.ReadAllText(fileNameJson);
            Config confForEdit = (Config)ZeroDep.Json.Deserialize(jsonString, typeof(Config));

            var form = new EditConfigForm(confForEdit.settings);
            form.ShowDialog();

            string jsonToSave = ZeroDep.Json.SerializeFormatted( confForEdit  );
            File.WriteAllText(fileNameJson, jsonToSave);

            //update config
            CheckSettings(jsonToSave);

        }

        private static void MenuOpen(object sender, EventArgs e)
        {
            string[] strArray = new string[conf.settings.FolderPairs.Count];
            int i = 0;
            foreach (FolderPair fp in conf.settings.FolderPairs) { strArray[i] = fp.LocalPath; i++; }

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
            Progress frmProgress=null;

            try
            {
                timer.Stop(); //ovo je winforms timwr koji samo dodaje triger na message loop,
                              //nije standardni timer, tj bloka inputm tj nije multithreaded,
                              //pa ipak na blocking operation u kodu ili dugo trajanje uspijeva pokrenuti nekao drugu operaciju,
                              //primjećeno ako ostane MsgBox aktivan iz CSscripta

                StringBuilderExt errors = new StringBuilderExt();
                StringBuilderExt changes = new StringBuilderExt();
                StringBuilderExt commands = new StringBuilderExt();
                StringBuilder versions = new StringBuilder();

                //show progress
                if (conf.settings.ShowProgress)
                {
                    frmProgress = new Progress(changes, errors);
                    frmProgress.Show();
                    frmProgress.Refresh();
                }


                trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdaterBusy.ico");

                //last Sync 
                //laod from file
                //if no file download all
                LoadLastSync(errors); //zbog novog nacine usporedbe maltene nepotrebno
                //spremi odmah da nebude da poslije ne pokupimo u vremenskoj ruspi sto je snimljeno
                //opet ako pokne copy teba ponistiti ovajsave AAAAAAAAAAAAAAAAAAAAA


                foreach (FolderPair fp in conf.settings.FolderPairs)
                {
                    if (!fp.Sync) continue;  //skip where Sync is false

                    //commands goes before sync, if there is RESTAR well doit , if som command needs to be done after sync we will think about that later  
                    CheckCommand(errors, commands, fp.SharePath, fp.LocalPath);

                    //this sync root folder end subfolders/files
                    DirectoryCopy(fp.SharePath, fp.LocalPath, errors, changes, fp.IgnorePaths, fp.SharePath);

                    if (conf.settings.PropagateDeletes) { DirectoryClean(fp.SharePath, fp.LocalPath, errors, changes, true); }

                }


                if (changes.Length > 0)
                {
                    thereWereChanges = true;
                    string changeFile = Path.Combine(programData, "_SyncChanges.txt");
                    File.WriteAllText(changeFile, changes.ToString());

                    foreach (FolderPair fp in conf.settings.FolderPairs)
                    {
                        DoScripts(errors, versions, fp.LocalPath, fp.SharePath);
                    }

                    if (conf.settings.ShowNotifications) trayIcon.ShowBalloonTip(3000, "New Version", "New version of APP(s) has been downloaded." + Environment.NewLine + versions, ToolTipIcon.Info);

                    if (versions.Length > 0)
                    {
                        string versionFile = Path.Combine(programData, "_Versions.txt");
                        File.WriteAllText(versionFile, versions.ToString());
                    }

                }
                else
                {
                    thereWereChanges = false;
                }


                if (errors.Length > 0)
                {
                    thereWereErros = true;
                    string errorFile = Path.Combine(programData, "_SyncErrors.txt");
                    File.WriteAllText(errorFile, errors.ToString());

                    errorsShown++;
                    if (errorsShown < 3)
                    {
                        if (conf.settings.ShowNotifications) trayIcon.ShowBalloonTip(3000, "Erros while syncing", "New version of Argosy has been tryed to download with errors, see : " + errorFile, ToolTipIcon.Warning);
                    }
                }
                else
                {
                    thereWereErros = false;
                }


                DateTime now = SaveLastSync(errors); //zbog novog nacine usporedbe maltene nepotrebno

                if (thereWereChanges || thereWereErros) { 
                    UpdateDb(errors, changes, versions, now); 
                } else
                {
                    UpdateDb(now);
                }
                


            }
            catch (Exception ex)
            {
                //neke teze greske koij enisu pohendlane pri syncu
                string errorFile = Path.Combine(programData, "_AppError.txt");
                File.WriteAllText(errorFile, ex.Message + Environment.NewLine + ex.StackTrace);
                if (conf.settings.ShowNotifications) trayIcon.ShowBalloonTip(3000, "Error syncing", ex.Message + Environment.NewLine + ex.StackTrace, ToolTipIcon.Error);
                trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdaterError.ico");
            }
            finally
            {
                Thread.Sleep(5000);

                if (frmProgress != null) frmProgress.Close();

                if (thereWereErros) {
                    trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdaterError.ico");
                } else {
                    trayIcon.Icon = new System.Drawing.Icon("ArgosyUpdater.ico");
                }

                timer.Start(); //pokreni ponovo jer smo zaustavili na pocetku
            }
        }

        private static void CheckCommand(StringBuilderExt errors, StringBuilderExt commands, string sharePath, string LocalPath)
        {
            string sharepath = "";
            string temppath = "";

            try
            {
                sharepath = Path.Combine(sharePath, "_scripts\\_aw_command.txt");
                temppath = Path.Combine(LocalPath, "_scripts\\_aw_command.txt");

                if (File.Exists(sharepath) && File.Exists(temppath)) //ako postoje obadvije fajle kopiraj ako je na shareu novija
                {
                    if (File.GetLastWriteTime(sharepath) > File.GetLastWriteTime(temppath))
                    {
                        //copy so that we are not in endelss loop, so that files have identical timestamp
                        File.Copy(sharepath, temppath , true);

                        string command = File.ReadAllText(temppath);
                        if (!string.IsNullOrEmpty(command))
                        {
                            if (command.Trim() == "RESTART")
                            {
                                commands.AppendLine("COMMAND: RESTART");
                                UpdateDb(errors, commands, new StringBuilder(), DateTime.Now);

                                Process[] pname = Process.GetProcessesByName("ArgosyUpdater");
                                if (pname.Length > 0)
                                {
                                    foreach (Process proc in pname)
                                    {
                                        //ne pocini samoubojstvo , to je smrtni grijeh
                                        if (!(proc.Id == Process.GetCurrentProcess().Id))
                                        {
                                            proc.Kill();
                                            proc.WaitForExit();
                                        }
                                    }
                                }


                                //pokreni novu instancu
                                ProcessStartInfo psi = new ProcessStartInfo();
                                psi.UseShellExecute = false;
                                psi.FileName = Path.Combine(appPath, "ArgosyUpdater.exe");
                                psi.WorkingDirectory = appPath;

                                Process.Start(psi); //onih sleep 5 sec je sad dovoljno da ovaj stigne izaći, pa i da poslije napravi running copy  

                                Environment.Exit(0);
                            }
                        }
                    }
                }
          
            }
            catch (Exception ex)
            {
                AddError(ex, errors, temppath, sharePath);
            }
        }

        private static void DoScripts(StringBuilderExt errors, StringBuilder versions, string locFolCurr, string shareCurrFol)
        {
            dynamic dynObject = null;
            try {
                string scriptPath = Path.Combine(locFolCurr, "_scripts");
                if (!Directory.Exists(scriptPath)) { return; }
                scriptPath = Path.Combine(scriptPath, "ArgCScript.cs");     //CONVENTION !!!!
                if (!File.Exists(scriptPath)) { return; }
                string script = File.ReadAllText(scriptPath);
                CSScript.CacheEnabled = false;
                System.Reflection.Assembly ass = CSScript.LoadCode(script);
                dynObject = ass.CreateObject("*");

                //version
                if (HasMethod(dynObject, "GetVersion"))
                {
                    versions.AppendLine(dynObject.GetVersion(locFolCurr));
                }

                //shortcuts
                for (int i=1; i<10; i++)
                {
                    string methodName = "GetExeForShortcut" + i.ToString();
                    if (HasMethod(dynObject, methodName))
                    {
                        //string[] shResult = dynObject.GetExeForShortcut(locFolCurr);
                        string[] shResult = dynObject.GetType().GetMethod(methodName).Invoke(dynObject, new object[] { locFolCurr });
                        string exePath = shResult[0];
                        string shName = shResult[1];
                        string iconPath = shResult[2];
                        string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shName + ".lnk");
                        string startIn = Path.GetDirectoryName(exePath);
                        Extensions.XShortCut.Create(desktopLink, exePath, startIn, shName, iconPath);
                    }
                }
                

                //after sync
                if (HasMethod(dynObject, "AfterSync")) { dynObject.AfterSync(locFolCurr); }


            } catch (Exception ex)
            {
                AddError(ex, errors, locFolCurr, shareCurrFol);
            }
            finally {
                GC.Collect(); //Argosy.exe ostaje zalockam u CS-scriptu, mada smo i tamo stavili drugi nacin loada assemblya opet ostaje zalockan ,
                              //pa smo za svaki slkucaj i ovdje gc, sada nekako nije zalockan nakon provjere verzije
            }
        }

        public static bool HasMethod(this object objectToCheck, string methodName)
        {
            var type = objectToCheck.GetType();
            return type.GetMethod(methodName) != null;
        }


        private static void UpdateDb(StringBuilderExt errors, StringBuilderExt changes, StringBuilder versions, DateTime now)
        {
            try
            {
                if (!String.IsNullOrEmpty(conf.settings.ConnectionString))
                {
                    string connectionString = conf.settings.ConnectionString;

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
           ,[UpdaterTerminalError]
           ,[ArgosyUpdaterVersion])
     VALUES
           (@MachineName
           ,@IPadress
           ,@UserName
           ,@LastSync
           ,@AppFolderVersions
           ,@LogChanges
           ,@LogErrors
           ,@UpdaterTerminalError
           ,@ArgosyUpdaterVersion)";


                    string updateStrig = @"UPDATE [dbo].[ArgosyUpdaterMachines]
   SET [IPadress] = @IPadress
      ,[UserName] = @UserName
      ,[LastSync] = @LastSync
      ,[AppFolderVersions] = @AppFolderVersions
      ,[LogChanges] = @LogChanges
      ,[LogErrors] = @LogErrors
      ,[UpdaterTerminalError] = @UpdaterTerminalError
      ,[ArgosyUpdaterVersion] = @ArgosyUpdaterVersion
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
                            foreach (var ip in machine.AddressList) { 
                                if (conf.settings.ReportIPv4Addr && ip.AddressFamily == AddressFamily.InterNetwork) ipAdrss = ipAdrss + ip.ToString() + " , ";
                                if (conf.settings.ReportIPv6Addr && ip.AddressFamily == AddressFamily.InterNetworkV6) ipAdrss = ipAdrss + ip.ToString() + " , ";
                            }
                            if (ipAdrss.Length > 3) ipAdrss = ipAdrss.Substring(0, ipAdrss.Length - 2); //remove last comma

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
                                commandU.Parameters.AddWithValue("@ArgosyUpdaterVersion", appVersion);

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
                                commandI.Parameters.AddWithValue("@ArgosyUpdaterVersion", appVersion);

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
                AddError(ex, errors, "DATABASE", "");
                string errorFile = Path.Combine(programData, "_DbError.txt");
                File.WriteAllText(errorFile, ex.Message + Environment.NewLine + ex.StackTrace);
                return;
            }
        }


        private static void UpdateDb(DateTime now)
        {
            try
            {
                if (!String.IsNullOrEmpty(conf.settings.ConnectionString))
                {
                    string connectionString = conf.settings.ConnectionString;

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

      string updateStrig = @"UPDATE [dbo].[ArgosyUpdaterMachines]
 SET [IPadress] = @IPadress
      ,[UserName] = @UserName
      ,[LastSync] = @LastSync
      ,[ArgosyUpdaterVersion] = @ArgosyUpdaterVersion
 WHERE MachineName = @MachineNameWhere";


                    IPHostEntry machine = Dns.GetHostEntry(Environment.MachineName);
                    if (machine == null) { return; }
                    if (machine.HostName == null) { return; }

                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        SqlDataReader reader = null;
                        try
                        {
                            SqlCommand command = new SqlCommand(queryString, connection);
                            command.Parameters.AddWithValue("@machineName", machine.HostName.Trim());
                            connection.Open();
                            reader = command.ExecuteReader();

                            bool machineExixts = false;
                            if (reader.HasRows) { machineExixts = true; }
                            reader.Close();

                            string ipAdrss = "";
                            foreach (var ip in machine.AddressList)
                            {
                                if (conf.settings.ReportIPv4Addr && ip.AddressFamily == AddressFamily.InterNetwork) ipAdrss = ipAdrss + ip.ToString() + " , ";
                                if (conf.settings.ReportIPv6Addr && ip.AddressFamily == AddressFamily.InterNetworkV6) ipAdrss = ipAdrss + ip.ToString() + " , ";
                            }
                            if (ipAdrss.Length > 3) ipAdrss = ipAdrss.Substring(0, ipAdrss.Length - 2); //remove last comma

                            if (machineExixts)  //update --------------------
                            {
                                SqlCommand commandU = new SqlCommand(updateStrig, connection);
                                commandU.Parameters.AddWithValue("@IPadress", ipAdrss);
                                commandU.Parameters.AddWithValue("@UserName", System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                                commandU.Parameters.AddWithValue("@LastSync", now);
                                commandU.Parameters.AddWithValue("@MachineNameWhere", machine.HostName.Trim());
                                commandU.Parameters.AddWithValue("@ArgosyUpdaterVersion", appVersion);

                                commandU.ExecuteNonQuery();

                            }
                        }
                        finally
                        {
                            connection.Close();
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                //AddError(ex, errors, "DATABASE", "");
                string errorFile = Path.Combine(programData, "_DbError.txt");
                File.WriteAllText(errorFile, ex.Message + Environment.NewLine + ex.StackTrace);
                return;
            }
        }

        private static void LoadLastSync(StringBuilderExt errors)
        {
            string lastSyncFile = Path.Combine(programData, "_lastSync.txt");

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
                AddError(ex, errors, lastSyncFile, "");
            }
        }

        private static DateTime SaveLastSync(StringBuilderExt errors)
        {
            string lastSyncFile = Path.Combine(programData, "_lastSync.txt");
            DateTime dateTime = DateTime.Now;

            try
            {
                if (File.Exists(lastSyncFile))  { File.Delete(lastSyncFile); }
                File.WriteAllText(lastSyncFile, dateTime.ToString());

            }
            catch (Exception ex)
            {
                AddError(ex, errors, lastSyncFile, ""  );
            }

            return dateTime;
        }

        private static void BalloonTip_Click(object sender, EventArgs e)
        {
            if (thereWereErros)
            {
                //show error in default TXT viewer
                string errorFile = Path.Combine(programData, "_SyncErrors.txt");
                System.Diagnostics.Process.Start(errorFile);
            }
            if (thereWereChanges)
            {
                string changeFile = Path.Combine(programData, "_SyncChanges.txt");
                System.Diagnostics.Process.Start(changeFile);
            }

            //show folder
            //ProcessStartInfo startInfo = new ProcessStartInfo {
            //    Arguments = Properties.Settings.Default.LocalPath,  FileName = "explorer.exe"
            //};
            //Process.Start(startInfo);
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, StringBuilderExt errors, StringBuilderExt changes, System.ComponentModel.BindingList<string> ignorePaths, string currSourceRootParm)
        {
            try
            {
                //check is ist on ignore list

                foreach (string path in ignorePaths)
                {
                    string dir1 = Path.Combine(currSourceRootParm, path).TrimEnd('\\');
                    string dir2 = sourceDirName.TrimEnd('\\');

                    if (dir1 == dir2) { return; }
                }

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
                        AddError(ex, errors, temppath, sourceDirName);
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
                        DirectoryCopy(subdir.FullName, temppath, errors, changes, ignorePaths , currSourceRootParm);
                    }
                    catch (Exception ex)
                    {
                        AddError(ex, errors, temppath, sourceDirName);
                        continue;
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                AddError(ex, errors, destDirName, sourceDirName);
                return;
            } 
        }

        private static void DirectoryClean(string sourceDirName, string destDirName, StringBuilderExt errors, StringBuilderExt changes, bool isRoot)
        {
            //provjeri da li je root dostupan jer inace X.Exists vraca false za sve slucajebe pa se sve pobriše, a necemo to dozvoliti ako je server nedostupan zapravo ili slicno
            //File/dir.Exists sve potrpa u isti koš
            if (isRoot)
            {
                if (!Directory.Exists(sourceDirName)) { return; }
            }

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
                    AddError(ex, errors, file.FullName, sourceDirName);
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
                        DirectoryClean(temppathDir, temppathDirDest, errors, changes, false);
                    }
                }
                catch (Exception ex)
                {
                    AddError(ex, errors, temppathDir, sourceDirName);
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

        private static void AddError(Exception ex, StringBuilderExt sb, string path, string path2)
        {
            sb.AppendLine(" PATH1: " + path + " PATH2: " +  path2 + "  MSG:" + ex.Message + Environment.NewLine + ex.StackTrace);
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
