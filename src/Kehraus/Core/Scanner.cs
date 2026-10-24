using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kehraus.Core
{
    /// <summary>Ermittelt im Hintergrund Ordner und Größen. Die Oberfläche fragt den Fortschritt ab.</summary>
    public static class Scanner
    {
        private static CancellationTokenSource _cts;
        private static int _done, _total, _running, _indeterminate;
        private static volatile string _phase = "";
        private static volatile string _current = "";

        public static int Done => Volatile.Read(ref _done);
        public static int Total => Volatile.Read(ref _total);
        public static bool Running => Volatile.Read(ref _running) == 1;
        public static bool Indeterminate => Volatile.Read(ref _indeterminate) == 1;
        public static string Phase => _phase;
        public static string Current => _current;

        public static void Cancel() => _cts?.Cancel();

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
                        if (map.TryGetValue(a.ProductCode, out var p)) a.SetPath(p, "Windows-Installer-Komponenten");
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
            if (!cache.TryGetValue(p, out var m))
            {
                FolderSize(p, tok, out long bytes, out int files, out bool partial);
                if (tok.IsCancellationRequested) return;
                m = new[] { bytes, files, partial ? 1L : 0L };
                cache[p] = m;
            }
            if (m[2] == 1 && m[1] == 0) { a.SetNotMeasured("kein Zugriff – als Administrator starten"); return; }
            a.SetMeasured(m[0], (int)m[1], m[2] == 1);
        }

        /// <summary>
        /// Summe aller Dateigrößen. Verknüpfte Ordner (Junctions/Symlinks) werden nicht verfolgt,
        /// damit nichts doppelt oder endlos gezählt wird.
        /// </summary>
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
                        if (fsi is FileInfo fi)
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
}
