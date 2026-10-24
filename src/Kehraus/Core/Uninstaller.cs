using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Kehraus.Core
{
    public sealed class UninstallCommand
    {
        public string File { get; set; }
        public string Arguments { get; set; }
        public string Display { get; set; }
    }

    /// <summary>Zerlegt und startet den Deinstallationsbefehl aus der Registry.</summary>
    public static class Uninstaller
    {
        private static readonly Regex MsiCommand =
            new Regex(@"^""?[^""]*msiexec(\.exe)?""?\s+/[IX]\s*(\{[0-9A-F-]{36}\})", RegexOptions.IgnoreCase);

        private static string SystemFile(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

        public static UninstallCommand GetCommand(AppEntry a)
        {
            string s = a.UninstallString ?? "";

            // MSI immer über den Produktcode – unabhängig davon, ob der Eintrag /I oder /X enthält
            if (a.ProductCode != null && (a.Kind == AppEntry.KindMsi || s.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0))
                return Msi(a.ProductCode);
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = Environment.ExpandEnvironmentVariables(s.Trim());

            var m = MsiCommand.Match(s);
            if (m.Success) return Msi(m.Groups[2].Value);

            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end > 1)
                    return new UninstallCommand { File = s.Substring(1, end - 1), Arguments = s.Substring(end + 1).Trim(), Display = s };
            }

            // Ungequoteter Pfad mit Leerzeichen: kürzesten existierenden Dateipfad suchen
            string[] parts = s.Split(' ');
            for (int i = 1; i <= parts.Length; i++)
            {
                string candidate = string.Join(" ", parts, 0, i);
                bool isFile;
                try { isFile = System.IO.File.Exists(candidate); } catch { isFile = false; }
                if (isFile)
                {
                    string rest = i < parts.Length ? string.Join(" ", parts, i, parts.Length - i) : "";
                    return new UninstallCommand { File = candidate, Arguments = rest.Trim(), Display = s };
                }
            }

            // Letzter Ausweg: so ausführen, wie Windows es täte
            return new UninstallCommand { File = SystemFile("cmd.exe"), Arguments = "/c \"" + s + "\"", Display = s };
        }

        private static UninstallCommand Msi(string productCode) => new UninstallCommand
        {
            File = SystemFile("msiexec.exe"),
            Arguments = "/x " + productCode,
            Display = "msiexec.exe /x " + productCode,
        };

        /// <summary>Startet das sichtbare Deinstallationsprogramm. Wirft Win32Exception 1223, wenn die UAC-Abfrage abgelehnt wird.</summary>
        public static Process Start(UninstallCommand cmd)
        {
            var psi = new ProcessStartInfo(cmd.File) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(cmd.Arguments)) psi.Arguments = cmd.Arguments;
            return Process.Start(psi);
        }
    }
}
