# Scan the tail of a huge log without loading the whole file (Unity truncates/rotates Editor.log
# and a Player build wipes Art/Temp/, so evidence must be copied out of Art/Temp/ immediately).
param([string]$Path,[long]$Bytes=14000000,[string]$Pattern)
$fs=[System.IO.File]::Open($Path,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,[System.IO.FileShare]::ReadWrite)
try {
  $len=$fs.Length
  $start=$len-$Bytes
  if($start -lt 0){ $start=0 }
  $fs.Seek($start,[System.IO.SeekOrigin]::Begin)|Out-Null
  $sr=New-Object System.IO.StreamReader($fs)
  if($start -gt 0){ $null=$sr.ReadLine() }
  $out=@()
  while(-not $sr.EndOfStream){ $line=$sr.ReadLine(); if($line -match $Pattern){ $out+=$line } }
  Write-Output ("lines_matched=" + $out.Count)
  $out | Select-Object -Last 40
} finally { $fs.Dispose() }
