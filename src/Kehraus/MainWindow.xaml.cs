using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Kehraus.Core;
using Microsoft.Win32;

namespace Kehraus
{
    public partial class MainWindow : Window
    {
        private const string AllDrives = "Alle Laufwerke";
        private const string NoFolder = "ohne Ordner";

        private readonly ObservableCollection<AppEntry> _items = new ObservableCollection<AppEntry>();
        private readonly ICollectionView _view;
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _procTimer;
        private bool _suppressFilter;
        private bool _loading;
        private int _tick;
        private Process _pendingProc;
        private string _pendingName;

        public MainWindow()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            TxtMachine.Text = $"{Environment.MachineName}  ·  " + (isAdmin ? "Administrator" : "ohne Adminrechte");
            if (isAdmin) BtnAdmin.Visibility = Visibility.Collapsed;

            AppList.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.SortDescriptions.Add(new SortDescription(nameof(AppEntry.SortBytes), ListSortDirection.Descending));
            SizeColumn.SortDirection = ListSortDirection.Descending;
            _view.Filter = FilterItem;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _scanTimer.Tick += (s, e) => UpdateProgress();

            // Wartet, bis das gestartete Deinstallationsprogramm beendet ist, und gleicht dann die Liste ab
            _procTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _procTimer.Tick += async (s, e) => await CheckPendingUninstallAsync();
        }

        // ================================================================ Laden & Messen

        private async void Window_ContentRendered(object sender, EventArgs e) => await SyncProgramsAsync(full: true);

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            Scanner.Cancel();
            _scanTimer.Stop();
            _procTimer.Stop();
        }

        private static List<AppEntry> LoadAll()
        {
            var list = Inventory.LoadWin32();
            list.AddRange(StoreApps.Load());
            return list;
        }

        /// <summary>
        /// Liest die Programmliste neu. full: alles neu messen. Sonst bleiben gemessene Einträge
        /// erhalten und nur Neues wird gemessen (z. B. nach einer Deinstallation).
        /// </summary>
        private async Task SyncProgramsAsync(bool full)
        {
            if (_loading) return;
            _loading = true;
            BtnRefresh.IsEnabled = false;
            SetStatus("Lese Programmliste …");
            try
            {
                var fresh = await Task.Run(() => LoadAll());
                if (full)
                {
                    Scanner.Cancel();
                    _items.Clear();
                    foreach (var e in fresh) _items.Add(e);
                }
                else
                {
                    var known = new HashSet<string>(_items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
                    var keep = new HashSet<string>(fresh.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
                    foreach (var e in fresh.Where(f => !known.Contains(f.Key))) _items.Add(e);
                    foreach (var e in _items.Where(i => !keep.Contains(i.Key)).ToList()) _items.Remove(e);
                }

                UpdateDriveFilter();
                _view.Refresh();
                UpdateCards();
                StartMeasurement();
            }
            catch (Exception ex)
            {
                ShowError("Die Programmliste konnte nicht gelesen werden:\n\n" + ex.Message);
            }
            finally
            {
                _loading = false;
                BtnRefresh.IsEnabled = true;
            }
        }

        private void StartMeasurement()
        {
            var pending = _items.Where(i => i.IsPending).ToList();
            if (pending.Count == 0) { SetReadyStatus(); return; }
            Scanner.Start(pending);
            _scanTimer.Start();
        }

        private void UpdateProgress()
        {
            _tick++;
            if (Scanner.Running)
            {
                Prog.Visibility = Visibility.Visible;
                if (Scanner.Indeterminate)
                {
                    Prog.IsIndeterminate = true;
                    SetStatus(Scanner.Phase);
                }
                else
                {
                    Prog.IsIndeterminate = false;
                    Prog.Maximum = Math.Max(1, Scanner.Total);
                    Prog.Value = Scanner.Done;
                    SetStatus($"{Scanner.Phase} {Scanner.Done} von {Scanner.Total}  ·  {Scanner.Current}");
                }
                if (_tick % 3 == 0) UpdateCards();
            }
            else
            {
                _scanTimer.Stop();
                Prog.Visibility = Visibility.Collapsed;
                _view.Refresh();
                UpdateDriveFilter();
                UpdateCards();
                SetReadyStatus();
            }
        }

        private void UpdateCards() => DriveCards.ItemsSource = Summary.Build(_items);

        private void SetStatus(string text) => TxtStatus.Text = text;

        private void SetReadyStatus()
        {
            int measured = _items.Count(i => i.IsMeasured);
            SetStatus($"Fertig · {_items.Count} Programme · {measured} gemessen · grau/kursiv = nur Registry-Angabe · "
                    + "Doppelklick öffnet den Ordner, Entf deinstalliert");
        }

        // ================================================================ Filter

        private bool FilterItem(object o)
        {
            var a = (AppEntry)o;
            if (a.IsStore && ChkStore.IsChecked != true) return false;

            var d = CmbDrive.SelectedItem as string;
            if (d == NoFolder) { if (a.Drive != "?") return false; }
            else if (!string.IsNullOrEmpty(d) && d != AllDrives && a.Drive != d) return false;

            string q = TxtSearch.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Has(a.Name, q) || Has(a.Publisher, q) || Has(a.InstallPath, q);
        }

        private static bool Has(string field, string q) =>
            field != null && field.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private void UpdateDriveFilter()
        {
            string selected = CmbDrive.SelectedItem as string;
            var drives = _items.Select(i => i.Drive).Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            var entries = new List<string> { AllDrives };
            entries.AddRange(drives.Where(d => d != "?"));
            if (drives.Contains("?")) entries.Add(NoFolder);

            _suppressFilter = true;
            CmbDrive.ItemsSource = entries;
            CmbDrive.SelectedItem = selected != null && entries.Contains(selected) ? selected : AllDrives;
            _suppressFilter = false;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

        private void CmbDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressFilter) _view?.Refresh();
        }

        // ================================================================ Aktionen

        private AppEntry Selected => AppList.SelectedItem as AppEntry;

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await SyncProgramsAsync(full: true);

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e) => await UninstallAsync(Selected);

        private void BtnOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(Selected);

        private void BtnExport_Click(object sender, RoutedEventArgs e) => ExportCsv();

        private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Nur Doppelklicks auf eine Zeile, nicht auf den Spaltenkopf
            if (ItemsControl.ContainerFromElement(AppList, e.OriginalSource as DependencyObject) is DataGridRow)
                OpenFolder(Selected);
        }

        private async void AppList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            await UninstallAsync(Selected);
        }

        private void BtnAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception)
            {
                // UAC-Abfrage abgebrochen – einfach weiterarbeiten
            }
        }

        private void OpenFolder(AppEntry a)
        {
            if (a?.InstallPath == null || !Directory.Exists(a.InstallPath)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + a.InstallPath + "\"") { UseShellExecute = true });
        }

        private async Task UninstallAsync(AppEntry a)
        {
            if (a == null) return;

            UninstallCommand cmd = null;
            string detail;
            if (a.IsStore)
            {
                detail = "Store-App entfernen:\n" + a.PackageFullName;
            }
            else
            {
                cmd = Uninstaller.GetCommand(a);
                if (cmd == null)
                {
                    MessageBox.Show(this, $"Für „{a.Name}“ ist kein Deinstallationsbefehl hinterlegt.", "Kehraus",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                detail = "Befehl:\n" + cmd.Display;
            }

            string size = a.IsMeasured ? $" ({a.SizeText})" : "";
            var answer = MessageBox.Show(this, $"Soll „{a.Name}“{size} wirklich deinstalliert werden?\n\n{detail}",
                                         "Kehraus – Deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            if (a.IsStore)
            {
                SetStatus($"Entferne „{a.Name}“ …");
                Cursor = Cursors.Wait;
                try
                {
                    await StoreApps.RemoveAsync(a.PackageFullName);
                }
                catch (Exception ex)
                {
                    ShowError($"„{a.Name}“ konnte nicht entfernt werden:\n\n{ex.Message}");
                    SetReadyStatus();
                    return;
                }
                finally
                {
                    Cursor = null;
                }
                await SyncProgramsAsync(full: false);
                SetStatus($"„{a.Name}“ wurde entfernt.");
                return;
            }

            try
            {
                _pendingProc = Uninstaller.Start(cmd);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                SetStatus("Deinstallation abgebrochen (Administratorabfrage abgelehnt).");
                return;
            }
            catch (Exception ex)
            {
                ShowError("Das Deinstallationsprogramm konnte nicht gestartet werden:\n\n" + ex.Message);
                return;
            }
            _pendingName = a.Name;
            _procTimer.Start();
            SetStatus($"Deinstallation von „{a.Name}“ läuft … die Liste aktualisiert sich danach automatisch.");
        }

        private async Task CheckPendingUninstallAsync()
        {
            bool running;
            try { running = _pendingProc != null && !_pendingProc.HasExited; }
            catch { running = false; }
            if (running) return;

            _procTimer.Stop();
            _pendingProc = null;
            await SyncProgramsAsync(full: false);
            SetStatus($"Deinstallationsprogramm von „{_pendingName}“ beendet – Liste abgeglichen. "
                    + "Falls es im Hintergrund weiterläuft: „Aktualisieren“.");
        }

        private void ExportCsv()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Liste exportieren",
                Filter = "CSV für Excel (*.csv)|*.csv",
                FileName = $"Kehraus_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd}.csv",
            };
            if (dlg.ShowDialog(this) != true) return;

            var de = Fmt.De;
            string Mb(long b) => b >= 0 ? (b / 1048576.0).ToString("0.0", de) : "";

            var sb = new StringBuilder();
            AppendRow(sb, "Name", "Version", "Hersteller", "Laufwerk", "Größe (MB)", "Grundlage", "Registry-Angabe (MB)",
                      "Dateien", "Installiert", "Typ", "Bereich", "Installationsordner", "Ordner ermittelt über",
                      "Deinstallationsbefehl", "Registry / Paket");
            int count = 0;
            foreach (AppEntry o in _view)
            {
                AppendRow(sb, o.Name, o.Version, o.Publisher, o.Drive, Mb(o.SortBytes), o.SizeSource, Mb(o.RegistryBytes),
                          o.IsMeasured ? o.FileCount.ToString(CultureInfo.InvariantCulture) : "", o.InstallDateText,
                          o.Kind, o.Scope, o.InstallPath, o.PathSource, o.UninstallString, o.RegistryPath);
                count++;
            }

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));   // mit BOM, damit Excel Umlaute erkennt
                SetStatus($"{count} Einträge exportiert nach {dlg.FileName}");
            }
            catch (Exception ex)
            {
                ShowError("Die Datei konnte nicht geschrieben werden:\n\n" + ex.Message);
            }
        }

        private static void AppendRow(StringBuilder sb, params string[] fields)
        {
            sb.AppendLine(string.Join(";", fields.Select(CsvField)));
        }

        private static string CsvField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool quote = s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0;
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private void ShowError(string message) =>
            MessageBox.Show(this, message, "Kehraus", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
