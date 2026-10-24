using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kehraus.Core
{
    public class DriveCard
    {
        public string Title { get; set; }
        public string Line1 { get; set; }
        public string Line2 { get; set; }
        public double UsedPercent { get; set; }
        public bool HasBar { get; set; }
    }

    /// <summary>Laufwerksübersicht: belegt/frei und wie viel davon auf Programme entfällt.</summary>
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

                    cards.Add(new DriveCard
                    {
                        Title = letter
                              + (string.IsNullOrEmpty(label) ? "" : "  " + label)
                              + (letter.Equals(sys, StringComparison.OrdinalIgnoreCase) ? "  · System" : ""),
                        UsedPercent = total > 0 ? used * 100.0 / total : 0,
                        HasBar = true,
                        Line1 = $"{Fmt.Size(used)} von {Fmt.Size(total)} belegt · {Fmt.Size(free)} frei",
                        Line2 = ProgramLine(mine),
                    });
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
                    Line2 = ProgramLine(g.ToList()),
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
                    Line2 = reg > 0 ? "laut Registry ≈ " + Fmt.Size(reg) : "keine Größenangabe",
                });
            }
            return cards;
        }

        private static string ProgramLine(List<AppEntry> mine)
        {
            if (mine.Count == 0) return "Keine Programme";
            int pending = mine.Count(a => a.IsPending);
            int notMeasured = mine.Count(a => !a.IsPending && !a.IsMeasured);
            string s = $"Programme: {Fmt.Size(DedupSum(mine))} · {mine.Count} " + (mine.Count == 1 ? "Eintrag" : "Einträge");
            if (pending > 0) s += $" · misst noch {pending}";
            else if (notMeasured > 0) s += $" · {notMeasured} ungemessen";
            return s;
        }

        /// <summary>Gleiche oder ineinander liegende Ordner nur einmal zählen.</summary>
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
}
