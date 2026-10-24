using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Kehraus.Core
{
    /// <summary>Store-/MSIX-Apps des aktuellen Benutzers über den Windows-Paketmanager.</summary>
    public static class StoreApps
    {
        public static List<AppEntry> Load()
        {
            var list = new List<AppEntry>();
            IEnumerable<Package> packages;
            try { packages = new PackageManager().FindPackagesForUser(string.Empty); }   // "" = aktueller Benutzer
            catch { return list; }

            foreach (var p in packages)
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.IsBundle) continue;
                    if (p.SignatureKind == PackageSignatureKind.System) continue;           // fest zu Windows gehörig

                    var id = p.Id;
                    string display = Try(() => p.DisplayName);
                    string publisher = Try(() => p.PublisherDisplayName);
                    string location = Try(() => p.InstalledLocation?.Path);
                    var v = id.Version;

                    var e = new AppEntry
                    {
                        Key = "appx:" + id.FullName,
                        Name = string.IsNullOrWhiteSpace(display) ? id.Name : display.Trim(),
                        Publisher = string.IsNullOrWhiteSpace(publisher) ? id.Publisher : publisher.Trim(),
                        Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        Kind = AppEntry.KindStore,
                        Scope = "Nur aktueller Benutzer",
                        PackageFullName = id.FullName,
                        RegistryPath = "Store-Paket " + id.FullName,
                    };
                    try { e.SetInstallDate(p.InstalledDate.LocalDateTime); } catch { }
                    if (!string.IsNullOrEmpty(location)) e.SetPath(location, "Store-Paket (InstalledLocation)");
                    list.Add(e);
                }
                catch { }
            }
            return list;
        }

        public static async Task RemoveAsync(string packageFullName)
        {
            var result = await new PackageManager().RemovePackageAsync(packageFullName);
            if (result.ExtendedErrorCode != null)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.ErrorText) ? result.ExtendedErrorCode.Message : result.ErrorText,
                    result.ExtendedErrorCode);
        }

        private static string Try(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }
}
