# measure-mem.ps1 - 测量 cx-ssh-client 的内存占用（工作集 / 私有字节 / 句柄 / GDI+USER）
# 用法: powershell -ExecutionPolicy Bypass -File measure-mem.ps1 [-Counts 0,1,2000] [-Settle 4]
param(
    [string] $Counts = '0,1,2000',   # 用 -File 调用时数组参数传不进来，改成逗号分隔字符串
    [int]    $Settle = 4
)
$ErrorActionPreference = 'Continue'
$CountList = @($Counts.Split(',') | ForEach-Object { [int]$_.Trim() })
$root = $PSScriptRoot
$exe  = Join-Path $root 'build\cx-ssh-client.exe'
$gen  = Join-Path $root 'build\gen.exe'
$data = Join-Path $root 'build\cx-ssh-client-data'

Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class M {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")]   public static extern uint GetGuiResources(IntPtr p, uint f);
  [DllImport("psapi.dll")]    public static extern bool EmptyWorkingSet(IntPtr p);
}
"@

function Sample([int]$pid2, [bool]$trim) {
    $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $h = [M]::OpenProcess(0x0400 -bor 0x0100, $false, [uint32]$pid2)   # QUERY_INFORMATION|QUERY_LIMITED
    $gdi = 0; $usr = 0
    if ($h -ne [IntPtr]::Zero) { $gdi = [M]::GetGuiResources($h,0); $usr = [M]::GetGuiResources($h,1) }
    if ($trim -and $h -ne [IntPtr]::Zero) { [void][M]::EmptyWorkingSet($h) ; Start-Sleep -Milliseconds 400 }
    $p.Refresh()
    $r = [ordered]@{
        WorkingSetMB = [math]::Round($p.WorkingSet64/1MB, 2)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64/1MB, 2)
        PagedMB      = [math]::Round($p.PagedMemorySize64/1MB, 2)
        Handles      = $p.HandleCount
        Threads      = $p.Threads.Count
        GDI          = $gdi
        USER         = $usr
    }
    if ($h -ne [IntPtr]::Zero) { [void][M]::CloseHandle($h) }
    return $r
}

$rows = @()
foreach ($n in $CountList) {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    if ($n -gt 0) {
        Push-Location (Join-Path $root 'build')
        & $gen $n | Out-Null
        Pop-Location
        Start-Sleep -Milliseconds 400
    }
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds $Settle
    if ($proc.HasExited) { Write-Host "N=$n 进程异常退出" -ForegroundColor Red; continue }

    $a = Sample $proc.Id $false          # 稳态
    $b = Sample $proc.Id $true           # 主动 Trim 之后
    $exeSize = [math]::Round((Get-Item $exe).Length/1KB, 1)
    $datSize = if (Test-Path (Join-Path $data 'sessions.dat')) { [math]::Round((Get-Item (Join-Path $data 'sessions.dat')).Length/1KB,1) } else { 0 }
    $rows += [pscustomobject]@{
        N = $n; DatKB = $datSize
        WS = $a.WorkingSetMB; Priv = $a.PrivateMB; Paged = $a.PagedMB
        Handles = $a.Handles; Threads = $a.Threads; GDI = $a.GDI; USER = $a.USER
        WStrim = $b.WorkingSetMB; PrivTrim = $b.PrivateMB
    }
    Stop-Process -Id $proc.Id -Force
    Start-Sleep -Milliseconds 700
}
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host ("exe size = {0} KB" -f $exeSize)
Write-Host "N      dat(KB)  WS(MB)  Priv(MB)  Paged(MB)  Handles  Thr  GDI  USER  WS_trim  Priv_trim"
foreach ($r in $rows) {
    Write-Host ("{0,-6} {1,-8} {2,-7} {3,-9} {4,-10} {5,-8} {6,-4} {7,-4} {8,-5} {9,-8} {10}" -f `
        $r.N,$r.DatKB,$r.WS,$r.Priv,$r.Paged,$r.Handles,$r.Threads,$r.GDI,$r.USER,$r.WStrim,$r.PrivTrim)
}
