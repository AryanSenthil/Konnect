# End-to-end test for kicad-editor-bridge against a standalone Eeschema on a throwaway copy of a KiCad demo.
# Proves: (1) without a reload, KiCad's next save silently overwrites an outside edit;
#         (2) with `reload`, the open editor picks the edit up and a later save keeps it.
param(
  [string]$Bridge = "$PSScriptRoot\kicad-editor-bridge.exe",
  [string]$Work = "$env:TEMP\kicad-bridge-test",
  [string]$Demo = "C:\Program Files\KiCad\10.0\share\kicad\demos\pic_programmer",
  [string]$Eeschema = "C:\Program Files\KiCad\10.0\bin\eeschema.exe"
)
$ErrorActionPreference = "Stop"
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
Copy-Item -Recurse $Demo $Work
$sch = Join-Path $Work "pic_programmer.kicad_sch"

function Bridge($cmd, [switch]$Force) {
  $a = @($cmd, $sch); if ($Force) { $a += "--force" }
  $out = & $Bridge @a
  Write-Host "  bridge $cmd -> $out"
  return ($out | ConvertFrom-Json)
}
function AddMarker($text) {
  $src = [IO.File]::ReadAllText($sch)
  $item = "`t(text `"$text`" (exclude_from_sim no) (at 40 40 0) (effects (font (size 1.27 1.27))) (uuid `"$([guid]::NewGuid())`"))`n"
  $i = $src.LastIndexOf("`t(sheet_instances")
  [IO.File]::WriteAllText($sch, $src.Insert($i, $item))
}
function Has($text) { return ([IO.File]::ReadAllText($sch)).Contains($text) }

Write-Host "1. open a standalone Schematic Editor on the copy"
$proc = Start-Process -FilePath $Eeschema -ArgumentList "`"$sch`"" -PassThru
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Milliseconds 500; $s = & $Bridge status $sch | ConvertFrom-Json } until (($s.editor -and -not $s.dialog_open) -or (Get-Date) -gt $deadline)
Write-Host "  status: $(& $Bridge status $sch)"
if (-not $s.editor) { throw "editor window never appeared" }
Start-Sleep -Seconds 2

$results = [ordered]@{}
try {
  Write-Host "2. control: outside edit, NO reload, then KiCad saves"
  AddMarker "CONTROL-NO-RELOAD"
  $r = Bridge save -Force
  $results["control: marker survives KiCad save without reload (expect False)"] = Has "CONTROL-NO-RELOAD"

  Write-Host "3. real: outside edit, reload through the bridge, then KiCad saves"
  AddMarker "LIVE-RELOAD-OK"
  $r = Bridge reload
  $results["reload ok"] = [bool]$r.ok
  $r = Bridge save -Force
  $results["marker survives KiCad save after reload (expect True)"] = Has "LIVE-RELOAD-OK"
}
finally {
  Write-Host "4. close the test editor"
  $w = & $Bridge status $sch | ConvertFrom-Json
  if ($w.editor) {
    Add-Type -Namespace T -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
    [T.U]::PostMessage([IntPtr][long]$w.hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
  }
  Start-Sleep -Seconds 3
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
Write-Host "=== results ==="
$results.GetEnumerator() | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Key, $_.Value) }
