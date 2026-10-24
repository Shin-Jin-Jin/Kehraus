using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Kehraus.Core
{
    /// <summary>
    /// Viele MSI-Pakete tragen keinen InstallLocation ein. Windows Installer merkt sich aber
    /// für jede Komponente, wo ihre Schlüsseldatei liegt – daraus wird der Ordner abgeleitet.
    /// </summary>
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
                                            if (!wanted.TryGetValue(valueName, out var pc)) continue;
                                            string d = KeyPathDir(ck.GetValue(valueName) as string);
                                            if (d == null) continue;
                                            if (!dirs.TryGetValue(pc, out var l)) dirs[pc] = l = new List<string>();
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

        /// <summary>{12345678-ABCD-EF01-2345-6789ABCDEF01} → 87654321DCBA10FE32547698BADCFE10</summary>
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
            var groups = dirs.GroupBy(PathRules.Anchor, StringComparer.OrdinalIgnoreCase)
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
}
