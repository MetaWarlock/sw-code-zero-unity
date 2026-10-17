$exe = "C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Unity.exe"
$proj = "D:\Art\SW Code Zero Unity"
$log = "D:\Art\SW Code Zero Unity\Logs\Editor-reopen3.log"
$argLine = '-projectPath "' + $proj + '" -acceptSoftwareTermsForThisRunOnly -logFile "' + $log + '"'
$p = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $proj -PassThru
Write-Output ("pid=" + $p.Id + " args=" + $argLine)
