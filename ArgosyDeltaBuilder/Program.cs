using ArgosyUpdater.Delta;
using Octodiff.Core;
using Octodiff.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Runs on the file server (Scheduled Task, every few minutes). Idempotent: every run only does what is missing.
//  1. for every complete version in <ShareRoot>\<VersionedDir> writes a manifest (file list + SHA256)
//  2. for every two consecutive complete versions builds a delta set (octodiff + gzip), see DeltaModel.cs for layout
//  3. removes old delta sets / manifests
// Usage: ArgosyDeltaBuilder.exe [config.json] [--whatif]

namespace ArgosyDeltaBuilder
{
    public class BuilderConfig
    {
        public string ShareRoot { get; set; }
        public string DeltaDir { get; set; } = "_DELTA";
        //no list initializers here, ZeroDep.Json appends json items to an existing list
        public List<string> VersionedDirs { get; set; }
        public string VersionPrefix { get; set; } = "Argosy";
        public string ReadyMarker { get; set; } = "_READY";
        //true once the publish process writes ReadyMarker: quiet period is no longer a readiness signal,
        //an aborted upload (no marker) would otherwise be published as complete after QuietMinutes
        public bool RequireReadyMarker { get; set; } = false;
        public int QuietMinutes { get; set; } = 10;
        public List<string> ExcludeFiles { get; set; }
        public int RetentionDays { get; set; } = 90;
        public long MinPatchFileSize { get; set; } = 65536;
        public double MaxPatchRatio { get; set; } = 0.8;
        public string LogDir { get; set; } = "logs";
        public int KeepLogs { get; set; } = 30;
    }

    internal static class Program
    {
        static BuilderConfig conf;
        static bool whatIf;
        static StreamWriter log;
        static int errorCount;
        static HashSet<string> excluded;

        static int Main(string[] args)
        {
            try
            {
                whatIf = args.Any(a => a.Equals("--whatif", StringComparison.OrdinalIgnoreCase) || a.Equals("-WhatIf", StringComparison.OrdinalIgnoreCase));
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string confPath = args.FirstOrDefault(a => !a.StartsWith("-")) ?? Path.Combine(exeDir, "ArgosyDeltaBuilder.json");

                conf = DeltaIO.ReadJson<BuilderConfig>(confPath);
                if (String.IsNullOrEmpty(conf.ShareRoot) || conf.VersionedDirs == null || conf.VersionedDirs.Count == 0)
                {
                    Console.Error.WriteLine("ShareRoot / VersionedDirs are empty in " + confPath);
                    return 2;
                }

                excluded = new HashSet<string>(conf.ExcludeFiles ?? new List<string> { "Thumbs.db", "desktop.ini" }, StringComparer.OrdinalIgnoreCase);
                if (conf.RequireReadyMarker && String.IsNullOrEmpty(conf.ReadyMarker))
                {
                    Console.Error.WriteLine("RequireReadyMarker is set but ReadyMarker is empty in " + confPath);
                    return 2;
                }

                OpenLog(Path.IsPathRooted(conf.LogDir) ? conf.LogDir : Path.Combine(exeDir, conf.LogDir));
                Log("START " + (whatIf ? "(WHATIF) " : "") + "ShareRoot=" + conf.ShareRoot);

                using (var mutex = new Mutex(false, @"Global\ArgosyDeltaBuilder"))
                {
                    bool owns;
                    try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) { Log("another instance is running, exit"); return 0; }

                    try
                    {
                        foreach (string vd in conf.VersionedDirs)
                        {
                            try { ProcessVersionedDir(vd); }
                            catch (Exception ex) { Error(ex, vd); }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }

                Log("END errors=" + errorCount);
                return errorCount > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Error(ex, "FATAL");
                return 3;
            }
            finally
            {
                if (log != null) log.Dispose();
            }
        }

        static void ProcessVersionedDir(string vdName)
        {
            string vdPath = Path.Combine(conf.ShareRoot, vdName);
            if (!Directory.Exists(vdPath)) { Log(vdName + ": not found, skip"); return; }

            string root = DeltaLayout.Root(conf.ShareRoot, conf.DeltaDir, vdName);
            string manDir = Path.Combine(root, DeltaLayout.ManifestsDir);
            string delDir = Path.Combine(root, DeltaLayout.DeltasDir);
            if (!whatIf)
            {
                Directory.CreateDirectory(manDir);
                Directory.CreateDirectory(delDir);
            }

            List<string> versions = VersionNames.List(vdPath, conf.VersionPrefix);
            var ready = new List<VersionManifest>();

            foreach (string v in versions)
            {
                try
                {
                    VersionManifest m = EnsureManifest(vdPath, root, v);
                    if (m != null) ready.Add(m);
                }
                catch (Exception ex) { Error(ex, vdName + "\\" + v); }
            }

            //consecutive complete versions, a version that is still uploading is simply skipped, when it becomes complete
            //it gets its own deltas and clients pick the shortest path anyway
            for (int i = 1; i < ready.Count; i++)
            {
                VersionManifest from = ready[i - 1];
                VersionManifest to = ready[i];
                string final = DeltaLayout.DeltaSetPath(root, from.Version, to.Version);
                if (File.Exists(Path.Combine(final, DeltaLayout.DeltaSetFile))) continue;

                if (whatIf) { Log(vdName + ": WHATIF would build delta " + from.Version + " -> " + to.Version); continue; }

                try { BuildDeltaSet(vdPath, root, from, to); }
                catch (Exception ex) { Error(ex, vdName + " delta " + from.Version + " -> " + to.Version); }
            }

            try { Cleanup(root, versions); }
            catch (Exception ex) { Error(ex, vdName + " cleanup"); }
        }

        // returns null if version is not complete yet
        static VersionManifest EnsureManifest(string vdPath, string root, string v)
        {
            string vPath = Path.Combine(vdPath, v);
            string manifestPath = DeltaLayout.ManifestPath(root, v);
            string pendingPath = DeltaLayout.PendingManifestPath(root, v);

            DateTime lastChangeUtc, lastEntryUtc, lastDirWriteUtc;
            string fingerprint = ScanFingerprint(vPath, out lastChangeUtc, out lastEntryUtc, out lastDirWriteUtc);

            VersionManifest old = null;
            if (File.Exists(manifestPath))
            {
                old = DeltaIO.ReadJson<VersionManifest>(manifestPath);
                if (old.Fingerprint == fingerprint) return old;
            }

            //version folder is new or it changed after its manifest was made (re-upload): clients must not take it as target
            //until it is complete and hashed again, so its manifest is hidden (pending) meanwhile
            if (old != null)
            {
                Log(v + ": folder changed after manifest was made, hiding manifest from clients");
                if (!whatIf) HideManifest(manifestPath, pendingPath);
            }
            else if (File.Exists(pendingPath))
            {
                old = DeltaIO.ReadJson<VersionManifest>(pendingPath);
            }

            bool marker = MarkerIsLast(vPath, lastEntryUtc, lastDirWriteUtc);
            //creation time, not only last write, copy (robocopy, explorer) keeps LastWriteTime of the build
            bool quiet = !conf.RequireReadyMarker && DateTime.UtcNow - lastChangeUtc >= TimeSpan.FromMinutes(conf.QuietMinutes);
            if (!marker && !quiet)
            {
                Log(v + ": not complete yet (last change " + lastChangeUtc.ToLocalTime() + ", no " + conf.ReadyMarker
                    + " written after content, or folders changed after it less than " + conf.QuietMinutes + " min ago"
                    + (conf.RequireReadyMarker ? ", marker required" : "") + ")");
                return null;
            }

            if (whatIf) { Log(v + ": WHATIF would write manifest"); return new VersionManifest { Version = v, Fingerprint = fingerprint }; }

            //hashing opens files with FileShare.Read, a file that is still being written fails here and we try next run
            VersionManifest m = BuildManifest(vPath, v, fingerprint);

            if (old != null && !SameContent(old, m))
            {
                Log(v + ": CONTENT CHANGED, removing its delta sets (clients that already have it keep old content)");
                InvalidateDeltas(root, v);
            }

            DeltaIO.WriteJson(manifestPath, m);
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
            Log(v + ": manifest written, files=" + m.Files.Count + " size=" + DeltaIO.Mb(m.TotalSize));
            return m;
        }

        // Marker counts only if it was written after every file and directory was created: a marker copied together with
        // the folder (robocopy copies root files before subfolders) would otherwise publish a half copied version.
        // A change that only touches directories after the marker (Explorer creating the excluded Thumbs.db, a folder being
        // deleted from share) must be quiet for QuietMinutes: it neither blocks the version forever nor publishes a version
        // that is being deleted. 2 s tolerance, creating the marker itself updates LastWriteTime of the version folder.
        static bool MarkerIsLast(string vPath, DateTime lastEntryUtc, DateTime lastDirWriteUtc)
        {
            if (String.IsNullOrEmpty(conf.ReadyMarker)) return false;
            var mi = new FileInfo(Path.Combine(vPath, conf.ReadyMarker));
            if (!mi.Exists) return false;

            DateTime markerUtc = Max(mi.CreationTimeUtc, mi.LastWriteTimeUtc).AddSeconds(2);
            if (markerUtc < lastEntryUtc) return false;
            return lastDirWriteUtc <= markerUtc || DateTime.UtcNow - lastDirWriteUtc >= TimeSpan.FromMinutes(conf.QuietMinutes);
        }

        static void HideManifest(string manifestPath, string pendingPath)
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
            File.Move(manifestPath, pendingPath);
        }

        // ExcludeFiles (Thumbs.db, desktop.ini) are OS artefacts at any depth, the ready marker only counts in version root
        static bool IsExcluded(FileInfo f, string vPath)
        {
            if (excluded.Contains(f.Name)) return true;
            return !String.IsNullOrEmpty(conf.ReadyMarker)
                && String.Equals(f.Name, conf.ReadyMarker, StringComparison.OrdinalIgnoreCase)
                && String.Equals(f.DirectoryName, Path.GetFullPath(vPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // cheap change detection without hashing content: every directory, every file with size, LastWriteTime and Hidden/System.
        // a content change that keeps both size and LastWriteTime is not detected (that needs hashing everything every run)
        // lastChangeUtc   = anything (quiet period)
        // lastEntryUtc    = creation/write of files, creation of directories (what the marker must be newer than)
        // lastDirWriteUtc = LastWriteTime of directories (changes inside a dir: deletes, excluded files)
        static string ScanFingerprint(string vPath, out DateTime lastChangeUtc, out DateTime lastEntryUtc, out DateTime lastDirWriteUtc)
        {
            var di = new DirectoryInfo(vPath);
            var lines = new List<string>();
            lastEntryUtc = di.CreationTimeUtc;
            lastDirWriteUtc = di.LastWriteTimeUtc;

            foreach (var d in di.GetDirectories("*", SearchOption.AllDirectories))
            {
                lastEntryUtc = Max(lastEntryUtc, d.CreationTimeUtc);
                lastDirWriteUtc = Max(lastDirWriteUtc, d.LastWriteTimeUtc);
                lines.Add("D|" + DeltaIO.RelativePath(vPath, d.FullName).ToUpperInvariant());
            }
            foreach (var f in di.GetFiles("*", SearchOption.AllDirectories))
            {
                if (IsExcluded(f, vPath)) continue;
                lastEntryUtc = Max(lastEntryUtc, Max(f.CreationTimeUtc, f.LastWriteTimeUtc));
                lines.Add("F|" + DeltaIO.RelativePath(vPath, f.FullName).ToUpperInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks
                    + "|" + (int)(f.Attributes & (FileAttributes.Hidden | FileAttributes.System)));
            }
            lastChangeUtc = Max(lastEntryUtc, lastDirWriteUtc);

            lines.Sort(StringComparer.Ordinal);
            using (var sha = new System.Security.Cryptography.SHA256CryptoServiceProvider())
            {
                return DeltaIO.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", lines))));
            }
        }

        static VersionManifest BuildManifest(string vPath, string v, string fingerprint)
        {
            var di = new DirectoryInfo(vPath);
            var m = new VersionManifest { Version = v, Fingerprint = fingerprint };

            m.Dirs = di.GetDirectories("*", SearchOption.AllDirectories)
                .Select(d => DeltaIO.RelativePath(vPath, d.FullName))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var f in di.GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsExcluded(f, vPath)) continue;
                m.Files.Add(new ManifestFile
                {
                    Path = DeltaIO.RelativePath(vPath, f.FullName),
                    Size = f.Length,
                    Hash = DeltaIO.HashFile(f.FullName),
                    LastWriteTimeUtcTicks = f.LastWriteTimeUtc.Ticks,
                    Attributes = (int)(f.Attributes & (FileAttributes.Hidden | FileAttributes.System))
                });
                m.TotalSize += f.Length;
            }
            return m;
        }

        static bool SameContent(VersionManifest a, VersionManifest b)
        {
            if (a.Files == null || a.Files.Count != b.Files.Count) return false;
            if (a.Dirs == null || !a.Dirs.SequenceEqual(b.Dirs, StringComparer.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < a.Files.Count; i++)
            {
                if (!String.Equals(a.Files[i].Path, b.Files[i].Path, StringComparison.OrdinalIgnoreCase) || a.Files[i].Hash != b.Files[i].Hash) return false;
            }
            return true;
        }

        static void InvalidateDeltas(string root, string v)
        {
            string delDir = Path.Combine(root, DeltaLayout.DeltasDir);
            if (!Directory.Exists(delDir)) return;
            foreach (var d in new DirectoryInfo(delDir).GetDirectories())
            {
                string from, to;
                if (!DeltaLayout.TryParsePair(d.Name, out from, out to)) continue;
                if (String.Equals(from, v, StringComparison.OrdinalIgnoreCase) || String.Equals(to, v, StringComparison.OrdinalIgnoreCase))
                {
                    Log("delete delta set " + d.Name);
                    DeltaIO.DeleteDirectory(d.FullName);
                }
            }
        }

        static void BuildDeltaSet(string vdPath, string root, VersionManifest from, VersionManifest to)
        {
            DateTime start = DateTime.Now;
            string pair = DeltaLayout.PairName(from.Version, to.Version);
            string work = Path.Combine(root, DeltaLayout.DeltasDir, DeltaLayout.WorkPrefix + pair);
            string final = DeltaLayout.DeltaSetPath(root, from.Version, to.Version);
            string dataDir = Path.Combine(work, DeltaLayout.DataDir);
            string tmpDir = Path.Combine(work, "tmp");

            DeltaIO.DeleteDirectory(work);
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(tmpDir);

            string fromPath = Path.Combine(vdPath, from.Version);
            string toPath = Path.Combine(vdPath, to.Version);

            var srcByPath = from.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var srcByHash = from.Files.GroupBy(f => f.Hash).ToDictionary(g => g.Key, g => g.First());

            var set = new DeltaSet
            {
                From = from.Version,
                To = to.Version,
                CreatedUtc = DateTime.UtcNow,
                TargetSize = to.TotalSize,
                Dirs = to.Dirs
            };

            int n = 0, copies = 0, patches = 0, fulls = 0;
            foreach (ManifestFile tf in to.Files)
            {
                var e = new DeltaEntry { Path = tf.Path, Size = tf.Size, Hash = tf.Hash, LastWriteTimeUtcTicks = tf.LastWriteTimeUtcTicks };

                ManifestFile sf;
                ManifestFile moved;
                srcByPath.TryGetValue(tf.Path, out sf);

                if (sf != null && sf.Hash == tf.Hash)
                {
                    e.Op = DeltaOp.Copy; e.SourcePath = sf.Path; e.SourceHash = sf.Hash;
                    copies++;
                }
                else if (srcByHash.TryGetValue(tf.Hash, out moved))
                {
                    e.Op = DeltaOp.Copy; e.SourcePath = moved.Path; e.SourceHash = moved.Hash;
                    copies++;
                }
                else
                {
                    n++;
                    string targetFile = Path.Combine(toPath, tf.Path);
                    string fullName = n + ".gz";
                    string fullGz = Path.Combine(dataDir, fullName);
                    DeltaIO.GZipFile(targetFile, fullGz);
                    e.Op = DeltaOp.Full; e.DataFile = fullName; e.DataSize = new FileInfo(fullGz).Length;

                    if (sf != null && tf.Size >= conf.MinPatchFileSize && sf.Size >= conf.MinPatchFileSize)
                    {
                        string patchName = n + ".patch.gz";
                        string patchGz = Path.Combine(dataDir, patchName);
                        string sigFile = Path.Combine(tmpDir, n + ".sig");
                        string deltaFile = Path.Combine(tmpDir, n + ".delta");

                        BuildOctodiffDelta(Path.Combine(fromPath, sf.Path), targetFile, sigFile, deltaFile);
                        DeltaIO.GZipFile(deltaFile, patchGz);
                        long patchSize = new FileInfo(patchGz).Length;
                        File.Delete(sigFile);
                        File.Delete(deltaFile);

                        if (patchSize < e.DataSize * conf.MaxPatchRatio)
                        {
                            File.Delete(fullGz);
                            e.Op = DeltaOp.Patch; e.SourcePath = sf.Path; e.SourceHash = sf.Hash;
                            e.DataFile = patchName; e.DataSize = patchSize;
                        }
                        else
                        {
                            File.Delete(patchGz);
                        }
                    }

                    if (e.Op == DeltaOp.Patch) patches++; else fulls++;
                }

                set.DataSize += e.DataSize;
                set.Entries.Add(e);
            }

            Directory.Delete(tmpDir, true);

            //source folders must not change while we were reading them, otherwise hashes in manifest do not match data
            DateTime i1, i2, i3;
            if (ScanFingerprint(fromPath, out i1, out i2, out i3) != from.Fingerprint || ScanFingerprint(toPath, out i1, out i2, out i3) != to.Fingerprint)
            {
                DeltaIO.DeleteDirectory(work);
                throw new IOException("version folder changed while building delta, will retry next run");
            }

            DeltaIO.WriteJson(Path.Combine(work, DeltaLayout.DeltaSetFile), set);
            DeltaIO.DeleteDirectory(final); //leftover without delta.json
            Directory.Move(work, final);

            Log(String.Format("delta {0} -> {1}: copy={2} patch={3} full={4} download={5} of {6}, took {7:N0}s",
                from.Version, to.Version, copies, patches, fulls, DeltaIO.Mb(set.DataSize), DeltaIO.Mb(set.TargetSize), (DateTime.Now - start).TotalSeconds));
        }

        static void BuildOctodiffDelta(string basisFile, string newFile, string sigFile, string deltaFile)
        {
            using (var basis = new FileStream(basisFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sig = new FileStream(sigFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var sb = new SignatureBuilder { ProgressReporter = NullProgressReporter.Instance };
                sb.Build(basis, new SignatureWriter(sig));
            }

            using (var newStream = new FileStream(newFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sig = new FileStream(sigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var delta = new FileStream(deltaFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var db = new DeltaBuilder { ProgressReporter = NullProgressReporter.Instance };
                db.BuildDelta(newStream, new SignatureReader(sig, NullProgressReporter.Instance), new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(delta)));
            }
        }

        // delta sets are kept at least RetentionDays after creation and always while their From version is on the share,
        // so a PC that was offline for a while can still patch from a version that is already deleted on the share
        static void Cleanup(string root, List<string> versionsOnShare)
        {
            var onShare = new HashSet<string>(versionsOnShare, StringComparer.OrdinalIgnoreCase);
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string delDir = Path.Combine(root, DeltaLayout.DeltasDir);
            string manDir = Path.Combine(root, DeltaLayout.ManifestsDir);

            if (Directory.Exists(delDir))
            {
                foreach (var d in new DirectoryInfo(delDir).GetDirectories())
                {
                    if (d.Name.StartsWith(DeltaLayout.WorkPrefix))
                    {
                        if (d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) Delete(d.FullName, "stale work folder");
                        continue;
                    }

                    string from, to;
                    if (!DeltaLayout.TryParsePair(d.Name, out from, out to)) continue;

                    if (!onShare.Contains(from) && d.CreationTimeUtc < DateTime.UtcNow.AddDays(-conf.RetentionDays))
                    {
                        Delete(d.FullName, "expired delta set");
                        continue;
                    }
                    referenced.Add(from);
                    referenced.Add(to);
                }
            }

            if (Directory.Exists(manDir))
            {
                foreach (var f in new DirectoryInfo(manDir).GetFiles("*.json"))
                {
                    string v = Path.GetFileNameWithoutExtension(f.Name);
                    if (v.StartsWith(DeltaLayout.WorkPrefix))
                    {
                        //pending (hidden) manifest, lives while its version is on share
                        if (!onShare.Contains(v.Substring(DeltaLayout.WorkPrefix.Length))) Delete(f.FullName, "pending manifest of removed version");
                        continue;
                    }
                    if (!onShare.Contains(v) && !referenced.Contains(v)) Delete(f.FullName, "unreferenced manifest");
                }
            }
        }

        static void Delete(string path, string why)
        {
            if (whatIf) { Log("WHATIF would delete " + why + ": " + path); return; }
            Log("delete " + why + ": " + path);
            if (Directory.Exists(path)) DeltaIO.DeleteDirectory(path); else File.Delete(path);
        }

        static DateTime Max(DateTime a, DateTime b) { return a > b ? a : b; }

        static void OpenLog(string logDir)
        {
            Directory.CreateDirectory(logDir);
            string file = Path.Combine(logDir, "ArgosyDeltaBuilder_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            log = new StreamWriter(file, true, Encoding.UTF8) { AutoFlush = true };

            foreach (var old in new DirectoryInfo(logDir).GetFiles("ArgosyDeltaBuilder_*.log").OrderByDescending(f => f.Name).Skip(conf.KeepLogs))
            {
                try { old.Delete(); } catch { }
            }
        }

        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg;
            Console.WriteLine(line);
            if (log != null) log.WriteLine(line);
        }

        static void Error(Exception ex, string where)
        {
            errorCount++;
            Log("ERROR " + where + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            if (ex.InnerException != null) Log("     INNER: " + ex.InnerException.Message);
        }
    }
}
