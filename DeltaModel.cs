using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Shared between ArgosyUpdater (client) and ArgosyDeltaBuilder (server), keep it dependency free (except ZeroDep.Json).
//
// Layout on share (DeltaDir is relative to share root, e.g. \\bepo\ARGOSY\_DELTA):
//   <DeltaDir>\<VersionedDir>\manifests\<Version>.json                 full list of files of a version (written only when version is complete)
//   <DeltaDir>\<VersionedDir>\deltas\<From>__TO__<To>\delta.json       how to build <To> from <From>
//   <DeltaDir>\<VersionedDir>\deltas\<From>__TO__<To>\data\<n>.gz      gzipped octodiff delta or gzipped full file
// Anything starting with "~" is work in progress and must be ignored by readers.

namespace ArgosyUpdater.Delta
{
    public class ManifestFile
    {
        public string Path { get; set; }            // relative to version folder, e.g. "x64\SQLite.Interop.dll"
        public long Size { get; set; }
        public string Hash { get; set; }            // SHA256 hex
        public long LastWriteTimeUtcTicks { get; set; }
        public int Attributes { get; set; }         // FileAttributes, only Hidden / System are used
    }

    public class VersionManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string Version { get; set; }
        public long TotalSize { get; set; }
        public string Fingerprint { get; set; }     // cheap change detection of source folder (count/size/mtime)
        public List<string> Dirs { get; set; } = new List<string>();
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();
    }

    public static class DeltaOp
    {
        public const string Copy = "Copy";      // take SourcePath from previous version as is
        public const string Patch = "Patch";    // apply octodiff delta (DataFile) on SourcePath from previous version
        public const string Full = "Full";      // DataFile is gzipped full file
    }

    public class DeltaEntry
    {
        public string Path { get; set; }
        public string Op { get; set; }
        public string SourcePath { get; set; }
        public string SourceHash { get; set; }
        public string DataFile { get; set; }
        public long DataSize { get; set; }
        public long Size { get; set; }
        public string Hash { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
    }

    public class DeltaSet
    {
        public int FormatVersion { get; set; } = 1;
        public string From { get; set; }
        public string To { get; set; }
        public long DataSize { get; set; }          // bytes to download for this step
        public long TargetSize { get; set; }        // size of target version, uncompressed
        public DateTime CreatedUtc { get; set; }
        public List<string> Dirs { get; set; } = new List<string>();
        public List<DeltaEntry> Entries { get; set; } = new List<DeltaEntry>();
    }

    public static class DeltaLayout
    {
        public const string ManifestsDir = "manifests";
        public const string DeltasDir = "deltas";
        public const string DataDir = "data";
        public const string DeltaSetFile = "delta.json";
        public const string PairJoin = "__TO__";   // version names already contain "__"
        public const string WorkPrefix = "~";

        public static string Root(string shareRoot, string deltaDir, string versionedDir)
        {
            return System.IO.Path.Combine(shareRoot, deltaDir, versionedDir);
        }

        public static string ManifestPath(string root, string version)
        {
            return System.IO.Path.Combine(root, ManifestsDir, version + ".json");
        }

        // manifest of a version that is being changed on share (re-upload), hidden from clients as target,
        // kept so server can tell if content really changed and clients can still recognize their local copy of it
        public static string PendingManifestPath(string root, string version)
        {
            return System.IO.Path.Combine(root, ManifestsDir, WorkPrefix + version + ".json");
        }

        public static string PairName(string from, string to)
        {
            return from + PairJoin + to;
        }

        public static bool TryParsePair(string name, out string from, out string to)
        {
            from = null; to = null;
            if (name.StartsWith(WorkPrefix)) return false;
            int i = name.IndexOf(PairJoin, StringComparison.Ordinal);
            if (i <= 0) return false;
            from = name.Substring(0, i);
            to = name.Substring(i + PairJoin.Length);
            return to.Length > 0;
        }

        public static string DeltaSetPath(string root, string from, string to)
        {
            return System.IO.Path.Combine(root, DeltasDir, PairName(from, to));
        }
    }

    public static class VersionNames
    {
        // Same rule as ArgosyBoot.ps1: name starts with prefix, newest = highest name.
        // Ordinal compare so it does not depend on culture (Sort-Object in PS is culture aware but for "Argosy_yyyy_MM_dd__HH_mm_ss" it is the same).
        public static List<string> List(string versionedDirPath, string prefix)
        {
            if (!Directory.Exists(versionedDirPath)) return new List<string>();
            return new DirectoryInfo(versionedDirPath).GetDirectories()
                .Select(d => d.Name)
                .Where(n => IsVersionName(n, prefix))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool IsVersionName(string name, string prefix)
        {
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static int Compare(string a, string b)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }
    }

    public static class DeltaIO
    {
        public static string HashFile(string path)
        {
            // SHA256CryptoServiceProvider, not SHA256.Create() (= SHA256Managed on .NET Framework, throws when FIPS policy is enabled)
            using (var sha = new SHA256CryptoServiceProvider())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void GZipFile(string source, string dest)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            {
                input.CopyTo(gz, 1 << 20);
            }
        }

        // returns SHA256 of the decompressed content, computed while writing it
        public static string GUnzipFile(string source, string dest)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            {
                return CopyWithHash(gz, dest);
            }
        }

        // copy that hashes on the way, so the copied file does not have to be read again for verification
        public static string CopyFileWithHash(string source, string dest)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                return CopyWithHash(input, dest);
            }
        }

        static string CopyWithHash(Stream input, string dest)
        {
            using (var sha = new SHA256CryptoServiceProvider())
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    output.Write(buffer, 0, read);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash);
            }
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("N1") + " MB";
        }

        public static T ReadJson<T>(string path)
        {
            return (T)ZeroDep.Json.Deserialize(File.ReadAllText(path, Encoding.UTF8), typeof(T));
        }

        // write to temp and rename, so readers never see half written json
        public static void WriteJson(string path, object value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ZeroDep.Json.SerializeFormatted(value), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string RelativePath(string root, string fullPath)
        {
            return fullPath.Substring(root.TrimEnd('\\').Length + 1);
        }

        public static void DeleteDirectory(string path)
        {
            path = LongPath(path);
            if (!Directory.Exists(path)) return;
            // clear read-only flags, Directory.Delete fails on them
            foreach (var f in new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories))
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
            }
            Directory.Delete(path, true);
        }

        // \\?\ prefix (supported by System.IO since .NET 4.6.2), so leftovers deeper than MAX_PATH can still be removed
        public static string LongPath(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }
    }
}
