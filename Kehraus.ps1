<#
    Kehraus  ·  Programme, Speicherorte und echte Größen
    =====================================================
    Zeigt alle installierten Programme (klassische Programme, MSI-Pakete und Store-Apps),
    auf welchem Laufwerk sie liegen und wie viel Platz ihr Installationsordner tatsächlich
    belegt – und startet auf Wunsch die Deinstallation.

    Die Größe wird direkt auf der Festplatte gemessen. Die Registry-Angabe "EstimatedSize"
    (die auch Windows unter "Apps" anzeigt) ist oft veraltet oder schlicht falsch; sie
    erscheint nur zum Vergleich bzw. grau/kursiv als Schätzung, wenn kein Ordner ermittelbar ist.

    Start:  Kehraus.cmd doppelklicken
            oder  powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File Kehraus.ps1
#>

$ErrorActionPreference = 'Stop'
$script:ScriptPath = $PSCommandPath
$script:ScriptDir  = $PSScriptRoot
$script:HostExe    = (Get-Process -Id $PID).Path

# WPF braucht einen STA-Thread – sonst neu starten
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    Start-Process -FilePath $script:HostExe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', "`"$script:ScriptPath`"")
    return
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

trap {
    [void][Windows.MessageBox]::Show(('Kehraus wurde wegen eines Fehlers beendet:{0}{0}{1}' -f [Environment]::NewLine, $_.Exception.Message), 'Kehraus', 'OK', 'Error')
    break
}

# ---------------------------------------------------------------------------
#  Kern (C#) laden
# ---------------------------------------------------------------------------
if (-not ('Kehraus.AppEntry' -as [type])) {
    $corePath = Join-Path $script:ScriptDir 'Kehraus.Core.cs'
    if (-not (Test-Path -LiteralPath $corePath)) { throw "Kehraus.Core.cs fehlt – sie muss neben Kehraus.ps1 liegen." }
    $source = [IO.File]::ReadAllText($corePath, [Text.Encoding]::UTF8)
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -TypeDefinition $source -Language CSharp
    } else {
        Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies 'System.Core'
    }
}

# ---------------------------------------------------------------------------
#  Oberfläche
# ---------------------------------------------------------------------------
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Kehraus" Width="1340" Height="860" MinWidth="980" MinHeight="580"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13"
        Background="#F3F4F6" UseLayoutRounding="True">
  <Window.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    <Style TargetType="Button">
      <Setter Property="Padding" Value="12,5"/>
      <Setter Property="Margin" Value="6,0,0,0"/>
    </Style>
    <Style x:Key="InfoLabel" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#5B6672"/>
      <Setter Property="Margin" Value="0,2,14,2"/>
      <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
    <Style x:Key="InfoValue" TargetType="TextBox">
      <Setter Property="IsReadOnly" Value="True"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Padding" Value="0"/>
      <Setter Property="Margin" Value="0,2"/>
      <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
    <Style x:Key="RightCell" TargetType="TextBlock">
      <Setter Property="HorizontalAlignment" Value="Right"/>
      <Setter Property="Margin" Value="0,0,6,0"/>
    </Style>
  </Window.Resources>

  <DockPanel>
    <!-- Kopfzeile -->
    <Border DockPanel.Dock="Top" Background="#1E2833" Padding="18,12">
      <DockPanel>
        <TextBlock x:Name="TxtMachine" DockPanel.Dock="Right" Foreground="#C9D3DD" VerticalAlignment="Center"/>
        <StackPanel>
          <TextBlock Text="Kehraus" Foreground="White" FontSize="22" FontWeight="SemiBold"/>
          <TextBlock Foreground="#AEBBC8" Text="Installierte Programme – wo sie liegen und wie viel Platz sie wirklich belegen"/>
        </StackPanel>
      </DockPanel>
    </Border>

    <!-- Laufwerke -->
    <ItemsControl x:Name="DriveCards" DockPanel.Dock="Top" Margin="12,12,2,2">
      <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
      </ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Border Background="White" BorderBrush="#D8DDE3" BorderThickness="1" CornerRadius="6"
                  Padding="12,9" Margin="0,0,10,10" Width="320">
            <StackPanel>
              <TextBlock Text="{Binding Title, Mode=OneWay}" FontWeight="SemiBold" FontSize="14" TextTrimming="CharacterEllipsis"/>
              <ProgressBar Height="7" Margin="0,7,0,7" Maximum="100" Value="{Binding UsedPercent, Mode=OneWay}"
                           Visibility="{Binding HasBar, Mode=OneWay, Converter={StaticResource BoolToVis}}"/>
              <TextBlock Text="{Binding Line1, Mode=OneWay}" Foreground="#4A5561" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Line2, Mode=OneWay}" Foreground="#1E2833" FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,2,0,0"/>
            </StackPanel>
          </Border>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>

    <!-- Werkzeugleiste -->
    <Grid DockPanel.Dock="Top" Margin="12,0,12,10">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="Suchen" VerticalAlignment="Center" Margin="0,0,6,0"/>
        <TextBox x:Name="TxtSearch" Width="250" Padding="4,3" VerticalContentAlignment="Center"
                 ToolTip="Sucht in Name, Hersteller und Ordner"/>
        <TextBlock Text="Laufwerk" VerticalAlignment="Center" Margin="16,0,6,0"/>
        <ComboBox x:Name="CmbDrive" Width="150"/>
        <CheckBox x:Name="ChkStore" Content="Store-Apps anzeigen" IsChecked="True" VerticalAlignment="Center" Margin="16,0,0,0"/>
      </StackPanel>
      <StackPanel Grid.Column="1" Orientation="Horizontal">
        <Button x:Name="BtnAdmin" Content="Als Administrator neu starten"
                ToolTip="Misst auch geschützte Ordner vollständig. Hinweis: Ein anderes Admin-Konto sieht die Programme, die nur für dein Benutzerkonto installiert sind, nicht."/>
        <Button x:Name="BtnExport" Content="CSV exportieren"/>
        <Button x:Name="BtnRefresh" Content="Aktualisieren"/>
        <Button x:Name="BtnOpen" Content="Ordner öffnen"/>
        <Button x:Name="BtnUninstall" Content="Deinstallieren" Background="#C0392B" Foreground="White" FontWeight="SemiBold"/>
      </StackPanel>
    </Grid>

    <!-- Statuszeile -->
    <Border DockPanel.Dock="Bottom" Background="#E4E7EB" Padding="12,6">
      <DockPanel>
        <ProgressBar x:Name="Prog" DockPanel.Dock="Right" Width="180" Height="10" Visibility="Collapsed" Margin="12,0,0,0"/>
        <TextBlock x:Name="TxtStatus" Text="Lese Programmliste …" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
      </DockPanel>
    </Border>

    <!-- Details zum ausgewählten Programm -->
    <Border DockPanel.Dock="Bottom" Background="White" BorderBrush="#D8DDE3" BorderThickness="1" CornerRadius="6"
            Margin="12,0,12,10" Padding="12,8" DataContext="{Binding SelectedItem, ElementName=List}">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
          <RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/>
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Style="{StaticResource InfoLabel}" Text="Ordner"/>
        <TextBox   Grid.Row="0" Grid.Column="1" Style="{StaticResource InfoValue}" Text="{Binding InstallPath, Mode=OneWay}"/>
        <TextBlock Grid.Row="1" Style="{StaticResource InfoLabel}" Text="Ermittelt über"/>
        <TextBox   Grid.Row="1" Grid.Column="1" Style="{StaticResource InfoValue}" Text="{Binding PathSource, Mode=OneWay}"/>
        <TextBlock Grid.Row="2" Style="{StaticResource InfoLabel}" Text="Messung"/>
        <StackPanel Grid.Row="2" Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
          <TextBlock Text="{Binding SizeSource, Mode=OneWay}"/>
          <TextBlock Text="{Binding FilesText, Mode=OneWay}" Foreground="#5B6672" Margin="12,0,0,0"/>
        </StackPanel>
        <TextBlock Grid.Row="3" Style="{StaticResource InfoLabel}" Text="Deinstallation"/>
        <TextBox   Grid.Row="3" Grid.Column="1" Style="{StaticResource InfoValue}" Text="{Binding UninstallString, Mode=OneWay}"/>
        <TextBlock Grid.Row="4" Style="{StaticResource InfoLabel}" Text="Registry / Paket"/>
        <TextBox   Grid.Row="4" Grid.Column="1" Style="{StaticResource InfoValue}" Text="{Binding RegistryPath, Mode=OneWay}"/>
      </Grid>
    </Border>

    <!-- Programmliste -->
    <DataGrid x:Name="List" Margin="12,0,12,10" AutoGenerateColumns="False" IsReadOnly="True"
              SelectionMode="Single" SelectionUnit="FullRow" HeadersVisibility="Column"
              GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#E6E9ED"
              AlternatingRowBackground="#FAFBFC" Background="White" BorderBrush="#D8DDE3"
              RowHeight="26" CanUserResizeRows="False" EnableRowVirtualization="True">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Name" Binding="{Binding Name, Mode=OneWay}" Width="2.2*"/>
        <DataGridTextColumn Header="Laufwerk" Binding="{Binding Drive, Mode=OneWay}" Width="70"/>
        <DataGridTextColumn Binding="{Binding SizeText, Mode=OneWay}" SortMemberPath="SortBytes" Width="95">
          <DataGridTextColumn.Header>
            <TextBlock Text="Größe" ToolTip="Auf der Festplatte gemessen. Grau/kursiv mit ≈: nur Registry-Angabe, nicht gemessen."/>
          </DataGridTextColumn.Header>
          <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
              <Setter Property="HorizontalAlignment" Value="Right"/>
              <Setter Property="Margin" Value="0,0,6,0"/>
              <Setter Property="FontWeight" Value="SemiBold"/>
              <Style.Triggers>
                <DataTrigger Binding="{Binding IsEstimate, Mode=OneWay}" Value="True">
                  <Setter Property="Foreground" Value="#8A939C"/>
                  <Setter Property="FontWeight" Value="Normal"/>
                  <Setter Property="FontStyle" Value="Italic"/>
                </DataTrigger>
              </Style.Triggers>
            </Style>
          </DataGridTextColumn.ElementStyle>
        </DataGridTextColumn>
        <DataGridTextColumn Header="Grundlage" Binding="{Binding SizeSource, Mode=OneWay}" Width="1.3*"/>
        <DataGridTextColumn Header="Registry-Angabe" Binding="{Binding RegistrySizeText, Mode=OneWay}" SortMemberPath="RegistryBytes"
                            Width="105" ElementStyle="{StaticResource RightCell}"/>
        <DataGridTextColumn Header="Version" Binding="{Binding Version, Mode=OneWay}" Width="100"/>
        <DataGridTextColumn Header="Hersteller" Binding="{Binding Publisher, Mode=OneWay}" Width="1.2*"/>
        <DataGridTextColumn Header="Installiert" Binding="{Binding InstallDateText, Mode=OneWay}" SortMemberPath="InstallDateSort" Width="82"/>
        <DataGridTextColumn Header="Typ" Binding="{Binding Kind, Mode=OneWay}" Width="52"/>
        <DataGridTextColumn Header="Bereich" Binding="{Binding Scope, Mode=OneWay}" Width="160"/>
        <DataGridTextColumn Header="Installationsordner" Binding="{Binding InstallPath, Mode=OneWay}" Width="2*"/>
      </DataGrid.Columns>
    </DataGrid>
  </DockPanel>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Parse($xaml)
$ui = @{}
foreach ($name in 'List', 'DriveCards', 'TxtSearch', 'CmbDrive', 'ChkStore', 'BtnUninstall', 'BtnOpen',
                  'BtnRefresh', 'BtnExport', 'BtnAdmin', 'TxtStatus', 'TxtMachine', 'Prog') {
    $ui[$name] = $window.FindName($name)
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$script:IsAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$ui.TxtMachine.Text = '{0}  ·  {1}' -f $env:COMPUTERNAME, $(if ($script:IsAdmin) { 'Administrator' } else { 'ohne Adminrechte' })
if ($script:IsAdmin) { $ui.BtnAdmin.Visibility = 'Collapsed' }

# ---------------------------------------------------------------------------
#  Daten, Ansicht, Filter
# ---------------------------------------------------------------------------
$script:Items = New-Object 'System.Collections.ObjectModel.ObservableCollection[Kehraus.AppEntry]'
$ui.List.ItemsSource = $script:Items
$script:View = [Windows.Data.CollectionViewSource]::GetDefaultView($script:Items)
$script:View.SortDescriptions.Add([ComponentModel.SortDescription]::new('SortBytes', [ComponentModel.ListSortDirection]::Descending))
$ui.List.Columns[2].SortDirection = [ComponentModel.ListSortDirection]::Descending

$script:SuppressFilter = $false
$script:StoreNameCache = @{}
$script:PendingProc    = $null
$script:PendingName    = ''
$script:Tick           = 0

$script:View.Filter = [Predicate[object]] {
    param($o)
    if ($o.Kind -eq 'Store' -and -not $ui.ChkStore.IsChecked) { return $false }
    $d = [string]$ui.CmbDrive.SelectedItem
    if ($d -eq 'ohne Ordner') {
        if ($o.Drive -ne '?') { return $false }
    } elseif ($d -and $d -ne 'Alle Laufwerke' -and $o.Drive -ne $d) {
        return $false
    }
    $q = $ui.TxtSearch.Text
    if (-not [string]::IsNullOrWhiteSpace($q)) {
        $q = $q.Trim()
        foreach ($field in @($o.Name, $o.Publisher, $o.InstallPath)) {
            if ($field -and $field.IndexOf($q, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------
#  Hilfsfunktionen
# ---------------------------------------------------------------------------
function Show-Error($err) {
    $msg = if ($err -is [Management.Automation.ErrorRecord]) { $err.Exception.Message } else { [string]$err }
    [void][Windows.MessageBox]::Show($window, $msg, 'Kehraus', 'OK', 'Error')
}

function Invoke-Safe([scriptblock]$Action) {
    try { & $Action } catch { Show-Error $_ }
}

function Get-StoreApps {
    $result = New-Object 'System.Collections.Generic.List[Kehraus.AppEntry]'
    try { $packages = @(Get-AppxPackage -PackageTypeFilter Main -ErrorAction Stop) } catch { return $result }

    foreach ($p in $packages) {
        try {
            if ($p.SignatureKind -eq 'System' -or $p.NonRemovable -or $p.IsFramework) { continue }

            $names = $script:StoreNameCache[$p.PackageFullName]
            if (-not $names) {
                $display = $null; $publisher = $null
                if ($p.InstallLocation) {
                    $manifestPath = Join-Path $p.InstallLocation 'AppxManifest.xml'
                    if (Test-Path -LiteralPath $manifestPath) {
                        try {
                            $m = New-Object Xml.XmlDocument
                            $m.Load($manifestPath)
                            $display   = [Kehraus.Native]::ResolveStoreName($p.PackageFullName, $p.Name, [string]$m.Package.Properties.DisplayName)
                            $publisher = [Kehraus.Native]::ResolveStoreName($p.PackageFullName, $p.Name, [string]$m.Package.Properties.PublisherDisplayName)
                        } catch { }
                    }
                }
                if (-not $display)   { $display = $p.Name }
                if (-not $publisher) { $publisher = ([string]$p.Publisher) -replace '^CN=([^,]+).*$', '$1' }
                $names = @($display, $publisher)
                $script:StoreNameCache[$p.PackageFullName] = $names
            }

            $e = New-Object Kehraus.AppEntry
            $e.Key             = 'appx:' + $p.PackageFullName
            $e.Name            = $names[0]
            $e.Publisher       = $names[1]
            $e.Version         = [string]$p.Version
            $e.Kind            = 'Store'
            $e.Scope           = 'Nur aktueller Benutzer'
            $e.PackageFullName = $p.PackageFullName
            $e.RegistryPath    = 'Store-Paket ' + $p.PackageFullName
            if ($p.InstallLocation) { $e.SetPath([string]$p.InstallLocation, 'Store-Paket (InstallLocation)') }
            $result.Add($e)
        } catch { }
    }
    $result
}

function Get-AllEntries {
    $list = New-Object 'System.Collections.Generic.List[Kehraus.AppEntry]'
    foreach ($e in [Kehraus.Inventory]::LoadWin32()) { $list.Add($e) }
    foreach ($e in (Get-StoreApps)) { $list.Add($e) }
    , $list
}

function Update-DriveFilter {
    $selected = [string]$ui.CmbDrive.SelectedItem
    $drives = @($script:Items | ForEach-Object { $_.Drive } | Sort-Object -Unique)
    $entries = @('Alle Laufwerke') + @($drives | Where-Object { $_ -ne '?' })
    if ($drives -contains '?') { $entries += 'ohne Ordner' }
    $script:SuppressFilter = $true
    $ui.CmbDrive.ItemsSource = $entries
    $ui.CmbDrive.SelectedItem = if ($selected -and $entries -contains $selected) { $selected } else { 'Alle Laufwerke' }
    $script:SuppressFilter = $false
}

function Update-Cards {
    $ui.DriveCards.ItemsSource = [Kehraus.Summary]::Build($script:Items)
}

function Set-ReadyStatus {
    $all = @($script:Items)
    $measured = @($all | Where-Object { $_.IsMeasured }).Count
    $ui.TxtStatus.Text = ('Fertig · {0} Programme · {1} gemessen · grau/kursiv = nur Registry-Angabe · Doppelklick öffnet den Ordner, Entf deinstalliert' -f $all.Count, $measured)
}

function Start-Measurement {
    $pending = New-Object 'System.Collections.Generic.List[Kehraus.AppEntry]'
    foreach ($e in $script:Items) { if ($e.IsPending) { $pending.Add($e) } }
    if ($pending.Count -gt 0) {
        [Kehraus.Scanner]::Start($pending)
        $script:ScanTimer.Start()
    } else {
        Set-ReadyStatus
    }
}

# Liest die Programmliste neu. -Full: alles neu messen.
# Ohne -Full bleiben bereits gemessene Einträge erhalten, nur Neues wird gemessen.
function Sync-Programs([switch]$Full) {
    $ui.TxtStatus.Text = 'Lese Programmliste …'
    $fresh = Get-AllEntries
    if ($Full) {
        [Kehraus.Scanner]::Cancel()
        $script:Items.Clear()
        foreach ($e in $fresh) { $script:Items.Add($e) }
    } else {
        $known = @{}
        foreach ($e in $script:Items) { $known[$e.Key] = $true }
        $keep = @{}
        foreach ($e in $fresh) {
            $keep[$e.Key] = $true
            if (-not $known.ContainsKey($e.Key)) { $script:Items.Add($e) }
        }
        foreach ($e in @($script:Items)) {
            if (-not $keep.ContainsKey($e.Key)) { [void]$script:Items.Remove($e) }
        }
    }
    Update-DriveFilter
    $script:View.Refresh()
    Update-Cards
    Start-Measurement
}

function Update-Progress {
    $script:Tick++
    if ([Kehraus.Scanner]::Running) {
        $ui.Prog.Visibility = 'Visible'
        if ([Kehraus.Scanner]::Indeterminate) {
            $ui.Prog.IsIndeterminate = $true
            $ui.TxtStatus.Text = [Kehraus.Scanner]::Phase
        } else {
            $done  = [Kehraus.Scanner]::Done
            $total = [Kehraus.Scanner]::Total
            $ui.Prog.IsIndeterminate = $false
            $ui.Prog.Maximum = [Math]::Max(1, $total)
            $ui.Prog.Value   = $done
            $ui.TxtStatus.Text = ('{0} {1} von {2}  ·  {3}' -f [Kehraus.Scanner]::Phase, $done, $total, [Kehraus.Scanner]::Current)
        }
        if ($script:Tick % 3 -eq 0) { Update-Cards }
    } else {
        $script:ScanTimer.Stop()
        $ui.Prog.Visibility = 'Collapsed'
        $script:View.Refresh()
        Update-DriveFilter
        Update-Cards
        Set-ReadyStatus
    }
}

# Zerlegt den Deinstallationsbefehl aus der Registry in Programm + Argumente
function Get-UninstallCommand($a) {
    $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $s = [string]$a.UninstallString

    if ($a.ProductCode -and ($a.Kind -eq 'MSI' -or $s -match '(?i)msiexec')) {
        return [pscustomobject]@{ File = $msiexec; Args = "/x $($a.ProductCode)"; Text = "msiexec.exe /x $($a.ProductCode)" }
    }
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $s = [Environment]::ExpandEnvironmentVariables($s.Trim())

    if ($s -match '(?i)^"?[^"]*msiexec(\.exe)?"?\s+/[IX]\s*(\{[0-9A-F-]{36}\})') {
        $code = $Matches[2]
        return [pscustomobject]@{ File = $msiexec; Args = "/x $code"; Text = "msiexec.exe /x $code" }
    }

    if ($s.StartsWith('"')) {
        $end = $s.IndexOf('"', 1)
        if ($end -gt 1) {
            return [pscustomobject]@{ File = $s.Substring(1, $end - 1); Args = $s.Substring($end + 1).Trim(); Text = $s }
        }
    }

    # Ungequoteter Pfad mit Leerzeichen: kürzesten existierenden Dateipfad suchen
    $parts = $s -split ' '
    for ($i = 1; $i -le $parts.Count; $i++) {
        $candidate = $parts[0..($i - 1)] -join ' '
        $isFile = $false
        try { $isFile = Test-Path -LiteralPath $candidate -PathType Leaf } catch { }
        if ($isFile) {
            $rest = if ($i -lt $parts.Count) { $parts[$i..($parts.Count - 1)] -join ' ' } else { '' }
            return [pscustomobject]@{ File = $candidate; Args = $rest.Trim(); Text = $s }
        }
    }

    # Letzter Ausweg: so ausführen, wie Windows es täte
    return [pscustomobject]@{ File = (Join-Path $env:SystemRoot 'System32\cmd.exe'); Args = "/c `"$s`""; Text = $s }
}

function Invoke-Uninstall($a) {
    if (-not $a) { return }

    if ($a.Kind -eq 'Store') {
        $detail = 'Store-App entfernen:' + [Environment]::NewLine + $a.PackageFullName
    } else {
        $cmd = Get-UninstallCommand $a
        if (-not $cmd) {
            [void][Windows.MessageBox]::Show($window, ('Für „{0}“ ist kein Deinstallationsbefehl hinterlegt.' -f $a.Name), 'Kehraus', 'OK', 'Warning')
            return
        }
        $detail = 'Befehl:' + [Environment]::NewLine + $cmd.Text
    }

    $size = if ($a.IsMeasured) { ' ({0})' -f $a.SizeText } else { '' }
    $question = 'Soll „{0}“{1} wirklich deinstalliert werden?{2}{2}{3}' -f $a.Name, $size, [Environment]::NewLine, $detail
    $answer = [Windows.MessageBox]::Show($window, $question, 'Kehraus – Deinstallieren', 'YesNo', 'Warning')
    if ($answer -ne [Windows.MessageBoxResult]::Yes) { return }

    if ($a.Kind -eq 'Store') {
        $window.Cursor = [Windows.Input.Cursors]::Wait
        try { Remove-AppxPackage -Package $a.PackageFullName -ErrorAction Stop }
        finally { $window.Cursor = $null }
        Sync-Programs
        $ui.TxtStatus.Text = ('„{0}“ wurde entfernt.' -f $a.Name)
        return
    }

    $startArgs = @{ FilePath = $cmd.File; PassThru = $true }
    if ($cmd.Args) { $startArgs.ArgumentList = $cmd.Args }
    try {
        $script:PendingProc = Start-Process @startArgs
    } catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        if ($inner -is [ComponentModel.Win32Exception] -and $inner.NativeErrorCode -eq 1223) {
            $ui.TxtStatus.Text = 'Deinstallation abgebrochen (Administratorabfrage abgelehnt).'
            return
        }
        throw
    }
    $script:PendingName = $a.Name
    $script:ProcTimer.Start()
    $ui.TxtStatus.Text = ('Deinstallation von „{0}“ läuft … die Liste aktualisiert sich danach automatisch.' -f $a.Name)
}

function Open-Folder($a) {
    if ($a -and $a.InstallPath -and (Test-Path -LiteralPath $a.InstallPath)) {
        Start-Process -FilePath 'explorer.exe' -ArgumentList ('"{0}"' -f $a.InstallPath)
    }
}

function Export-List {
    $dlg = New-Object Microsoft.Win32.SaveFileDialog
    $dlg.Title    = 'Liste exportieren'
    $dlg.Filter   = 'CSV für Excel (*.csv)|*.csv'
    $dlg.FileName = 'Kehraus_{0}_{1:yyyy-MM-dd}.csv' -f $env:COMPUTERNAME, (Get-Date)
    if ($dlg.ShowDialog($window) -ne $true) { return }

    $de = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
    $mb = { param($b) if ($b -ge 0) { ($b / 1MB).ToString('0.0', $de) } else { '' } }
    $rows = foreach ($o in $script:View) {
        [pscustomobject]@{
            'Name'                  = $o.Name
            'Version'               = $o.Version
            'Hersteller'            = $o.Publisher
            'Laufwerk'              = $o.Drive
            'Größe (MB)'            = & $mb $o.SortBytes
            'Grundlage'             = $o.SizeSource
            'Registry-Angabe (MB)'  = & $mb $o.RegistryBytes
            'Dateien'               = $(if ($o.IsMeasured) { $o.FileCount } else { '' })
            'Installiert'           = $o.InstallDateText
            'Typ'                   = $o.Kind
            'Bereich'               = $o.Scope
            'Installationsordner'   = $o.InstallPath
            'Ordner ermittelt über' = $o.PathSource
            'Deinstallationsbefehl' = $o.UninstallString
            'Registry / Paket'      = $o.RegistryPath
        }
    }
    $encoding = if ($PSVersionTable.PSEdition -eq 'Core') { 'utf8BOM' } else { 'UTF8' }
    $rows | Export-Csv -LiteralPath $dlg.FileName -Delimiter ';' -NoTypeInformation -Encoding $encoding
    $ui.TxtStatus.Text = ('{0} Einträge exportiert nach {1}' -f @($rows).Count, $dlg.FileName)
}

# ---------------------------------------------------------------------------
#  Timer
# ---------------------------------------------------------------------------
$script:ScanTimer = New-Object Windows.Threading.DispatcherTimer
$script:ScanTimer.Interval = [TimeSpan]::FromMilliseconds(400)
$script:ScanTimer.Add_Tick({
    try { Update-Progress } catch { $script:ScanTimer.Stop(); Show-Error $_ }
})

# Wartet, bis das gestartete Deinstallationsprogramm beendet ist, und gleicht dann die Liste ab
$script:ProcTimer = New-Object Windows.Threading.DispatcherTimer
$script:ProcTimer.Interval = [TimeSpan]::FromSeconds(1)
$script:ProcTimer.Add_Tick({
    $running = $false
    try { $running = $script:PendingProc -and -not $script:PendingProc.HasExited } catch { $running = $false }
    if ($running) { return }
    $script:ProcTimer.Stop()
    Invoke-Safe {
        $script:PendingProc = $null
        Sync-Programs
        $ui.TxtStatus.Text = ('Deinstallationsprogramm von „{0}“ beendet – Liste abgeglichen. Falls es noch nachläuft: „Aktualisieren“.' -f $script:PendingName)
    }
})

# ---------------------------------------------------------------------------
#  Ereignisse
# ---------------------------------------------------------------------------
$ui.TxtSearch.Add_TextChanged({ $script:View.Refresh() })
$ui.CmbDrive.Add_SelectionChanged({ if (-not $script:SuppressFilter) { $script:View.Refresh() } })
$ui.ChkStore.Add_Click({ $script:View.Refresh() })

$ui.BtnUninstall.Add_Click({ Invoke-Safe { Invoke-Uninstall $ui.List.SelectedItem } })
$ui.BtnOpen.Add_Click({ Invoke-Safe { Open-Folder $ui.List.SelectedItem } })
$ui.BtnRefresh.Add_Click({ Invoke-Safe { Sync-Programs -Full } })
$ui.BtnExport.Add_Click({ Invoke-Safe { Export-List } })
$ui.List.Add_MouseDoubleClick({ Invoke-Safe { Open-Folder $ui.List.SelectedItem } })
$ui.List.Add_KeyDown({
    param($sender, $e)
    if ($e.Key -eq [Windows.Input.Key]::Delete) {
        $e.Handled = $true
        Invoke-Safe { Invoke-Uninstall $ui.List.SelectedItem }
    }
})

$ui.BtnAdmin.Add_Click({
    try {
        Start-Process -FilePath $script:HostExe -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden', '-File', "`"$script:ScriptPath`"")
        $window.Close()
    } catch {
        # UAC-Abfrage abgebrochen – einfach weiterarbeiten
    }
})

$window.Add_ContentRendered({ Invoke-Safe { Sync-Programs -Full } })
$window.Add_Closing({
    [Kehraus.Scanner]::Cancel()
    $script:ScanTimer.Stop()
    $script:ProcTimer.Stop()
})

[void]$window.ShowDialog()
