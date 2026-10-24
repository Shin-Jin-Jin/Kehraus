using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Kehraus.Core
{
    /// <summary>Liest die klassischen Programme aus den Uninstall-Schlüsseln der Registry.</summary>
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

            var e = new AppEntry
            {
                Key = fullKey,
                RegistryPath = fullKey,
                Name = name.Trim(),
                Version = Str(k, "DisplayVersion"),
                Publisher = Str(k, "Publisher"),
                Scope = scope,
                Kind = msi ? AppEntry.KindMsi : AppEntry.KindWin32,
                UninstallString = uninstall,
                QuietUninstallString = Str(k, "QuietUninstallString"),
            };
            if (Guid.TryParseExact(keyName, "B", out _)) e.ProductCode = keyName.ToUpperInvariant();
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
            try { return k.GetValue(n)?.ToString(); }
            catch { return null; }
        }

        private static long Num(RegistryKey k, string n)
        {
            try
            {
                object v = k.GetValue(n);
                if (v == null) return -1;
                if (v is int i) return (uint)i;          // DWORD vorzeichenlos lesen
                if (v is long l) return l;
                return long.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : -1;
            }
            catch { return -1; }
        }
    }
}
