<#
    build.ps1 — 程星SSH客户端 (cx-ssh-client) 原生 C++ 版一键编译脚本
    使用 MinGW-w64 的 g++，零第三方依赖。

    用法：
        powershell -ExecutionPolicy Bypass -File build.ps1
        powershell -ExecutionPolicy Bypass -File build.ps1 -Clean
        powershell -ExecutionPolicy Bypass -File build.ps1 -GxxPath "C:\msys64\mingw64\bin\g++.exe"
        powershell -ExecutionPolicy Bypass -File build.ps1 -NoManifest      # 不嵌入清单（无 windres 时）
        powershell -ExecutionPolicy Bypass -File build.ps1 -WithTools       # 顺便编译验证工具 gen.exe

    产物：build\cx-ssh-client.exe（-WithTools 时另有 build\gen.exe）
#>
[CmdletBinding()]
param(
    [string] $GxxPath    = '',
    [switch] $Clean,
    [switch] $NoManifest,
    [switch] $WithTools
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $root) { $root = (Get-Location).Path }

$outDir = Join-Path $root 'build'
$exeOut = Join-Path $outDir 'cx-ssh-client.exe'
$resOut = Join-Path $outDir 'app.res'

$sources = @(
    'cx-ssh-client.cpp',
    'src\common.cpp',
    'src\session.cpp',
    'src\probe.cpp',
    'src\ui.cpp'
)

$libs = @(
    '-lws2_32', '-lcomctl32', '-lcrypt32', '-lshlwapi', '-lole32',
    '-lshell32', '-lcomdlg32', '-luser32', '-lgdi32', '-ladvapi32'
)

# ---------------------------------------------------------------------------
# 1. 查找 g++
# ---------------------------------------------------------------------------
function Find-Gxx {
    param([string] $Hint)

    if ($Hint) {
        $p = $Hint
        if (Test-Path -LiteralPath $p -PathType Container) { $p = Join-Path $p 'g++.exe' }
        if (Test-Path -LiteralPath $p -PathType Leaf) { return (Resolve-Path -LiteralPath $p).Path }
        throw "指定的 g++ 不存在：$Hint"
    }

    $cmd = Get-Command g++ -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 常见安装位置
    $patterns = @()
    if ($env:LOCALAPPDATA) {
        $patterns += (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\*\mingw64\bin\g++.exe')
        $patterns += (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\*\mingw32\bin\g++.exe')
        $patterns += (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\g++.exe')
    }
    if ($env:USERPROFILE) {
        $patterns += (Join-Path $env:USERPROFILE 'scoop\apps\mingw\current\bin\g++.exe')
        $patterns += (Join-Path $env:USERPROFILE 'scoop\apps\mingw-winlibs\current\bin\g++.exe')
        $patterns += (Join-Path $env:USERPROFILE 'scoop\shims\g++.exe')
        $patterns += (Join-Path $env:USERPROFILE 'mingw-dl\mingw64\bin\g++.exe')
        $patterns += (Join-Path $env:USERPROFILE 'mingw64\bin\g++.exe')
    }
    $patterns += 'C:\msys64\ucrt64\bin\g++.exe'
    $patterns += 'C:\msys64\mingw64\bin\g++.exe'
    $patterns += 'C:\msys64\mingw32\bin\g++.exe'
    $patterns += 'C:\mingw64\bin\g++.exe'
    $patterns += 'C:\mingw32\bin\g++.exe'
    $patterns += 'C:\mingw-toolchain\mingw64\bin\g++.exe'
    $patterns += 'C:\ProgramData\chocolatey\lib\mingw\tools\install\mingw64\bin\g++.exe'
    $patterns += 'C:\ProgramData\chocolatey\bin\g++.exe'
    $patterns += 'C:\Program Files\mingw-w64\*\mingw64\bin\g++.exe'
    $patterns += 'C:\Program Files (x86)\mingw-w64\*\mingw64\bin\g++.exe'

    foreach ($pat in $patterns) {
        $hit = Get-ChildItem -Path $pat -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    # 最后再在几个根目录里做一次有深度限制的递归搜索
    foreach ($r in @('C:\msys64', 'C:\mingw64', 'C:\mingw-toolchain', 'C:\ProgramData\chocolatey')) {
        if (-not (Test-Path -LiteralPath $r)) { continue }
        $hit = Get-ChildItem -LiteralPath $r -Filter 'g++.exe' -File -Recurse -Depth 5 -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    throw @'
没有找到 g++。请先安装 MinGW-w64，例如：
  winget install --id BrechtSanders.WinLibs.POSIX.UCRT --silent --accept-package-agreements --accept-source-agreements
或者用 -GxxPath 指定 g++.exe 的完整路径。
'@
}

$gxx = Find-Gxx -Hint $GxxPath
$gxxDir = Split-Path -Parent $gxx
Write-Host "== 工具链 ==" -ForegroundColor Cyan
Write-Host "  g++ : $gxx"
$verLine = (& $gxx --version 2>&1 | Select-Object -First 1)
Write-Host "  版本: $verLine"

# 把工具链目录放进本次会话的 PATH，方便 g++ 调用 as/ld 等同伴程序
$env:PATH = "$gxxDir;$env:PATH"

# ---------------------------------------------------------------------------
# 2. 准备输出目录
# ---------------------------------------------------------------------------
if ($Clean -and (Test-Path -LiteralPath $outDir)) {
    Write-Host "== 清理 $outDir ==" -ForegroundColor Cyan
    Remove-Item -LiteralPath $outDir -Recurse -Force
}
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# ---------------------------------------------------------------------------
# 3. 可选的资源编译（嵌入 comctl32 v6 清单，获得现代控件外观）
# ---------------------------------------------------------------------------
$resArg = @()
$rcFile = Join-Path $root 'app.rc'
$windres = Join-Path $gxxDir 'windres.exe'
if ((-not $NoManifest) -and (Test-Path -LiteralPath $rcFile) -and (Test-Path -LiteralPath $windres)) {
    Write-Host "== 编译资源清单 ==" -ForegroundColor Cyan
    Push-Location $root
    $prevEapRc = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        # -O coff 必须显式指定：windres 默认输出裸 .res，新版 binutils 的 ld 不认
        & $windres "--input=$rcFile" "--output=$resOut" "--include-dir=$root" '-O' 'coff' 2>&1 |
            ForEach-Object { Write-Host "  $_" }
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $resOut)) {
            $resArg = @($resOut)
            Write-Host "  app.res 已生成（启用 Visual Styles / DPI 感知清单）"
        } else {
            Write-Warning "windres 失败，跳过清单嵌入（程序仍可正常编译运行）"
        }
    } finally {
        $ErrorActionPreference = $prevEapRc
        Pop-Location
    }
} else {
    Write-Host "== 跳过资源清单（--NoManifest 或未找到 windres）==" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 4. 编译
# ---------------------------------------------------------------------------
$gxxArgs = @(
    '-std=c++17'
    '-municode'
    '-O2'
    '-static'
    '-mwindows'
    '-I.'
    '-Isrc'
    '-finput-charset=UTF-8'
    '-fexec-charset=UTF-8'
    '-Wall'
    '-Wextra'
    '-Wno-unused-parameter'
    # 体积/内存优化：每个函数、每个数据各自一个节，链接时把没用到的整段丢掉，
    # 再剥掉符号表。镜像越小，进程启动时要映射和触碰的页就越少。
    '-ffunction-sections'
    '-fdata-sections'
    '-fno-rtti'
    '-Wl,--gc-sections'
    '-s'
    '-Wl,--nxcompat'
    '-Wl,--dynamicbase'
    '-o', $exeOut
) + $sources + $resArg + $libs

Write-Host "== 编译 ==" -ForegroundColor Cyan
Write-Host "  g++ $($gxxArgs -join ' ')"
Write-Host ""

Push-Location $root
$rawLines = @()
$exitCode = 1
$prevEap = $ErrorActionPreference
try {
    # g++ 把诊断写到 stderr；$ErrorActionPreference='Stop' 时 PowerShell 会把它当成终止错误，
    # 这里临时放开，改为自己按退出码判断成败。
    $ErrorActionPreference = 'Continue'
    $rawLines = & $gxx @gxxArgs 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $prevEap
    Pop-Location
}

foreach ($line in $rawLines) { Write-Host $line }

$errors   = @($rawLines | Where-Object { $_ -match ':\s*error:' -or $_ -match '^error:' })
$warnings = @($rawLines | Where-Object { $_ -match ':\s*warning:' -or $_ -match '^warning:' })

Write-Host ""
Write-Host "== 结果 ==" -ForegroundColor Cyan
Write-Host ("  error   = {0}" -f $errors.Count)
Write-Host ("  warning = {0}" -f $warnings.Count)
Write-Host ("  g++ 退出码 = {0}" -f $exitCode)

if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $exeOut)) {
    Write-Host "编译失败。" -ForegroundColor Red
    exit 1
}

$fi = Get-Item -LiteralPath $exeOut
Write-Host ("  产物: {0}  ({1:N0} 字节)" -f $fi.FullName, $fi.Length) -ForegroundColor Green

# ---------------------------------------------------------------------------
# 5. 可选的验证工具（内存测量 / 存储 dump 用，不属于程序本体）
# ---------------------------------------------------------------------------
if ($WithTools) {
    $genOut = Join-Path $outDir 'gen.exe'
    $genArgs = @(
        '-std=c++17', '-O2', '-municode', '-static', '-mconsole',
        '-I.', '-Isrc', '-finput-charset=UTF-8', '-fexec-charset=UTF-8',
        '-o', $genOut, 'tools\gen.cpp', 'src\common.cpp', 'src\session.cpp',
        '-lcrypt32', '-lshlwapi', '-lole32', '-lshell32', '-ladvapi32', '-luser32'
    )
    Write-Host "== 编译验证工具 gen.exe ==" -ForegroundColor Cyan
    $prevEap2 = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location $root
    try {
        & $gxx @genArgs 2>&1 | ForEach-Object { Write-Host "  $_" }
        $genRc = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap2
        Pop-Location
    }
    if ($genRc -eq 0 -and (Test-Path -LiteralPath $genOut)) {
        Write-Host ("  gen.exe -> {0}" -f $genOut) -ForegroundColor Green
    } else {
        Write-Warning "gen.exe 编译失败（不影响程序本体）"
    }
}

Write-Host "编译成功。" -ForegroundColor Green
exit 0
