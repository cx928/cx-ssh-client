Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process 程星SSH客户端 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_PROCESS"; exit 1 }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Write-Output ("WINDOW: " + $win.Current.Name)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ("ELEMENTS: " + $all.Count)
$i = 0
foreach ($e in $all) {
    try {
        $n = $e.Current.Name
        $t = $e.Current.ControlType.ProgrammaticName -replace "ControlType\.", ""
        $aid = $e.Current.AutomationId
        $pat = ""
        try { if ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)) { $pat = "invoke" } } catch { }
        if ($n -or $aid) {
            Write-Output ("[{0}] {1} | name='{2}' | id='{3}' | {4}" -f $i, $t, $n, $aid, $pat)
        }
    } catch { }
    $i++
}
