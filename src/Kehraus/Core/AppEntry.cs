using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace Kehraus.Core
{
    public static class Fmt
    {
        public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

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

    /// <summary>Ein installiertes Programm samt Speicherort und Messergebnis.</summary>
    public class AppEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(params string[] names)
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            foreach (var n in names) handler(this, new PropertyChangedEventArgs(n));
        }

        public const string KindWin32 = "Win32";
        public const string KindMsi = "MSI";
        public const string KindStore = "Store";

        public string Key { get; set; }                  // eindeutig: Registry-Pfad bzw. appx:Paketname
        public string Name { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Kind { get; set; }                 // Win32 | MSI | Store
        public string Scope { get; set; }
        public string RegistryPath { get; set; }
        public string ProductCode { get; set; }          // {GUID}, falls vorhanden
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }
        public string PackageFullName { get; set; }      // nur Store-Apps

        public bool IsStore => Kind == KindStore;

        public long RegistryBytes { get; set; } = -1;
        public string RegistrySizeText => Fmt.Size(RegistryBytes);

        // ---------------------------------------------------------------- Installationsdatum

        public string InstallDateText { get; private set; } = "";
        public int InstallDateSort { get; private set; }

        public void SetInstallDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy", "M/d/yyyy", "MM/dd/yyyy" };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                SetInstallDate(dt);
        }

        public void SetInstallDate(DateTime dt)
        {
            if (dt.Year <= 1990 || dt.Year >= 2100) return;
            InstallDateText = dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            InstallDateSort = dt.Year * 10000 + dt.Month * 100 + dt.Day;
        }

        // ---------------------------------------------------------------- Installationsordner

        private volatile string _path;
        private volatile string _pathSource;
        public string InstallPath => _path;
        public string PathSource => _pathSource;

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
            Raise(nameof(InstallPath), nameof(PathSource), nameof(Drive));
        }

        // ---------------------------------------------------------------- Messung
        // 0 = wartet, 1 = gemessen, 2 = nicht messbar

        private volatile int _state;
        private long _measured = -1;
        private int _files;
        private bool _partial;
        private string _reason;

        public bool IsPending => _state == 0;
        public bool IsMeasured => _state == 1;
        public bool IsEstimate => _state != 1;
        public long MeasuredBytes => _state == 1 ? _measured : -1;
        public int FileCount => _state == 1 ? _files : 0;

        /// <summary>Sortierwert: gemessen, sonst Registry-Angabe, sonst -1.</summary>
        public long SortBytes => _state == 1 ? _measured : (RegistryBytes >= 0 ? RegistryBytes : -1);

        public string SizeText
        {
            get
            {
                switch (_state)
                {
                    case 0: return "…";
                    case 1: return Fmt.Size(_measured);
                    default: return RegistryBytes >= 0 ? "≈ " + Fmt.Size(RegistryBytes) : "—";
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
                    default: return (RegistryBytes >= 0 ? "Registry-Angabe – " : "unbekannt – ") + _reason;
                }
            }
        }

        public string FilesText => _state == 1 ? _files.ToString("N0", Fmt.De) + " Dateien" : "";

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
            Raise(nameof(SizeText), nameof(SizeSource), nameof(SortBytes), nameof(IsEstimate), nameof(IsPending),
                  nameof(IsMeasured), nameof(MeasuredBytes), nameof(FileCount), nameof(FilesText));
        }
    }
}
