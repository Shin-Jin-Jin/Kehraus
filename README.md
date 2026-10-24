# Kehraus

Zeigt alle installierten Programme unter Windows – **auf welchem Laufwerk sie liegen und wie viel Platz sie wirklich belegen** – und startet auf Wunsch die Deinstallation.

## Download & Start

Unter **Actions → Build → letzter Lauf → Artefakt „Kehraus“** (bzw. unter **Releases**, sobald ein Versions-Tag gesetzt ist) liegt die fertige `Kehraus.exe`. Entpacken, starten – fertig.

Voraussetzung: Windows 10 oder 11. Kehraus läuft auf .NET Framework 4.8, das bei Windows 10/11 schon dabei ist; es muss nichts installiert werden.

## Was Kehraus anzeigt

- **Laufwerkskacheln** oben: belegt/frei pro Laufwerk und wie viel davon auf Programme entfällt.
- **Programmliste**: klassische Programme (64- und 32-Bit, alle Benutzer und aktueller Benutzer), MSI-Pakete und Store-Apps – mit Laufwerk, Größe, Version, Hersteller, Installationsdatum und Ordner.
- **Details** zum ausgewählten Eintrag: Ordner, woher der Ordner stammt, Deinstallationsbefehl, Registry-Schlüssel bzw. Paketname.
- Suche, Laufwerksfilter, CSV-Export (für Excel, Semikolon-getrennt).

## Warum die Größen stimmen

Windows zeigt unter „Apps“ die Registry-Angabe `EstimatedSize`. Die trägt der Installer einmal ein – oft gar nicht, oft falsch, und nach Updates fast nie aktualisiert. Kehraus **misst stattdessen den Installationsordner auf der Festplatte**:

1. Ordner ermitteln, in dieser Reihenfolge: `InstallLocation` → Pfad des Programmsymbols → Ort des Deinstallationsprogramms. Unterordner wie `bin` oder `1.2.3` werden auf den Programmordner hochgezogen.
2. MSI-Pakete ohne Ordnerangabe werden über die Windows-Installer-Komponenten aufgelöst (wo die Dateien des Pakets tatsächlich liegen).
3. Store-Apps kommen direkt vom Windows-Paketmanager (Name, Version, Ordner).
4. Sammelordner (`C:\Program Files`, `AppData\Local`, Laufwerkswurzel, Installer-Caches, …) werden **nie** einem einzelnen Programm zugerechnet.
5. Gemessen wird im Hintergrund, pro Laufwerk parallel; Junctions/Symlinks werden nicht verfolgt. Teilen sich Programme einen Ordner, zählt er in der Laufwerkssumme nur einmal.

Wo nichts messbar ist, steht die Registry-Angabe **grau/kursiv mit ≈** daneben – die Spalte „Grundlage“ sagt jeweils, woher die Zahl kommt.

## Deinstallieren

Eintrag wählen → **Deinstallieren** (oder Entf). Kehraus zeigt vorher den genauen Befehl und fragt nach. Es wird immer das normale (sichtbare) Deinstallationsprogramm gestartet, MSI-Pakete über `msiexec /x`, Store-Apps über den Windows-Paketmanager. Danach gleicht sich die Liste automatisch ab.

## Adminrechte

Kehraus startet ohne Adminrechte. Einzelne geschützte Ordner (z. B. manche Store-Apps) sind dann nicht vollständig messbar – das steht in der Spalte „Grundlage“. Der Knopf **Als Administrator neu starten** behebt das. Achtung: Wer sich dabei mit einem *anderen* Admin-Konto anmeldet, sieht die nur für das eigene Konto installierten Programme nicht.

## Selbst bauen

```
dotnet build src/Kehraus/Kehraus.csproj -c Release
```

oder `Kehraus.sln` in Visual Studio 2022 öffnen. Jeder Push auf `main` wird per GitHub Actions auf Windows gebaut; ein Tag `v*` (z. B. `v1.0.0`) erzeugt zusätzlich ein Release mit der ZIP-Datei.
