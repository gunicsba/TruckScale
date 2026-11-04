# Takes the documentation screenshots (docs/images) from the program running on the demo database.
# Windows only (UI Automation). Never uses real data: the demo database is generated with tools/TruckScale.Demo.
#
#   powershell -ExecutionPolicy Bypass -File tools\Screenshots.ps1
#
# Nothing is printed: the screenshots stop before any Nyomtatás.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "docs\images"
$work = Join-Path $env:TEMP "truckscale-screenshots"
New-Item -ItemType Directory -Force $out, $work | Out-Null

Write-Host "Building..."
dotnet build "$root\src\TruckScale.App" -c Release -v q | Out-Null
dotnet run --project "$root\tools\TruckScale.Demo" -c Release -- "$work\demo.db" --overwrite

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class ShotNative {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  public static void DoubleClick(int x, int y) { SetCursorPos(x, y); for (int i = 0; i < 2; i++) { mouse_event(2,0,0,0,0); mouse_event(4,0,0,0,0); } }
}
"@
[ShotNative]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$nb = [char]0xA0

function Find($el, $name) { $el.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) }
function Click($el, $name) {
  $e = Find $el $name; if (-not $e) { throw "not found: $name" }
  $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  while ($e -and ($e.GetSupportedPatterns().ProgrammaticName -notcontains "InvokePatternIdentifiers.Pattern")) { $e = $w.GetParent($e) }
  $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Window($title, $el) {
  for ($i = 0; $i -lt 40; $i++) {
    $w = $el.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) |
      Where-Object { $_.Current.Name -like $title } | Select-Object -First 1
    if ($w) { return $w }; Start-Sleep -Milliseconds 250
  }
  throw "window not found: $title"
}
function Edits($el) { $el.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))) }
function SetText($edit, $text) { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Shot($el, $name) {
  Start-Sleep -Milliseconds 800
  $r = $el.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Write-Host "  $name.png"
}

$exe = Join-Path $root "src\TruckScale.App\bin\Release\net10.0\TruckScale.exe"
$p = Start-Process $exe -ArgumentList "--db `"$work\demo.db`" --sim" -PassThru
try {
  $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$p.Id)
  for ($i = 0; $i -lt 40 -and -not ($main = $A::RootElement.FindFirst($TS::Children, $cond)); $i++) { Start-Sleep -Milliseconds 250 }
  Start-Sleep -Seconds 2
  [ShotNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null

  # Main window with a loaded truck on the scale
  Click $main "26${nb}440 kg"; Start-Sleep -Seconds 3
  Shot $main "main"

  # Arrival (Beszállítás), pre-filled from the last weighing
  Click $main "Beszállítás"
  $dlg = Window "Jármű érkezése*" $main
  $ae = Edits $dlg
  SetText $ae[0] "Példa Péter"; Start-Sleep -Milliseconds 400
  ($ae | Select-Object -Skip 1 | Where-Object { $_.Current.IsKeyboardFocusable } | Select-Object -First 1).SetFocus()   # closes the suggestion list
  Shot $dlg "arrival"
  Click $dlg "Új…"
  $nw = Window "Új ügyfél" $main
  Shot $nw "new-customer"
  Click $nw "Mégse"; Start-Sleep -Milliseconds 500
  $dlg = $main.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) |
    Where-Object { $_.Current.Name -like "Jármű érkezése*" } | Select-Object -First 1
  if ($dlg) { Click $dlg "Mégse" }; Start-Sleep -Milliseconds 500

  # Departure of a truck in the yard: double-click its row
  Click $main "11${nb}300 kg"; Start-Sleep -Seconds 3
  $row = (Find $main "ABC-123/XYZ-456").Current.BoundingRectangle
  [ShotNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300
  [ShotNative]::DoubleClick([int]($row.X + 5), [int]($row.Y + $row.Height / 2))
  $dlg = Window "Jármű távozása*" $main
  Shot $dlg "departure"
  Click $dlg "Mérés"          # weigh only (no printing) to show the result
  $dlg = Window "Mérlegjegy*" $main
  Shot $dlg "departure-done"
  Click $dlg "Bezárás"; Start-Sleep -Seconds 1
  Shot $main "main-after"

  # Napló with column filters, and a ticket preview
  Click $main "Napló"
  $log = Window "Napló*" $main
  $e = Edits $log
  SetText $e[1] (Get-Date).AddYears(-1).ToString("yyyy")
  SetText $e[5] "búza"
  Shot $log "log"
  $cell = $log.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)))
  $r = $cell.Current.BoundingRectangle
  [ShotNative]::DoubleClick([int]($r.X + 20), [int]($r.Y + $r.Height / 2))
  $tp = Window "Mérlegjegy * előnézet" $main
  Shot $tp "ticket-preview"
  Click $tp "Bezárás"; Click $log "Bezárás"; Start-Sleep -Milliseconds 500

  # Áttekintés (previous year, inbound, per grain and customer) and its print preview
  Click $main "Áttekintés"
  $sw = Window "Áttekintés*" $main
  Click $sw "Tavaly"
  $dir = $sw.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
  $dir.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
  $item = $A::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Beszállítás")),
      (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))))
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  try { $dir.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
  Shot $sw "summary"
  Click $sw "Előnézet"
  $rp = Window "*– előnézet" $main
  Start-Sleep -Seconds 1
  Shot $rp "summary-preview"
  Click $rp "Bezárás"; Click $sw "Bezárás"; Start-Sleep -Milliseconds 500

  # Adatállomány and Beállítás
  Click $main "Adatállomány"
  $pw = Window "Adatállomány*" $main
  Shot $pw "customers"
  Click $pw "Bezárás"; Start-Sleep -Seconds 2
  [ShotNative]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null
  Click $main "Beállítás"
  $st = Window "Beállítás" $main
  Shot $st "settings"
  Click $st "Mégse"
}
finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
Write-Host "Screenshots saved to $out"
