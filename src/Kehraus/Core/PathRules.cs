using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kehraus.Core
{
    /// <summary>
    /// Regeln, welcher Ordner "der Ordner eines Programms" ist – und welche Ordner
    /// (Program Files, AppData, Installer-Caches …) niemals einem Programm zugerechnet werden.
    /// </summary>
    public static class PathRules
    {
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        private static string Combine(string a, string b) => string.IsNullOrEmpty(a) ? null : Path.Combine(a, b);

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

        /// <summary>Vollständiger Pfad ohne abschließenden Backslash (außer bei "C:\").</summary>
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

        /// <summary>Pfad sagt nichts über den Installationsort aus (Windows-Ordner, Installer-Cache, Temp).</summary>
        public static bool IsNoHint(string p)
        {
            string n = Norm(p);
            if (n == null) return true;
            if (IsUnder(n, NoHint)) return true;
            if (n.IndexOf(@"\InstallShield Installation Information", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith(@"\Package Cache", StringComparison.OrdinalIgnoreCase)) return true;
            return Guid.TryParseExact(Path.GetFileName(n), "B", out _);   // Ordner "{GUID}" = Installer-Cache
        }

        public static bool IsShellFolder(string n)
        {
            return n.IndexOf(@"\Start Menu", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf(@"\Quick Launch", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith(@"\Desktop", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf(@"\Desktop\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Ordner, dessen Größe NICHT einem einzelnen Programm zugerechnet werden darf.</summary>
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

            return IsUserContainer(n) || IsNoHint(n);
        }

        /// <summary>Aus "...\Foo\bin" oder "...\Foo\1.2.3" wird "...\Foo".</summary>
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

        /// <summary>Oberster nicht-allgemeiner Ordner auf dem Weg (C:\Program Files\Foo\bin → C:\Program Files\Foo).</summary>
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

        /// <summary>Ordner aus einem Befehl/Symbolpfad: "C:\Foo\bar.exe" /x  bzw.  C:\Foo\bar.exe,0</summary>
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
}
