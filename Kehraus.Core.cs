// Kehraus – Kern
// --------------
// Liest installierte Programme (Registry, Windows Installer), ermittelt ihren
// tatsächlichen Installationsordner und misst dessen Größe auf der Festplatte.
//
// Wird von Kehraus.ps1 zur Laufzeit per Add-Type kompiliert. Bewusst in C# 5
// geschrieben, damit es auch mit dem Compiler von Windows PowerShell 5.1 läuft
// (keine String-Interpolation, kein ?., keine Auto-Property-Initialisierer).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Kehraus
{
    // ------------------------------------------------------------------ Formatierung

    public static class Fmt
    {
        public static readonly CultureInfo De = new CultureInfo("de-DE");

        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            string f = i == 0 ? "0" : (v < 10 ? "0.00" : (v < 100 ? "0.0" : "0"));
            return v.ToString(f, De) + " " + units[i];
        }
    }

    // ------------------------------------------------------------------ Ein Programm

    public class AppEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(params string[] names)
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            foreach (var n in names) handler(this, new PropertyChangedEventArgs(n));
        }

        public string Key { get; set; }                  // eindeutiger Schlüssel (Registry-Pfad bzw. appx:Paketname)
        public string Name { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Kind { get; set; }                 // Win32 | MSI | Store
        public string Scope { get; set; }                // Alle Benutzer · 64-Bit usw.
        public string RegistryPath { get; set; }
        public string ProductCode { get; set; }          // {GUID}, falls vorhanden
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }
        public string PackageFullName { get; set; }      // nur Store-Apps

        private long _registryBytes = -1;
        public long RegistryBytes { get { return _registryBytes; } set { _registryBytes = value; } }
        public string RegistrySizeText { get { return Fmt.Size(_registryBytes); } }

        // --- Installationsdatum
        private string _dateText = "";
        private int _dateSort;
        public string InstallDateText { get { return _dateText; } }
        public int InstallDateSort { get { return _dateSort; } }

        public void SetInstallDate(string raw)
        {
            _dateText = ""; _dateSort = 0;
            if (string.IsNullOrWhiteSpace(raw)) return;
            string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy", "M/d/yyyy", "MM/dd/yyyy" };
            DateTime dt;
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                && dt.Year > 1990 && dt.Year < 2100)
            {
                _dateText = dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                _dateSort = dt.Year * 10000 + dt.Month * 100 + dt.Day;
            }
        }

        // --- Installationsordner
        private volatile string _path;
        private volatile string _pathSource;
        public string InstallPath { get { return _path; } }
        public string PathSource { get { return _pathSource; } }

        public string Drive
        {
            get
            {
                string p = _path;
                if (string.IsNullOrEmpty(p)) return "?";
                try
                {
                    string root = Path.GetPathRoot(p);
                    return string.IsNullOrEmpty(root) ? "?" : root.TrimEnd('\\').ToUpperInvariant();
                }
                catch { return "?"; }
            }
        }

        public void SetPath(string path, string source)
        {
            _path = PathRules.Norm(path) ?? path;
            _pathSource = source;
            Raise("InstallPath", "PathSource", "Drive");
        }

        // --- Messung: 0 = wartet, 1 = gemessen, 2 = nicht messbar
        private volatile int _state;
        private long _measured = -1;
        private int _files;
        private bool _partial;
        private string _reason;

        public bool IsPending { get { return _state == 0; } }
        public bool IsMeasured { get { return _state == 1; } }
        public bool IsEstimate { get { return _state != 1; } }
        public long MeasuredBytes { get { return _state == 1 ? _measured : -1; } }
        public int FileCount { get { return _state == 1 ? _files : 0; } }

        // Sortierwert: gemessen > Registry-Angabe > unbekannt
        public long SortBytes
        {
            get
            {
                if (_state == 1) return _measured;
                return _registryBytes >= 0 ? _registryBytes : -1;
            }
        }

        public string SizeText
        {
            get
            {
                switch (_state)
                {
                    case 0: return "…";
                    case 1: return Fmt.Size(_measured);
                    default: return _registryBytes >= 0 ? "≈ " + Fmt.Size(_registryBytes) : "—";
                }
            }
        }

        public string SizeSource
        {
            get
            {
                switch (_state)
                {
                    case 0: return "wird ermittelt …";
                    case 1: return _partial ? "gemessen – teilweise kein Zugriff" : "gemessen";
                    default: return (_registryBytes >= 0 ? "Registry-Angabe – " : "unbekannt – ") + _reason;
                }
            }
        }

        public string FilesText
        {
            get { return _state == 1 ? _files.ToString("N0", Fmt.De) + " Dateien" : ""; }
        }

        public void ResetMeasurement()
        {
            _measured = -1; _files = 0; _partial = false; _reason = null;
            _state = 0;
            RaiseSize();
        }

        public void SetMeasured(long bytes, int files, bool partial)
        {
            _measured = bytes; _files = files; _partial = partial; _reason = null;
            _state = 1;
            RaiseSize();
        }

        public void SetNotMeasured(string reason)
        {
            _measured = -1; _files = 0; _partial = false; _reason = reason;
            _state = 2;
            RaiseSize();
        }

        private void RaiseSize()
        {
            Raise("SizeText", "SizeSource", "SortBytes", "IsEstimate", "IsPending", "IsMeasured",
                  "MeasuredBytes", "FileCount", "FilesText");
        }
    }

    // ------------------------------------------------------------------ Pfadregeln

    public static class PathRules
    {
        // Ordner, die nie "der Ordner eines Programms" sind (Program Files, AppData, …)
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Orte, die gar nichts über den Installationsort aussagen (Windows, Installer-Caches, Temp)
        private static readonly List<string> NoHint = new List<string>();
        private static readonly string UsersDir;

        // Sammelordner direkt unter einer Laufwerkswurzel (D:\Programme, D:\Games, …)
        private static readonly HashSet<string> CollectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program Files", "Program Files (x86)", "Programme", "Programs", "Games", "Spiele", "SteamLibrary"
        };

        // Unterordner, in denen oft nur die EXE liegt – dann eine Ebene höher gehen
        private static readonly HashSet<string> SubFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin32", "bin64", "x64", "x86", "win32", "win64", "app", "application", "lib", "program",
            "uninstall", "uninst", "_uninst", "uninstaller", "setup", "installer", "resources", "current",
            "icons", "res", "images"
        };

        private static readonly Regex VersionFolder = new Regex(@"^(app-)?v?\d+(\.\d+)+$", RegexOptions.IgnoreCase);

        static PathRules()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programData = Env("ProgramData");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pf64 = Env("ProgramW6432") ?? Env("ProgramFiles");
            UsersDir = Norm(Parent(profile));

            string[] generic =
            {
                win, Combine(win, "System32"), Combine(win, "SysWOW64"),
                Env("ProgramFiles"), Env("ProgramFiles(x86)"), Env("ProgramW6432"),
                Env("CommonProgramFiles"), Env("CommonProgramFiles(x86)"), Env("CommonProgramW6432"),
                Combine(pf64, "WindowsApps"), Combine(pf64, "ModifiableWindowsApps"),
                programData, Env("PUBLIC"), profile, local, roaming, Parent(local), Combine(local, "Programs")
            };
            foreach (var s in generic)
            {
                var n = Norm(s);
                if (n != null) Generic.Add(n);
            }

            string[] noHint =
            {
                win, Combine(programData, "Package Cache"), Combine(local, "Package Cache"),
                Combine(local, "Temp"), Path.GetTempPath()
            };
            foreach (var s in noHint)
            {
                var n = Norm(s);
                if (n != null && !NoHint.Contains(n, StringComparer.OrdinalIgnoreCase)) NoHint.Add(n);
            }
        }

        private static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private static string Combine(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? null : Path.Combine(a, b);
        }

        private static string Parent(string a)
        {
            try { return string.IsNullOrEmpty(a) ? null : Path.GetDirectoryName(a.TrimEnd('\\')); }
            catch { return null; }
        }

        public static bool IsAbsolute(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length < 3) return false;
            if (char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) return true;
            return p.StartsWith(@"\\") && !p.StartsWith(@"\\?\");
        }

        // Vollständiger Pfad ohne abschließenden Backslash (außer bei "C:\")
        public static string Norm(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = p.Trim();
            if (!IsAbsolute(p)) return null;
            try
            {
                string full = Path.GetFullPath(p);
                if (full.Length > 3) full = full.TrimEnd('\\');
                return full;
            }
            catch { return null; }
        }

        private static bool IsUnder(string n, List<string> bases)
        {
            foreach (var b in bases)
            {
                if (string.Equals(n, b, StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = b.EndsWith("\\") ? b : b + "\\";
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // C:\Users\Name, ...\AppData, ...\AppData\Local, ...\AppData\Local\Programs
        private static bool IsUserContainer(string n)
        {
            if (UsersDir == null) return false;
            string prefix = UsersDir + "\\";
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string[] seg = n.Substring(prefix.Length).Split('\\');
            if (seg.Length == 1) return true;
            if (!seg[1].Equals("AppData", StringComparison.OrdinalIgnoreCase)) return false;
            if (seg.Length <= 3) return true;
            return seg.Length == 4
                && seg[2].Equals("Local", StringComparison.OrdinalIgnoreCase)
                && seg[3].Equals("Programs", StringComparison.OrdinalIgnoreCase);
        }

        // Pfad sagt nichts über den Installationsort aus (Windows-Ordner, Installer-Cache, Temp)
        public static bool IsNoHint(string p)
        {
            string n = Norm(p);
            if (n == null) return true;
            if (IsUnder(n, NoHint)) return true;
            if (n.IndexOf(@"\InstallShield Installation Information", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith(@"\Package Cache", StringComparison.OrdinalIgnoreCase)) return true;
            Guid g;
            if (Guid.TryParseExact(Path.GetFileName(n), "B", out g)) return true;   // Ordner "{GUID}" = Installer-Cache
            return false;
        }

        public static bool IsShellFolder(string n)
        {
            return n.IndexOf(@"\Start Menu", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf(@"\Quick Launch", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith(@"\Desktop", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf(@"\Desktop\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Ordner, dessen Größe NICHT einem einzelnen Programm zugerechnet werden darf
        public static bool IsGeneric(string p)
        {
            string n = Norm(p);
            if (n == null) return true;
            if (Generic.Contains(n)) return true;

            string root = Path.GetPathRoot(n);
            if (string.IsNullOrEmpty(root)) return true;
            string rootTrim = root.TrimEnd('\\');
            if (string.Equals(n.TrimEnd('\\'), rootTrim, StringComparison.OrdinalIgnoreCase)) return true;

            string parent = Path.GetDirectoryName(n);
            string name = Path.GetFileName(n);
            if (parent != null && string.Equals(parent.TrimEnd('\\'), rootTrim, StringComparison.OrdinalIgnoreCase)
                && CollectionNames.Contains(name)) return true;
            if (name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("common", StringComparison.OrdinalIgnoreCase) && parent != null
                && Path.GetFileName(parent).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return true;

            if (IsUserContainer(n)) return true;
            if (IsNoHint(n)) return true;
            return false;
        }

        // Aus "...\Foo\bin" oder "...\Foo\1.2.3" wird "...\Foo"
        public static string Climb(string dir)
        {
            if (dir == null) return null;
            for (int i = 0; i < 4; i++)
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) break;
                if (!SubFolderNames.Contains(name) && !VersionFolder.IsMatch(name)) break;
                string parent = Path.GetDirectoryName(dir);
                if (parent == null || IsGeneric(parent)) break;
                dir = parent;
            }
            return dir;
        }

        // Oberster nicht-allgemeiner Ordner auf dem Weg zu dir (C:\Program Files\Foo\bin → C:\Program Files\Foo)
        public static string Anchor(string dir)
        {
            string cur = Norm(dir);
            if (cur == null) return dir;
            while (true)
            {
                string parent = Path.GetDirectoryName(cur);
                if (parent == null || IsGeneric(parent)) return cur;
                cur = parent;
            }
        }

        public static string FromInstallLocation(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = Environment.ExpandEnvironmentVariables(raw.Trim()).Trim().Trim('"').Trim();
            if (!IsAbsolute(s)) return null;
            try { if (File.Exists(s)) s = Path.GetDirectoryName(s); } catch { }
            return Norm(s);
        }

        // Ordner aus einem Befehl/Symbolpfad: "C:\Foo\bar.exe" /x  bzw.  C:\Foo\bar.exe,0
        public static string FromCommand(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = Environment.ExpandEnvironmentVariables(raw.Trim());
            string file;
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end <= 1) return null;
                file = s.Substring(1, end - 1);
            }
            else
            {
                int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) file = s.Substring(0, exe + 4);
                else
                {
                    int comma = s.LastIndexOf(',');
                    file = comma > 0 ? s.Substring(0, comma) : s;
                }
            }
            file = file.Trim();
            if (!IsAbsolute(file)) return null;

            string dir;
            try { dir = Path.GetDirectoryName(file); } catch { return null; }
            dir = Norm(dir);
            if (dir == null || IsNoHint(dir)) return null;
            return Climb(dir);
        }
    }

    // ------------------------------------------------------------------ Registry auslesen

    public static class Inventory
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<AppEntry> LoadWin32()
        {
            var list = new List<AppEntry>();
            if (Environment.Is64BitOperatingSystem)
            {
                Read(RegistryHive.LocalMachine, RegistryView.Registry64,
                     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", "Alle Benutzer · 64-Bit", list);
                Read(RegistryHive.LocalMachine, RegistryView.Registry32,
                     @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\", "Alle Benutzer · 32-Bit", list);
            }
            else
            {
                Read(RegistryHive.LocalMachine, RegistryView.Default, @"HKLM\" + UninstallKey + @"\", "Alle Benutzer", list);
            }
            Read(RegistryHive.CurrentUser, RegistryView.Default, @"HKCU\" + UninstallKey + @"\", "Nur aktueller Benutzer", list);
            return list;
        }

        private static void Read(RegistryHive hive, RegistryView view, string displayPrefix, string scope, List<AppEntry> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var root = baseKey.OpenSubKey(UninstallKey))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = root.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                var e = FromKey(k, name, displayPrefix + name, scope);
                                if (e != null) list.Add(e);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static AppEntry FromKey(RegistryKey k, string keyName, string fullKey, string scope)
        {
            string name = Str(k, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Num(k, "SystemComponent") == 1) return null;                    // von Windows ausgeblendet
            if (!string.IsNullOrEmpty(Str(k, "ParentKeyName"))) return null;    // Update zu einem anderen Eintrag
            string release = Str(k, "ReleaseType") ?? "";
            if (release.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0
                || release.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            bool msi = Num(k, "WindowsInstaller") == 1;
            string uninstall = Str(k, "UninstallString");
            if (!msi && string.IsNullOrWhiteSpace(uninstall)) return null;      // zeigt Windows auch nicht an

            var e = new AppEntry();
            e.Key = fullKey;
            e.RegistryPath = fullKey;
            e.Name = name.Trim();
            e.Version = Str(k, "DisplayVersion");
            e.Publisher = Str(k, "Publisher");
            e.Scope = scope;
            e.Kind = msi ? "MSI" : "Win32";
            e.UninstallString = uninstall;
            e.QuietUninstallString = Str(k, "QuietUninstallString");
            Guid g;
            if (Guid.TryParseExact(keyName, "B", out g)) e.ProductCode = keyName.ToUpperInvariant();
            long est = Num(k, "EstimatedSize");                                  // in KB
            e.RegistryBytes = est > 0 ? est * 1024 : -1;
            e.SetInstallDate(Str(k, "InstallDate"));

            // Installationsordner: bevorzugt ein konkreter, existierender Ordner
            var cands = new List<KeyValuePair<string, string>>();
            AddCand(cands, PathRules.FromInstallLocation(Str(k, "InstallLocation")), "InstallLocation (Registry)");
            AddCand(cands, PathRules.FromCommand(Str(k, "DisplayIcon")), "Programmsymbol (DisplayIcon)");
            if (!msi) AddCand(cands, PathRules.FromCommand(uninstall), "Ort des Deinstallationsprogramms");

            var pick = cands.FirstOrDefault(c => !PathRules.IsGeneric(c.Key) && DirExists(c.Key));
            if (pick.Key == null) pick = cands.FirstOrDefault(c => !PathRules.IsGeneric(c.Key));
            if (pick.Key == null) pick = cands.FirstOrDefault();
            if (pick.Key != null) e.SetPath(pick.Key, pick.Value);
            return e;
        }

        private static void AddCand(List<KeyValuePair<string, string>> cands, string path, string source)
        {
            if (path != null) cands.Add(new KeyValuePair<string, string>(path, source));
        }

        private static bool DirExists(string p)
        {
            try { return Directory.Exists(p); } catch { return false; }
        }

        private static string Str(RegistryKey k, string n)
        {
            try { var v = k.GetValue(n); return v == null ? null : v.ToString(); }
            catch { return null; }
        }

        private static long Num(RegistryKey k, string n)
        {
            try
            {
                var v = k.GetValue(n);
                if (v == null) return -1;
                if (v is int) return (long)(uint)(int)v;     // DWORD vorzeichenlos lesen
                if (v is long) return (long)v;
                long r;
                return long.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : -1;
            }
            catch { return -1; }
        }
    }

    // ------------------------------------------------------------------ MSI-Pakete ohne Pfadangabe

    // Viele MSI-Pakete tragen keinen InstallLocation ein. Windows Installer merkt sich
    // aber für jede Komponente, wo ihre Schlüsseldatei liegt – daraus wird der Ordner abgeleitet.
    public static class MsiResolver
    {
        public static Dictionary<string, string> Resolve(ICollection<string> productCodes, CancellationToken tok)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // komprimierte GUID → Produktcode
            foreach (var pc in productCodes)
            {
                string c = Compress(pc);
                if (c != null && !wanted.ContainsKey(c)) wanted[c] = pc.ToUpperInvariant();
            }
            if (wanted.Count == 0) return result;

            var dirs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var userData = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData"))
                {
                    if (userData == null) return result;
                    foreach (var sid in userData.GetSubKeyNames())
                    {
                        if (tok.IsCancellationRequested) return result;
                        RegistryKey comps = null;
                        try { comps = userData.OpenSubKey(sid + @"\Components"); } catch { }
                        if (comps == null) continue;
                        using (comps)
                        {
                            foreach (var compName in comps.GetSubKeyNames())
                            {
                                if (tok.IsCancellationRequested) return result;
                                try
                                {
                                    using (var ck = comps.OpenSubKey(compName))
                                    {
                                        if (ck == null) continue;
                                        foreach (var valueName in ck.GetValueNames())
                                        {
                                            string pc;
                                            if (!wanted.TryGetValue(valueName, out pc)) continue;
                                            string d = KeyPathDir(ck.GetValue(valueName) as string);
                                            if (d == null) continue;
                                            List<string> l;
                                            if (!dirs.TryGetValue(pc, out l)) { l = new List<string>(); dirs[pc] = l; }
                                            l.Add(d);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            foreach (var kv in dirs)
            {
                string best = PickFolder(kv.Value);
                if (best != null) result[kv.Key] = best;
            }
            return result;
        }

        // {12345678-ABCD-EF01-2345-6789ABCDEF01} → 87654321DCBA10EF325476981BADCFED10
        public static string Compress(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string g = guid.Trim().Trim('{', '}').Replace("-", "").ToUpperInvariant();
            if (g.Length != 32) return null;
            var sb = new StringBuilder(32);
            sb.Append(Reverse(g.Substring(0, 8)));
            sb.Append(Reverse(g.Substring(8, 4)));
            sb.Append(Reverse(g.Substring(12, 4)));
            for (int i = 16; i < 32; i += 2) { sb.Append(g[i + 1]); sb.Append(g[i]); }
            return sb.ToString();
        }

        private static string Reverse(string s)
        {
            char[] a = s.ToCharArray();
            Array.Reverse(a);
            return new string(a);
        }

        private static string KeyPathDir(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Length < 4) return null;
            if (!char.IsLetter(v[0]) || (v[1] != ':' && v[1] != '?') || v[2] != '\\') return null;   // Registry-Schlüsselpfade überspringen
            v = v[0] + ":" + v.Substring(2);
            try
            {
                string d = v.EndsWith("\\") ? v.TrimEnd('\\') : Path.GetDirectoryName(v);
                d = PathRules.Norm(d);
                if (d == null || PathRules.IsNoHint(d) || PathRules.IsShellFolder(d)) return null;
                return d;
            }
            catch { return null; }
        }

        private static string PickFolder(List<string> dirs)
        {
            var groups = dirs.GroupBy(d => PathRules.Anchor(d), StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(g => g.Count());
            foreach (var g in groups)
            {
                string common = CommonDir(g.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                common = PathRules.Climb(PathRules.Norm(common));
                if (common != null && !PathRules.IsGeneric(common)) return common;
            }
            return null;
        }

        private static string CommonDir(IList<string> dirs)
        {
            if (dirs.Count == 0) return null;
            string[] first = dirs[0].Split('\\');
            int len = first.Length;
            for (int i = 1; i < dirs.Count; i++)
            {
                string[] s = dirs[i].Split('\\');
                int j = 0;
                while (j < len && j < s.Length && string.Equals(first[j], s[j], StringComparison.OrdinalIgnoreCase)) j++;
                len = j;
            }
            if (len == 0) return null;
            string r = string.Join("\\", first, 0, len);
            if (r.Length == 2 && r[1] == ':') r += "\\";
            return r;
        }
    }

    // ------------------------------------------------------------------ Größen messen (im Hintergrund)

    public static class Scanner
    {
        private static CancellationTokenSource _cts;
        private static int _done, _total, _running, _indeterminate;
        private static volatile string _phase = "";
        private static volatile string _current = "";

        public static int Done { get { return Volatile.Read(ref _done); } }
        public static int Total { get { return Volatile.Read(ref _total); } }
        public static bool Running { get { return Volatile.Read(ref _running) == 1; } }
        public static bool Indeterminate { get { return Volatile.Read(ref _indeterminate) == 1; } }
        public static string Phase { get { return _phase; } }
        public static string Current { get { return _current; } }

        public static void Cancel()
        {
            var c = _cts;
            if (c != null) c.Cancel();
        }

        public static void Start(IEnumerable items)
        {
            Cancel();
            var list = items.OfType<AppEntry>().ToList();
            foreach (var a in list) a.ResetMeasurement();
            var cts = new CancellationTokenSource();
            _cts = cts;
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _total, list.Count);
            Interlocked.Exchange(ref _running, 1);
            Task.Factory.StartNew(() => Run(list, cts), CancellationToken.None,
                                  TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void Run(List<AppEntry> list, CancellationTokenSource cts)
        {
            var tok = cts.Token;
            try
            {
                // 1) MSI-Pakete ohne brauchbaren Pfad über die Installer-Komponenten auflösen
                var needMsi = list.Where(a => a.ProductCode != null
                                           && (a.InstallPath == null || PathRules.IsGeneric(a.InstallPath))).ToList();
                if (needMsi.Count > 0)
                {
                    _phase = "Werte Windows-Installer-Daten aus …";
                    Interlocked.Exchange(ref _indeterminate, 1);
                    var map = MsiResolver.Resolve(needMsi.Select(a => a.ProductCode).ToList(), tok);
                    foreach (var a in needMsi)
                    {
                        string p;
                        if (map.TryGetValue(a.ProductCode, out p)) a.SetPath(p, "Windows-Installer-Komponenten");
                    }
                    Interlocked.Exchange(ref _indeterminate, 0);
                }
                if (tok.IsCancellationRequested) return;

                // 2) Ordner messen – ein Durchlauf pro Laufwerk, damit HDD und SSD parallel laufen,
                //    eine HDD aber nicht durch mehrere gleichzeitige Leser ausgebremst wird.
                _phase = "Messe Ordnergrößen:";
                var cache = new ConcurrentDictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                var tasks = list.GroupBy(a => a.Drive, StringComparer.OrdinalIgnoreCase)
                    .Select(g => Task.Factory.StartNew(() =>
                    {
                        foreach (var a in g)
                        {
                            if (tok.IsCancellationRequested) return;
                            MeasureOne(a, cache, tok);
                            if (!tok.IsCancellationRequested) Interlocked.Increment(ref _done);
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                    .ToArray();
                Task.WaitAll(tasks);
            }
            catch { }
            finally
            {
                if (_cts == cts)
                {
                    _phase = "Fertig";
                    _current = "";
                    Interlocked.Exchange(ref _indeterminate, 0);
                    Interlocked.Exchange(ref _running, 0);
                }
            }
        }

        private static void MeasureOne(AppEntry a, ConcurrentDictionary<string, long[]> cache, CancellationToken tok)
        {
            string p = a.InstallPath;
            if (string.IsNullOrEmpty(p)) { a.SetNotMeasured("Ordner nicht ermittelbar"); return; }
            if (PathRules.IsGeneric(p)) { a.SetNotMeasured("nur Sammelordner bekannt"); return; }
            bool exists;
            try { exists = Directory.Exists(p); } catch { exists = false; }
            if (!exists) { a.SetNotMeasured("Ordner existiert nicht mehr"); return; }

            _current = p;
            long[] m;
            if (!cache.TryGetValue(p, out m))
            {
                long bytes; int files; bool partial;
                FolderSize(p, tok, out bytes, out files, out partial);
                if (tok.IsCancellationRequested) return;
                m = new long[] { bytes, files, partial ? 1 : 0 };
                cache[p] = m;
            }
            if (m[2] == 1 && m[1] == 0) { a.SetNotMeasured("kein Zugriff – als Administrator starten"); return; }
            a.SetMeasured(m[0], (int)m[1], m[2] == 1);
        }

        // Summe aller Dateigrößen. Verknüpfte Ordner (Junctions/Symlinks) werden nicht verfolgt,
        // damit nichts doppelt oder endlos gezählt wird.
        public static void FolderSize(string root, CancellationToken tok, out long bytes, out int files, out bool partial)
        {
            bytes = 0; files = 0; partial = false;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                if (tok.IsCancellationRequested) { partial = true; return; }
                var dir = stack.Pop();
                try
                {
                    foreach (var fsi in dir.EnumerateFileSystemInfos())
                    {
                        var fi = fsi as FileInfo;
                        if (fi != null)
                        {
                            try { bytes += fi.Length; files++; } catch { partial = true; }
                        }
                        else if ((fsi.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            stack.Push((DirectoryInfo)fsi);
                        }
                    }
                }
                catch { partial = true; }
            }
        }
    }

    // ------------------------------------------------------------------ Laufwerksübersicht

    public class DriveCard
    {
        public string Title { get; set; }
        public string Line1 { get; set; }
        public string Line2 { get; set; }
        public double UsedPercent { get; set; }
        public bool HasBar { get; set; }
    }

    public static class Summary
    {
        public static List<DriveCard> Build(IEnumerable items)
        {
            var all = items.OfType<AppEntry>().ToList();
            var cards = new List<DriveCard>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sys = "";
            try { sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\'); }
            catch { }

            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                    if (!di.IsReady) continue;
                    string letter = di.Name.TrimEnd('\\').ToUpperInvariant();
                    var mine = all.Where(a => a.Drive == letter).ToList();
                    long total = di.TotalSize, free = di.TotalFreeSpace, used = total - free;
                    string label = di.VolumeLabel;

                    var c = new DriveCard();
                    c.Title = letter
                        + (string.IsNullOrEmpty(label) ? "" : "  " + label)
                        + (letter.Equals(sys, StringComparison.OrdinalIgnoreCase) ? "  · System" : "");
                    c.UsedPercent = total > 0 ? used * 100.0 / total : 0;
                    c.HasBar = true;
                    c.Line1 = Fmt.Size(used) + " von " + Fmt.Size(total) + " belegt · " + Fmt.Size(free) + " frei";
                    c.Line2 = ProgramLine(mine);
                    cards.Add(c);
                    seen.Add(letter);
                }
                catch { }
            }

            // Laufwerke, auf die Einträge zeigen, die aber gerade nicht da sind (Netz, abgezogen, …)
            foreach (var g in all.Where(a => a.Drive != "?" && !seen.Contains(a.Drive)).GroupBy(a => a.Drive))
            {
                cards.Add(new DriveCard
                {
                    Title = g.Key + "  · nicht verfügbar",
                    Line1 = "Laufwerk ist gerade nicht erreichbar",
                    Line2 = ProgramLine(g.ToList())
                });
            }

            var unknown = all.Where(a => a.Drive == "?").ToList();
            if (unknown.Count > 0)
            {
                long reg = unknown.Where(a => a.RegistryBytes > 0).Sum(a => a.RegistryBytes);
                cards.Add(new DriveCard
                {
                    Title = "Ohne erkennbaren Ordner",
                    Line1 = unknown.Count + (unknown.Count == 1 ? " Eintrag" : " Einträge"),
                    Line2 = reg > 0 ? "laut Registry ≈ " + Fmt.Size(reg) : "keine Größenangabe"
                });
            }
            return cards;
        }

        private static string ProgramLine(List<AppEntry> mine)
        {
            if (mine.Count == 0) return "Keine Programme";
            int pending = mine.Count(a => a.IsPending);
            int notMeasured = mine.Count(a => !a.IsPending && !a.IsMeasured);
            string s = "Programme: " + Fmt.Size(DedupSum(mine)) + " · " + mine.Count + (mine.Count == 1 ? " Eintrag" : " Einträge");
            if (pending > 0) s += " · misst noch " + pending;
            else if (notMeasured > 0) s += " · " + notMeasured + " ungemessen";
            return s;
        }

        // Gleiche oder ineinander liegende Ordner nur einmal zählen
        public static long DedupSum(IEnumerable<AppEntry> list)
        {
            var paths = list.Where(a => a.IsMeasured && a.InstallPath != null)
                            .GroupBy(a => a.InstallPath, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .OrderBy(a => a.InstallPath.Length)
                            .ToList();
            var counted = new List<string>();
            long sum = 0;
            foreach (var a in paths)
            {
                string p = a.InstallPath;
                if (counted.Any(c => p.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                counted.Add(p);
                sum += a.MeasuredBytes;
            }
            return sum;
        }
    }

    // ------------------------------------------------------------------ Store-App-Namen

    public static class Native
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

        public static string LoadIndirect(string source)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int hr = SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero);
                return hr == 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        // "ms-resource:AppName" aus dem Manifest in den echten Anzeigenamen übersetzen
        public static string ResolveStoreName(string packageFullName, string packageName, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            if (!raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return raw;

            string res = raw.Substring("ms-resource:".Length);
            var candidates = new List<string>();
            if (res.StartsWith("//")) candidates.Add("ms-resource:" + res);
            else if (res.StartsWith("/")) candidates.Add("ms-resource://" + packageName + res);
            else
            {
                if (res.Contains("/")) candidates.Add("ms-resource://" + packageName + "/" + res);
                candidates.Add("ms-resource://" + packageName + "/Resources/" + res);
            }
            foreach (var c in candidates)
            {
                string s = LoadIndirect("@{" + packageFullName + "?" + c + "}");
                if (!string.IsNullOrWhiteSpace(s) && !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    return s.Trim();
            }
            return null;
        }
    }
}
