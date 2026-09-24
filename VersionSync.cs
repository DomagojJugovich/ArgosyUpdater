using ArgosyUpdater.Delta;
using Octodiff.Core;
using Octodiff.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// Sync of versioned folder (EXEDIR) : only the newest complete version from share is brought to the PC.
//  - local version + chain of delta sets from server (ArgosyDeltaBuilder) -> new version, intermediate versions are never materialized
//  - no usable chain (or plain copy is cheaper) -> unchanged files are taken from newest local version (by hash),
//    the rest gzipped from the delta set into target, or plain from share
//  - everything is built in EXEDIR\~s_<version>, verified against manifest (SHA256) and only then renamed to EXEDIR\<version>,
//    ArgosyBoot.ps1 starts only folders named <VersionPrefix>*, so it never starts a half built version
// Old local versions are left alone (user may be working in one), DirectoryClean removes them when they are gone from share.
// Runs on a worker thread (Program.RunPumping), must not touch UI.

namespace ArgosyUpdater
{
    internal static class VersionSync
    {
        //short names, version folders already have deep paths (_CONFIGS\_ARHIVA\...) close to MAX_PATH
        public const string StagingPrefix = DeltaLayout.WorkPrefix + "s_";
        const string OldPrefix = DeltaLayout.WorkPrefix + "o_";
        const string HiddenPrefix = DeltaLayout.WorkPrefix + "p_";
        const string WorkDirName = DeltaLayout.WorkPrefix + "w";

        // chain smaller than this part of the version is taken without comparing it to plain copy
        const int ObviouslyCheapDivisor = 20;

#if DEBUG
        // manual tray test only: slows down building a version so the tray menu can be tried while sync runs
        static readonly int TestDelayMs = Int32.TryParse(Environment.GetEnvironmentVariable("ARGOSYUPDATER_TEST_DELAY_MS"), out int ms) ? ms : 0;
#endif

        // local file with content of a target file. Hash is set when the content is known to be that hash
        // (hashed while copying/decompressing, octodiff SHA1 check, FindIdenticalFiles), null = must be hashed
        class Source
        {
            public readonly string File;
            public readonly string Hash;
            // local version files: size + LastWriteTime when Hash was computed, the hash is trusted only if they did not change
            public readonly long Size;
            public readonly long WriteTicks;
            public Source(string file, string hash) { File = file; Hash = hash; }
            public Source(string file, string hash, long size, long writeTicks) : this(file, hash) { Size = size; WriteTicks = writeTicks; }
        }

        // gzipped full file from a delta set into target
        class FullData
        {
            public string GzFile;
            public string Hash;
            public long DataSize;
        }

        // versioned dirs of this folder pair handled here, those are excluded from plain DirectoryCopy.
        // DeltaSyncRequired: all of them, missing delta infrastructure is reported by Sync, never falls back to copying all versions.
        // otherwise only those with manifests on share, others (server tool not deployed yet) stay on legacy sync.
        // Directory.Exists is false also on access/network errors, that is why fallback should be off once the server tool runs.
        public static List<string> GetManagedDirs(FolderPair fp)
        {
            var ret = new List<string>();
            if (fp.VersionedDirs == null || String.IsNullOrEmpty(fp.DeltaDir)) return ret;

            foreach (string vd in fp.VersionedDirs)
            {
                if (String.IsNullOrEmpty(vd)) continue;
                if (fp.DeltaSyncRequired || Directory.Exists(ManifestsDir(fp, vd))) ret.Add(vd);
            }
            return ret;
        }

        static string ManifestsDir(FolderPair fp, string vdName)
        {
            return Path.Combine(DeltaLayout.Root(fp.SharePath, fp.DeltaDir, vdName), DeltaLayout.ManifestsDir);
        }

        public static void Sync(FolderPair fp, string vdName, StringBuilderExt errors, StringBuilderExt changes)
        {
            string shareVd = Path.Combine(fp.SharePath, vdName);
            string localVd = Path.Combine(fp.LocalPath, vdName);
            string deltaRoot = DeltaLayout.Root(fp.SharePath, fp.DeltaDir, vdName);
            string prefix = String.IsNullOrEmpty(fp.VersionPrefix) ? "Argosy" : fp.VersionPrefix;

            string staging = null;
            string old = null;
            string localTarget = null;
            string work = Path.Combine(localVd, WorkDirName);

            try
            {
                if (!Directory.Exists(shareVd)) return;

                //one listing instead of File.Exists per version: File.Exists is false also on network/access errors
                //and we delete local folders based on this
                HashSet<string> manifests = ListManifests(deltaRoot, fp.DeltaSyncRequired);

                //newest version on share that has a manifest, server writes manifest only for complete versions
                string target = VersionNames.List(shareVd, prefix).LastOrDefault(v => manifests.Contains(v + ".json"));

                Directory.CreateDirectory(localVd);
                RemoveLeftovers(localVd, errors);
                //PC already has a newer complete version (e.g. target's successor is being re-uploaded), building older one is pointless
                if (HideUnverifiedNewer(localVd, shareVd, prefix, target, deltaRoot, manifests, errors, changes)) return;
                if (target == null) return;

                VersionManifest manifest = DeltaIO.ReadJson<VersionManifest>(DeltaLayout.ManifestPath(deltaRoot, target));
                localTarget = Path.Combine(localVd, target);
                string shareTarget = Path.Combine(shareVd, target);

                if (Directory.Exists(localTarget))
                {
                    if (QuickCheck(localTarget, manifest)) return;

                    //incomplete version out of the way first: if someone runs it we give up now, not after rebuilding it
                    string aside = Path.Combine(localVd, OldPrefix + target);
                    try { Directory.Move(localTarget, aside); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new IOException("Incomplete version is in use, will rebuild when it is closed : " + localTarget, ex);
                    }
                    old = aside;
                    changes.AppendLine("VERSION INCOMPLETE, REBUILD : " + localTarget);
                }

                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(localVd))).AvailableFreeSpace;
                if (free < manifest.TotalSize * 2)
                {
                    throw new IOException("Not enough free space for " + target + " : free " + DeltaIO.Mb(free) + ", need " + DeltaIO.Mb(manifest.TotalSize * 2));
                }

                List<string> localVersions = VersionNames.List(localVd, prefix)
                    .Where(v => VersionNames.Compare(v, target) < 0)
                    .ToList();
                string newest = localVersions.LastOrDefault();

                staging = Path.Combine(localVd, StagingPrefix + target);
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(work);

                //delta.json parsing is the main fixed cost, every set is read once
                var sets = new Dictionary<string, DeltaSet>(StringComparer.OrdinalIgnoreCase);

                string baseVersion;
                List<DeltaSet> chain = FindChain(deltaRoot, localVersions, target, sets, errors, out baseVersion);
                Dictionary<string, FullData> fullData = FindFullData(deltaRoot, target, sets, errors);

                //relative path -> local file that already has the right content for target
                Dictionary<string, Source> content = null;
                Dictionary<string, Source> identical = null;

                if (chain != null)
                {
                    long chainBytes = chain.Sum(s => s.DataSize);
                    if (chainBytes > manifest.TotalSize / ObviouslyCheapDivisor && newest != null)
                    {
                        //many steps can together be more than what plain copy downloads (it reuses unchanged files)
                        identical = FindIdenticalFiles(Path.Combine(localVd, newest), manifest);
                        long copyBytes = CopyDownloadSize(manifest, identical, fullData);
                        if (copyBytes <= chainBytes)
                        {
                            changes.AppendLine("COPY CHEAPER THAN " + chain.Count + " DELTA STEP(S) : " + DeltaIO.Mb(copyBytes) + " < " + DeltaIO.Mb(chainBytes));
                            chain = null;
                        }
                    }
                }

                if (chain != null)
                {
                    try
                    {
                        content = ApplyChain(Path.Combine(localVd, baseVersion), chain, deltaRoot, work, changes);
                        changes.AppendLine("PATCH VERSION : " + baseVersion + " -> " + target + " in " + chain.Count + " step(s), downloaded " + DeltaIO.Mb(chain.Sum(s => s.DataSize)));
                    }
                    catch (Exception ex)
                    {
                        Program.AddError(ex, errors, "PATCH " + baseVersion + " -> " + target + ", falling back to copy", deltaRoot);
                        content = null;
                    }
                }

                if (content == null)
                {
                    content = identical ?? (newest == null ? new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase) : FindIdenticalFiles(Path.Combine(localVd, newest), manifest));
                    changes.AppendLine("COPY VERSION : " + target + (newest == null ? "" : ", " + content.Count + " unchanged files reused from " + newest));
                }

                Dictionary<string, string> hashes = Materialize(manifest, content, fullData, shareTarget, staging, work, changes);
                Verify(manifest, hashes, shareTarget, staging, changes);

                DeleteDir(work);

                MoveWithRetry(staging, localTarget);
                staging = null;
                if (old != null)
                {
                    try { DeleteDir(old); } catch { } //RemoveLeftovers next time
                    old = null;
                }

                changes.AppendLine("NEW VERSION READY : " + localTarget);
            }
            catch (Exception ex)
            {
                Program.AddError(ex, errors, localVd, shareVd);
            }
            finally
            {
                try { DeleteDir(work); } catch { }
                if (staging != null) { try { DeleteDir(staging); } catch { } }
                //failed after incomplete version was moved aside, put it back, better incomplete in place than none
                if (old != null && !Directory.Exists(localTarget)) { try { Directory.Move(old, localTarget); } catch { } }
            }
        }

        static HashSet<string> ListManifests(string deltaRoot, bool required)
        {
            string manDir = Path.Combine(deltaRoot, DeltaLayout.ManifestsDir);
            try
            {
                return new HashSet<string>(Directory.GetFiles(manDir, "*.json").Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException((required ? "Delta sync required but manifests are not reachable" : "Delta manifests are not reachable")
                    + " (ArgosyDeltaBuilder not running or no access) : " + manDir, ex);
            }
        }

        static void RemoveLeftovers(string localVd, StringBuilderExt errors)
        {
            //from previous run that crashed/was killed, if staging/work of this run can not be removed we fail later on create/copy
            foreach (var d in new DirectoryInfo(localVd).GetDirectories(DeltaLayout.WorkPrefix + "*"))
            {
                try { DeleteDir(d.FullName); }
                catch (Exception ex) { Program.AddError(ex, errors, d.FullName, "leftover"); }
            }
        }

        // Local version newer than target (or any, when there is no target yet) that also exists on share without a normal manifest:
        // share version is still uploading, being re-uploaded (pending manifest) or not hashed by server yet.
        // Local copy of it made by legacy file sync may be partial and ArgosyBoot.ps1 would start it (highest Argosy* name).
        //  - pending manifest: local must match it (it is the last complete content)
        //  - no manifest: local must match file list + sizes on share (server just did not get to it yet)
        // otherwise it is renamed to ~p_ and RemoveLeftovers deletes it next run, if it is in use rename fails and we try again.
        // Versions that are not on share are left to DirectoryClean / PropagateDeletes, nothing to compare them with.
        // Anything we can not read is left alone. Returns true if a complete newer version stays in place.
        static bool HideUnverifiedNewer(string localVd, string shareVd, string prefix, string target, string deltaRoot, HashSet<string> manifests, StringBuilderExt errors, StringBuilderExt changes)
        {
            bool keptNewer = false;
            var onShare = new HashSet<string>(VersionNames.List(shareVd, prefix), StringComparer.OrdinalIgnoreCase);

            foreach (string v in VersionNames.List(localVd, prefix).Where(x => (target == null || VersionNames.Compare(x, target) > 0) && onShare.Contains(x)))
            {
                string localV = Path.Combine(localVd, v);
                bool complete;
                try
                {
                    complete = manifests.Contains(DeltaLayout.WorkPrefix + v + ".json")
                        ? QuickCheck(localV, DeltaIO.ReadJson<VersionManifest>(DeltaLayout.PendingManifestPath(deltaRoot, v)))
                        : SameAsShare(localV, Path.Combine(shareVd, v));
                }
                catch (Exception ex)
                {
                    Program.AddError(ex, errors, "CAN NOT CHECK NEWER VERSION, LEFT AS IS : " + localV, deltaRoot);
                    keptNewer = true;
                    continue;
                }

                if (complete)
                {
                    keptNewer = true;
                    continue;
                }

                try
                {
                    Directory.Move(localV, Path.Combine(localVd, HiddenPrefix + v));
                    changes.AppendLine("UNVERIFIED NEWER VERSION REMOVED : " + localV);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Program.AddError(ex, errors, "UNVERIFIED NEWER VERSION IN USE, WILL RETRY : " + localV, deltaRoot);
                }
            }
            return keptNewer;
        }

        // same relative paths and sizes both ways
        static bool SameAsShare(string localV, string shareV)
        {
            var share = new DirectoryInfo(shareV).GetFiles("*", SearchOption.AllDirectories)
                .ToDictionary(f => DeltaIO.RelativePath(shareV, f.FullName), f => f.Length, StringComparer.OrdinalIgnoreCase);
            var local = new DirectoryInfo(localV).GetFiles("*", SearchOption.AllDirectories);
            if (local.Length != share.Count) return false;

            foreach (var f in local)
            {
                long len;
                if (!share.TryGetValue(DeltaIO.RelativePath(localV, f.FullName), out len) || len != f.Length) return false;
            }
            return true;
        }

        // dirs + file sizes, no hashing, local version is trusted once it is renamed in place
        static bool QuickCheck(string localTarget, VersionManifest manifest)
        {
            foreach (string d in manifest.Dirs)
            {
                if (!Directory.Exists(Path.Combine(localTarget, d))) return false;
            }
            foreach (ManifestFile mf in manifest.Files)
            {
                var fi = new FileInfo(Path.Combine(localTarget, mf.Path));
                if (!fi.Exists || fi.Length != mf.Size) return false;
            }
            return true;
        }

        // Full entries of delta sets that end in target: gzipped copies of changed files, used instead of plain files from share
        static DeltaSet LoadSet(string deltaRoot, string from, string to, Dictionary<string, DeltaSet> sets)
        {
            string pair = DeltaLayout.PairName(from, to);
            DeltaSet set;
            if (!sets.TryGetValue(pair, out set))
            {
                set = DeltaIO.ReadJson<DeltaSet>(Path.Combine(DeltaLayout.DeltaSetPath(deltaRoot, from, to), DeltaLayout.DeltaSetFile));
                sets[pair] = set;
            }
            return set;
        }

        static Dictionary<string, FullData> FindFullData(string deltaRoot, string target, Dictionary<string, DeltaSet> sets, StringBuilderExt errors)
        {
            var ret = new Dictionary<string, FullData>(StringComparer.OrdinalIgnoreCase);
            string delDir = Path.Combine(deltaRoot, DeltaLayout.DeltasDir);
            if (!Directory.Exists(delDir)) return ret;

            foreach (var d in new DirectoryInfo(delDir).GetDirectories())
            {
                string from, to;
                if (!DeltaLayout.TryParsePair(d.Name, out from, out to) || !String.Equals(to, target, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    DeltaSet set = LoadSet(deltaRoot, from, to, sets);
                    foreach (DeltaEntry e in set.Entries.Where(x => x.Op == DeltaOp.Full))
                    {
                        ret[e.Path] = new FullData { GzFile = Path.Combine(d.FullName, DeltaLayout.DataDir, e.DataFile), Hash = e.Hash, DataSize = e.DataSize };
                    }
                }
                catch (Exception ex) { Program.AddError(ex, errors, d.FullName, "delta set, plain files from share are used instead"); }
            }
            return ret;
        }

        // bytes plain copy would download: everything not found locally, gzipped where delta data has it
        static long CopyDownloadSize(VersionManifest manifest, Dictionary<string, Source> identical, Dictionary<string, FullData> fullData)
        {
            long bytes = 0;
            foreach (ManifestFile mf in manifest.Files)
            {
                if (identical.ContainsKey(mf.Path)) continue;
                FullData fd;
                bytes += fullData.TryGetValue(mf.Path, out fd) && fd.Hash == mf.Hash ? fd.DataSize : mf.Size;
            }
            return bytes;
        }

        // local version from which target is reached with least download, path = fewest steps in delta graph
        static List<DeltaSet> FindChain(string deltaRoot, List<string> localVersions, string target, Dictionary<string, DeltaSet> sets, StringBuilderExt errors, out string baseVersion)
        {
            baseVersion = null;
            string delDir = Path.Combine(deltaRoot, DeltaLayout.DeltasDir);
            if (localVersions.Count == 0 || !Directory.Exists(delDir)) return null;

            var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in new DirectoryInfo(delDir).GetDirectories())
            {
                string from, to;
                if (!DeltaLayout.TryParsePair(d.Name, out from, out to)) continue;
                if (!File.Exists(Path.Combine(d.FullName, DeltaLayout.DeltaSetFile))) continue;
                if (VersionNames.Compare(to, target) > 0) continue; //never go past target
                if (!edges.ContainsKey(from)) edges[from] = new List<string>();
                edges[from].Add(to);
            }

            List<DeltaSet> best = null;
            long bestBytes = long.MaxValue;

            for (int i = localVersions.Count - 1; i >= 0; i--)
            {
                List<string> path = ShortestPath(edges, localVersions[i], target);
                if (path == null) continue;

                var chain = new List<DeltaSet>();
                try
                {
                    for (int s = 1; s < path.Count; s++)
                    {
                        chain.Add(LoadSet(deltaRoot, path[s - 1], path[s], sets));
                    }
                }
                catch (Exception ex)
                {
                    //set being removed by the server right now or unreadable: try other bases, or plain copy
                    Program.AddError(ex, errors, "DELTA SET NOT USABLE, BASE " + localVersions[i] + " SKIPPED", deltaRoot);
                    continue;
                }

                long bytes = chain.Sum(c => c.DataSize);
                if (bytes < bestBytes)
                {
                    best = chain;
                    bestBytes = bytes;
                    baseVersion = localVersions[i];
                }
            }
            return best;
        }

        static List<string> ShortestPath(Dictionary<string, List<string>> edges, string start, string target)
        {
            var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { start, null } };
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                string v = queue.Dequeue();
                if (String.Equals(v, target, StringComparison.OrdinalIgnoreCase))
                {
                    var path = new List<string>();
                    for (string p = v; p != null; p = prev[p]) path.Insert(0, p);
                    return path;
                }

                List<string> next;
                if (!edges.TryGetValue(v, out next)) continue;
                foreach (string n in next.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (prev.ContainsKey(n)) continue;
                    prev[n] = v;
                    queue.Enqueue(n);
                }
            }
            return null;
        }

        // Applies deltas one after another without building intermediate versions: we only keep a map
        // relative path -> local file with current content (file in base version or patched file in work dir).
        // A file that can not be patched (local base differs from server) is dropped from the map and
        // downloaded in Materialize, so one bad file does not throw away the whole chain.
        static Dictionary<string, Source> ApplyChain(string basePath, List<DeltaSet> chain, string deltaRoot, string work, StringBuilderExt changes)
        {
            var current = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in new DirectoryInfo(basePath).GetFiles("*", SearchOption.AllDirectories))
            {
                current[DeltaIO.RelativePath(basePath, f.FullName)] = new Source(f.FullName, null);
            }

            int step = 0;
            foreach (DeltaSet set in chain)
            {
                step++;
                string stepDir = Path.Combine(work, "s" + step);
                Directory.CreateDirectory(stepDir);
                string setDir = DeltaLayout.DeltaSetPath(deltaRoot, set.From, set.To);

                var next = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
                int n = 0;

                foreach (DeltaEntry e in set.Entries)
                {
                    n++;
                    Source src = null;
                    if (e.SourcePath != null) current.TryGetValue(e.SourcePath, out src);

                    if (e.Op == DeltaOp.Copy)
                    {
                        //content known to be something else than server expects -> leave it for download
                        if (src != null && (src.Hash == null || src.Hash == e.Hash)) next[e.Path] = src;
                        continue;
                    }

                    if (e.Op == DeltaOp.Patch && src == null) continue; //basis unknown, Materialize downloads it

                    string outFile = Path.Combine(stepDir, n + ".bin");
                    string dlFile = Path.Combine(stepDir, n + ".dl");
                    try
                    {
                        if (e.Op == DeltaOp.Full)
                        {
                            string hash = DeltaIO.GUnzipFile(Path.Combine(setDir, DeltaLayout.DataDir, e.DataFile), outFile);
                            if (hash != e.Hash) throw new InvalidDataException("delta data does not match its hash");
                            next[e.Path] = new Source(outFile, hash);
                        }
                        else
                        {
                            DeltaIO.GUnzipFile(Path.Combine(setDir, DeltaLayout.DataDir, e.DataFile), dlFile);
                            //octodiff checks SHA1 of the result against the new file the delta was made from (= e.Hash)
                            ApplyOctodiff(src.File, dlFile, outFile);
                            File.Delete(dlFile);
                            next[e.Path] = new Source(outFile, e.Hash);
                        }
                    }
                    catch (Exception ex)
                    {
                        changes.AppendLine("PATCH FAILED, WILL COPY : " + e.Path + " (" + set.To + ") " + ex.Message);
                        if (File.Exists(dlFile)) File.Delete(dlFile);
                        if (File.Exists(outFile)) File.Delete(outFile);
                    }
                }

                //files of previous steps that are not needed any more
                var used = new HashSet<string>(next.Values.Select(s => s.File), StringComparer.OrdinalIgnoreCase);
                foreach (string f in current.Values.Select(s => s.File).Where(v => v.StartsWith(work, StringComparison.OrdinalIgnoreCase) && !used.Contains(v)).Distinct())
                {
                    File.Delete(f);
                }

                current = next;
                changes.AppendLine("APPLY DELTA : " + set.From + " -> " + set.To + " (" + DeltaIO.Mb(set.DataSize) + ")");
            }

            return current;
        }

        static void ApplyOctodiff(string basisFile, string deltaFile, string outFile)
        {
            using (var basis = new FileStream(basisFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var delta = new FileStream(deltaFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                //hash check reads output back and compares with SHA1 of new file embedded in delta
                new DeltaApplier { SkipHashCheck = false }.Apply(basis, new BinaryDeltaReader(delta, NullProgressReporter.Instance), output);
            }
        }

        // target files that already exist in local version: same path first, then any file of the same size (moved/renamed).
        // every local file is hashed at most once
        static Dictionary<string, Source> FindIdenticalFiles(string localVersionPath, VersionManifest manifest)
        {
            var ret = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
            var bySize = new DirectoryInfo(localVersionPath).GetFiles("*", SearchOption.AllDirectories)
                .GroupBy(f => f.Length).ToDictionary(g => g.Key, g => g.ToList());
            var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (ManifestFile mf in manifest.Files)
            {
                List<FileInfo> sameSize;
                if (!bySize.TryGetValue(mf.Size, out sameSize)) continue;

                string samePath = Path.Combine(localVersionPath, mf.Path);
                foreach (var fi in sameSize.OrderBy(f => String.Equals(f.FullName, samePath, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    string hash;
                    if (!hashCache.TryGetValue(fi.FullName, out hash))
                    {
                        try
                        {
                            fi.Refresh();
                            hash = DeltaIO.HashFile(fi.FullName);
                            var after = new FileInfo(fi.FullName);
                            if (after.Length != fi.Length || after.LastWriteTimeUtc != fi.LastWriteTimeUtc) hash = ""; //changed while hashing
                        }
                        catch { hash = ""; } //locked or unreadable, just download it
                        hashCache[fi.FullName] = hash;
                    }
                    if (hash == mf.Hash)
                    {
                        ret[mf.Path] = new Source(fi.FullName, hash, fi.Length, fi.LastWriteTimeUtc.Ticks);
                        break;
                    }
                }
            }
            return ret;
        }

        // Builds staging from content / delta data / share. Returns hash of every file written, computed on the way
        // (or already known), so Verify does not read the files again. Work files used once are moved, not copied.
        static Dictionary<string, string> Materialize(VersionManifest manifest, Dictionary<string, Source> content, Dictionary<string, FullData> fullData,
            string shareTarget, string staging, string work, StringBuilderExt changes)
        {
            foreach (string d in manifest.Dirs) Directory.CreateDirectory(Path.Combine(staging, d));

            var refs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestFile mf in manifest.Files)
            {
                Source s;
                if (content.TryGetValue(mf.Path, out s)) refs[s.File] = (refs.ContainsKey(s.File) ? refs[s.File] : 0) + 1;
            }

            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int gzCount = 0, plainCount = 0;
            long gzBytes = 0, plainBytes = 0;

            foreach (ManifestFile mf in manifest.Files)
            {
#if DEBUG
                if (TestDelayMs > 0) Thread.Sleep(TestDelayMs);
#endif
                string dest = Path.Combine(staging, mf.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));

                Source src;
                FullData fd;
                string hash;
                if (content.TryGetValue(mf.Path, out src))
                {
                    if (src.Hash != null && src.File.StartsWith(work, StringComparison.OrdinalIgnoreCase))
                    {
                        //our own work file, nobody else writes it, the hash checked when it was made still holds
                        if (refs[src.File] == 1) File.Move(src.File, dest);
                        else File.Copy(src.File, dest, true);
                        hash = src.Hash;
                    }
                    else if (src.Hash != null && Unchanged(src))
                    {
                        //file in a local version hashed by FindIdenticalFiles and not touched since, no need to read it twice
                        File.Copy(src.File, dest, true);
                        hash = src.Hash;
                    }
                    else
                    {
                        //file in a local version not hashed yet or changed since (user may be running it), hash what is really copied
                        hash = DeltaIO.CopyFileWithHash(src.File, dest);
                    }
                }
                else if (fullData.TryGetValue(mf.Path, out fd) && fd.Hash == mf.Hash)
                {
                    hash = DeltaIO.GUnzipFile(fd.GzFile, dest);
                    gzCount++; gzBytes += fd.DataSize;
                }
                else
                {
                    hash = DeltaIO.CopyFileWithHash(Path.Combine(shareTarget, mf.Path), dest);
                    plainCount++; plainBytes += mf.Size;
                }

                SetAttributes(dest, mf);
                hashes[mf.Path] = hash;
            }

            if (gzCount > 0) changes.AppendLine("COPY FROM DELTA DATA : " + gzCount + " file(s), " + DeltaIO.Mb(gzBytes));
            if (plainCount > 0) changes.AppendLine("COPY FROM SHARE : " + plainCount + " file(s), " + DeltaIO.Mb(plainBytes));
            return hashes;
        }

        static bool Unchanged(Source src)
        {
            var fi = new FileInfo(src.File);
            return fi.Exists && fi.Length == src.Size && fi.LastWriteTimeUtc.Ticks == src.WriteTicks;
        }

        // not ReadOnly (we must be able to delete old versions), Hidden/System as on share (from manifest), LastWriteTime as on share
        static void SetAttributes(string dest, ManifestFile mf)
        {
            var fi = new FileInfo(dest);
            fi.Attributes = FileAttributes.Normal;
            fi.LastWriteTimeUtc = new DateTime(mf.LastWriteTimeUtcTicks, DateTimeKind.Utc);
            var keep = (FileAttributes)mf.Attributes & (FileAttributes.Hidden | FileAttributes.System);
            if (keep != 0) fi.Attributes = keep;
        }

        // every file against manifest, a wrong one is copied once more from share, second failure aborts
        static void Verify(VersionManifest manifest, Dictionary<string, string> hashes, string shareTarget, string staging, StringBuilderExt changes)
        {
            foreach (ManifestFile mf in manifest.Files)
            {
                if (hashes[mf.Path] == mf.Hash) continue;

                changes.AppendLine("HASH MISMATCH, COPY AGAIN : " + mf.Path);
                string dest = Path.Combine(staging, mf.Path);
                string source = Path.Combine(shareTarget, mf.Path);
                File.SetAttributes(dest, FileAttributes.Normal); //overwriting a hidden/system file fails with access denied
                if (DeltaIO.CopyFileWithHash(source, dest) != mf.Hash)
                {
                    throw new InvalidDataException("File on share does not match manifest : " + source);
                }
                SetAttributes(dest, mf);
            }

            int count = new DirectoryInfo(staging).GetFiles("*", SearchOption.AllDirectories).Length;
            if (count != manifest.Files.Count) throw new InvalidDataException("File count " + count + " does not match manifest " + manifest.Files.Count + " : " + staging);
        }

        // antivirus / indexer may hold a handle for a moment after we wrote the files
        static void MoveWithRetry(string from, string to)
        {
            for (int i = 1; ; i++)
            {
                try { Directory.Move(from, to); return; }
                catch (IOException) when (i < 5) { Thread.Sleep(2000); }
                catch (UnauthorizedAccessException) when (i < 5) { Thread.Sleep(2000); }
            }
        }

        static void DeleteDir(string path)
        {
            DeltaIO.DeleteDirectory(path);
        }
    }
}
