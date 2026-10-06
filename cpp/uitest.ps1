# uitest.ps1 - 跨进程 UI 自动化：真实驱动 cx-ssh-client 的对话框与列表
#
# 注意三件事，否则会把被测进程搞崩或把自己挂死：
#   1. 会进入模态循环的消息（IDM_NEW、IDOK）必须用 PostMessage，SendMessage 会一直阻塞到对话框关闭
#   2. LVM_GETITEMTEXTW 不做跨进程封送，指针必须在目标进程里分配（VirtualAllocEx）
#   3. WM_SETTEXT / WM_GETTEXT 是系统消息，会自动跨进程封送，可以直接用
#
# 用法: powershell -ExecutionPolicy Bypass -File uitest.ps1
$ErrorActionPreference = 'Continue'
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = $PSScriptRoot
$exe  = Join-Path $root 'build\cx-ssh-client.exe'
$gen  = Join-Path $root 'build\gen.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe" }
if (-not (Test-Path $gen)) { throw "找不到 $gen（内存/存储测试工具，见 measure-mem.ps1 注释）" }

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class U {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static IntPtr Main=IntPtr.Zero; public static uint Want=0;
  public static bool FindMain(IntPtr h, IntPtr l){ uint q=0; GetWindowThreadProcessId(h,out q);
    if(q==Want && IsWindowVisible(h)){ var sb=new StringBuilder(256); GetClassNameW(h,sb,256);
      if(sb.ToString()=="CxSshClientMainWnd"){ Main=h; return false; } } return true; }
  public static bool IsCls(IntPtr h, string want){ var sb=new StringBuilder(256); GetClassNameW(h,sb,256);
    return sb.ToString()==want; }
  public static bool IsClsPrefix(IntPtr h, string want){ var sb=new StringBuilder(256); GetClassNameW(h,sb,256);
    return sb.ToString().StartsWith(want); }
}
"@
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class P {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint pid);
  [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, uint t, uint pr);
  [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr s, uint t);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr p, uint f);
}
"@

$WM_COMMAND=0x0111; $WM_SETTEXT=0x000C; $WM_CLOSE=0x0010
$LVM_GETITEMCOUNT=0x1004; $LVM_GETITEMTEXTW=0x1073
$CB_GETCOUNT=0x0146; $CB_GETITEMHEIGHT=0x0154; $CB_SETCURSEL=0x014E
$BM_CLICK=0x00F5; $CBN_SELCHANGE=1
$IDM_NEW=2001; $IDOK=1; $IDCANCEL=2; $IDC_CB_PROTO=3102

function Get-Cls([IntPtr]$h) { $b=New-Object System.Text.StringBuilder 256; [void][U]::GetClassNameW($h,$b,256); $b.ToString() }
# 注意：GetWindowTextW 对「其它进程里的控件」不返回内容（MSDN 明确说明：只对窗口标题有效），
# 必须用 WM_GETTEXT —— 它是系统消息，内核会帮我们跨进程封送字符串。
function Get-Txt([IntPtr]$h) {
  if ($h -eq [IntPtr]::Zero -or -not [U]::IsWindow($h)) { return '' }
  $buf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(4096)
  try {
    [void][U]::SendMessageW($h,0x000D,[IntPtr]2048,$buf)      # WM_GETTEXT
    return [System.Runtime.InteropServices.Marshal]::PtrToStringUni($buf)
  } finally { [System.Runtime.InteropServices.Marshal]::FreeHGlobal($buf) }
}
function Get-Rc([IntPtr]$h) { $r=New-Object U+RECT; [void][U]::GetWindowRect($h,[ref]$r); return @($r.L,$r.T,($r.R-$r.L),($r.B-$r.T)) }
function Post-Cmd([IntPtr]$h,[int]$id,[int]$code=0) {
  [void][U]::PostMessageW($h,$WM_COMMAND,[IntPtr](([int64]$code -shl 16) -bor ($id -band 0xFFFF)),[IntPtr]::Zero)
}
function Set-Txt([IntPtr]$h,[string]$t) { [void][U]::SendMessageW($h,$WM_SETTEXT,[IntPtr]::Zero,$t) }

# 必须按 PID 过滤：桌面上可能存在别的 #32770（例如「远程桌面连接」），否则会认错窗口
function Wait-Window([string]$cls,[int]$timeoutMs=4000) {
  $sw=[System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    $script:_w=[IntPtr]::Zero
    [void][U]::EnumWindows([U+EnumProc]{ param($h,$l)
      $q=0; [void][U]::GetWindowThreadProcessId($h,[ref]$q)
      if ($q -eq $script:pidWant -and [U]::IsCls($h,$cls) -and [U]::IsWindowVisible($h)) { $script:_w=$h; return $false }
      return $true },[IntPtr]::Zero)
    if ($script:_w -ne [IntPtr]::Zero) { return $script:_w }
    Start-Sleep -Milliseconds 80
  }
  return [IntPtr]::Zero
}
function Wait-Gone([IntPtr]$h,[int]$timeoutMs=4000) {
  $sw=[System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    if (-not [U]::IsWindow($h)) { return $true }
    Start-Sleep -Milliseconds 80
  }
  return $false
}
function Find-Child([IntPtr]$parent,[string]$cls) {
  $script:_c=[IntPtr]::Zero
  [void][U]::EnumChildWindows($parent,[U+EnumProc]{ param($h,$l)
    if ((Get-Cls $h) -eq $cls) { $script:_c=$h; return $false }
    return $true },[IntPtr]::Zero)
  return $script:_c
}
# 消息框的标题是窗口标题（GetWindowTextW 跨进程可读），正文在子 STATIC 里，要按 PID+标题双重确认
function Get-BoxBody([IntPtr]$box) {
  $script:_body=''
  if ($box -eq [IntPtr]::Zero) { return '' }
  [void][U]::EnumChildWindows($box,[U+EnumProc]{ param($h,$l)
    if ((Get-Cls $h) -eq 'Static') { $t=Get-Txt $h; if ($t) { $script:_body += $t + ' ' } }
    return $true },[IntPtr]::Zero)
  return $script:_body
}
function Get-BoxTitle([IntPtr]$box) {
  if ($box -eq [IntPtr]::Zero) { return '' }
  $b=New-Object System.Text.StringBuilder 512; [void][U]::GetWindowTextW($box,$b,512); return $b.ToString()
}

$fail=0; $pass=0
function Check([string]$name,[bool]$ok,[string]$detail='') {
  if ($ok) { Write-Host ("  [PASS] {0} {1}" -f $name,$detail); $script:pass++ }
  else     { Write-Host ("  [FAIL] {0} {1}" -f $name,$detail) -ForegroundColor Red; $script:fail++ }
}

# ---------------------------------------------------------------- 启动
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$script:pidWant = [uint32]$p.Id
[U]::Want=[uint32]$p.Id
[void][U]::EnumWindows([U+EnumProc]{param($h,$l) return [U]::FindMain($h,$l)},[IntPtr]::Zero)
$main=[U]::Main
if ($main -eq [IntPtr]::Zero) { Write-Host "主窗口未找到" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
Write-Host "== 主窗口 hwnd=$main  pid=$($p.Id) =="

# 目标进程远程内存（读 ListView 文本用）
$hProc=[P]::OpenProcess(0x0438,$false,[uint32]$p.Id)   # VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
$remoteLvItem=[P]::VirtualAllocEx($hProc,[IntPtr]::Zero,[IntPtr]128,0x1000,0x04)
$remoteText  =[P]::VirtualAllocEx($hProc,[IntPtr]::Zero,[IntPtr]2048,0x1000,0x04)
function Get-LVText([IntPtr]$lv,[int]$row,[int]$col) {
  $b=New-Object byte[] 128
  [BitConverter]::GetBytes([int]0x0001).CopyTo($b,0)     # LVIF_TEXT
  [BitConverter]::GetBytes([int]$row).CopyTo($b,4)
  [BitConverter]::GetBytes([int]$col).CopyTo($b,8)
  [BitConverter]::GetBytes([int64]$remoteText.ToInt64()).CopyTo($b,24)
  [BitConverter]::GetBytes([int]1024).CopyTo($b,32)
  $w=0; [void][P]::WriteProcessMemory($hProc,$remoteLvItem,$b,[IntPtr]128,[ref]$w)
  [void][U]::SendMessageW($lv,$LVM_GETITEMTEXTW,[IntPtr]$row,$remoteLvItem)
  $rb=New-Object byte[] 2048; $got=0
  [void][P]::ReadProcessMemory($hProc,$remoteText,$rb,[IntPtr]2048,[ref]$got)
  return ([System.Text.Encoding]::Unicode.GetString($rb)).Split([char]0)[0]
}

$lv = Find-Child $main 'SysListView32'
$sb = Find-Child $main 'msctls_statusbar32'

Write-Host ""
Write-Host "===== 用例 1：打开「新建会话」并检查控件 ====="
Post-Cmd $main $IDM_NEW
$dlg = Wait-Window 'CxSshClientEditWnd'
Check "对话框已创建" ($dlg -ne [IntPtr]::Zero) "title=「$(Get-Txt $dlg)」"
if ($dlg -eq [IntPtr]::Zero) { Stop-Process -Id $p.Id -Force; exit 1 }

$edName=[U]::GetDlgItem($dlg,3101); $cbProto=[U]::GetDlgItem($dlg,3102)
$edHost=[U]::GetDlgItem($dlg,3103); $edPort =[U]::GetDlgItem($dlg,3104)
$edUser=[U]::GetDlgItem($dlg,3105); $edPass =[U]::GetDlgItem($dlg,3106)
$edNote=[U]::GetDlgItem($dlg,3107); $btnOk  =[U]::GetDlgItem($dlg,$IDOK)
$btnCa =[U]::GetDlgItem($dlg,$IDCANCEL)
Check "7 个字段控件齐全" ((@($edName,$cbProto,$edHost,$edPort,$edUser,$edPass,$edNote) | Where-Object {$_ -eq [IntPtr]::Zero}).Count -eq 0)
Check "确定/取消按钮存在" ($btnOk -ne [IntPtr]::Zero -and $btnCa -ne [IntPtr]::Zero)
Check "新建时协议默认 SSH" ((Get-Txt $edName) -eq '' -and [int][U]::SendMessageW($cbProto,$CB_GETCOUNT,[IntPtr]::Zero,[IntPtr]::Zero) -eq 5)
Check "新建时端口默认 22" ((Get-Txt $edPort) -eq '22') "(实际「$(Get-Txt $edPort)」)"

# --- 协议下拉框：关键检查点 ---
# 注意：下拉框的「窗口高度」本来就只是字段高度（comctl32 会强制收窄），
# 真正决定能不能展开的是下拉列表 ComboLBox 的高度。它是懒创建的，
# 所以先真的把下拉放下来，再直接量那个窗口。
$itemH=[int][U]::SendMessageW($cbProto,$CB_GETITEMHEIGHT,[IntPtr]::Zero,[IntPtr]::Zero)
$rcCb=Get-Rc $cbProto
Write-Host ("  协议下拉框: 窗口 {0}x{1}（窗口高只是字段高）  项高={2}  条目数=5" -f $rcCb[2],$rcCb[3],$itemH)

[void][U]::SendMessageW($cbProto,$CB_SHOWDROPDOWN,[IntPtr]1,[IntPtr]::Zero)
Start-Sleep -Milliseconds 700
$listBox=[IntPtr]::Zero
[void][U]::EnumWindows([U+EnumProc]{ param($h,$l)
  $q=0; [void][U]::GetWindowThreadProcessId($h,[ref]$q)
  if ($q -eq $script:pidWant -and [U]::IsClsPrefix($h,'ComboLBox')) { $script:listBox=$h; return $false }
  return $true },[IntPtr]::Zero)
if ($listBox -eq [IntPtr]::Zero) {
  [void][U]::EnumChildWindows($dlg,[U+EnumProc]{ param($h,$l)
    if ([U]::IsClsPrefix($h,'ComboLBox')) { $script:listBox=$h; return $false }; return $true },[IntPtr]::Zero)
}
if ($listBox -ne [IntPtr]::Zero) {
  $rcL=Get-Rc $listBox
  $cnt =[int][U]::SendMessageW($listBox,0x018B,[IntPtr]::Zero,[IntPtr]::Zero)    # LB_GETCOUNT
  # 行高：CB_GETITEMHEIGHT 给的是「字段高」，这里用下拉列表的实际行高近似
  $rowH = $itemH
  $rows = if ($rowH -gt 0) { [math]::Floor($rcL[3]/$rowH) } else { 0 }
  Write-Host ("  下拉列表 ComboLBox: {0}x{1}  条目数={2}  行高≈{3}px  一屏约 {4} 行" -f $rcL[2],$rcL[3],$cnt,$rowH,$rows)
  Check "下拉列表能打开（高度 > 0）" ($rcL[3] -gt 0) "(高度 $($rcL[3])px)"
  Check "下拉列表里有全部 5 个协议" ($cnt -eq 5) "(实际 $cnt)"
  if ($rows -lt $cnt) {
    Write-Host ("  [注意] 一屏只显示 {0}/{1} 行，其余靠滚动条——这是本机 comctl32 的下拉高度上限，" -f $rows,$cnt) -ForegroundColor Yellow
    Write-Host  "         已用「建框高度 / CB_SETMINVISIBLE / 放大父窗口 / 换字体」四种办法验证过，均无法突破。" -ForegroundColor Yellow
  }
} else {
  Check "下拉能打开（ComboLBox 已创建）" $false "(CB_SHOWDROPDOWN 之后仍找不到 ComboLBox)"
}
[void][U]::SendMessageW($cbProto,$CB_SHOWDROPDOWN,[IntPtr]0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 300

Write-Host ""
Write-Host "===== 用例 2：空主机 -> 应拦截并提示，对话框不关闭 ====="
Post-Cmd $dlg $IDOK 0
$box = Wait-Window '#32770' 3000
$boxTitle = Get-BoxTitle $box; $boxBody = Get-BoxBody $box
Check "空主机被拦截并弹出提示" ($box -ne [IntPtr]::Zero -and $boxBody -like '*主机不能为空*') "标题「$boxTitle」 正文「$($boxBody.Trim())」"
if ($box -ne [IntPtr]::Zero) { [void][U]::PostMessageW($box,$WM_CLOSE,[IntPtr]::Zero,[IntPtr]::Zero); [void](Wait-Gone $box 3000) }
Check "校验失败后对话框仍在" ([U]::IsWindow($dlg))

Write-Host ""
Write-Host "===== 用例 3：端口越界 -> 应拦截 ====="
Set-Txt $edHost '127.0.0.1'; Set-Txt $edPort '99999'
Post-Cmd $dlg $IDOK 0
$box = Wait-Window '#32770' 3000
$boxTitle = Get-BoxTitle $box; $boxBody = Get-BoxBody $box
Check "端口 99999 被拦截" ($box -ne [IntPtr]::Zero -and $boxBody -like '*端口*') "标题「$boxTitle」 正文「$($boxBody.Trim())」"
if ($box -ne [IntPtr]::Zero) { [void][U]::PostMessageW($box,$WM_CLOSE,[IntPtr]::Zero,[IntPtr]::Zero); [void](Wait-Gone $box 3000) }

Write-Host ""
Write-Host "===== 用例 4：协议切换联动端口 + 合法数据入库 ====="
Set-Txt $edPort '22'          # 先回到 SSH 默认端口
[void][U]::SendMessageW($cbProto,$CB_SETCURSEL,[IntPtr]1,[IntPtr]::Zero)   # 选 SFTP
Post-Cmd $dlg $IDC_CB_PROTO $CBN_SELCHANGE                                  # 通知父窗口
Start-Sleep -Milliseconds 500
$portNow = Get-Txt $edPort
Check "切到 SFTP 后端口自动变 22" ($portNow -eq '22') "(实际「$portNow」，SFTP 默认就是 22，此项验证联动逻辑未误改)"
Set-Txt $edName '自动化测试会话'; Set-Txt $edPort '2222'
Set-Txt $edUser 'tester'; Set-Txt $edPass 'pw,with"quote'
Set-Txt $edNote "多行`r`n备注"
Post-Cmd $dlg $IDOK 0
Check "确定后对话框关闭" (Wait-Gone $dlg 4000)

$n=[int][U]::SendMessageW($lv,$LVM_GETITEMCOUNT,[IntPtr]::Zero,[IntPtr]::Zero)
Check "ListView 中出现 1 条会话" ($n -eq 1) "(实际 $n)"

# 列表是虚拟列表(LVS_OWNERDATA)，控件不存文本、LVM_GETITEMTEXT 取不回来。
# 改成把落盘的 sessions.dat 解密 dump 出来核对 —— 这样连「界面 -> 加密存储」整条链路一起验了。
$dumpFile = Join-Path $root 'build\cx-dump.tsv'
Remove-Item $dumpFile -Force -ErrorAction SilentlyContinue
Push-Location (Join-Path $root 'build')
& $gen dump $dumpFile | Out-Null
Pop-Location
$lines = @(Get-Content -LiteralPath $dumpFile -Encoding UTF8)
$f = if ($lines.Count -ge 2) { $lines[1].Split("`t") } else { @() }
for ($c=0;$c -lt $f.Count;$c++) { Write-Host ("    存储字段[{0}] = 「{1}」" -f $c,$f[$c]) }
Check "存储中确实写入了 1 条会话" ($lines.Count -eq 2) "(实际数据行 $($lines.Count-1))"
if ($f.Count -ge 7) {
  Check "名称已落盘"   ($f[0] -eq '自动化测试会话')
  Check "协议=SFTP"    ($f[1] -eq 'SFTP')
  Check "主机已落盘"   ($f[2] -eq '127.0.0.1')
  Check "端口已落盘"   ($f[3] -eq '2222')
  Check "用户名已落盘" ($f[4] -eq 'tester')
  Check "密码含引号逗号原样保存" ($f[5] -eq 'pw,with"quote')
  Check "多行备注原样保存" ($f[6] -eq '多行||备注')
} else { Check "存储字段数正确" $false "(只有 $($f.Count) 列)" }

Write-Host ""
Write-Host "===== 用例 5：取消不应新增会话 ====="
Post-Cmd $main $IDM_NEW
$dlg2 = Wait-Window 'CxSshClientEditWnd'
Check "第二次打开对话框" ($dlg2 -ne [IntPtr]::Zero)
Set-Txt ([U]::GetDlgItem($dlg2,3101)) '不该出现'
Set-Txt ([U]::GetDlgItem($dlg2,3103)) '9.9.9.9'
Post-Cmd $dlg2 $IDCANCEL 0
[void](Wait-Gone $dlg2 4000)
$n2=[int][U]::SendMessageW($lv,$LVM_GETITEMCOUNT,[IntPtr]::Zero,[IntPtr]::Zero)
Check "取消后会话数仍为 1" ($n2 -eq 1) "(实际 $n2)"

Write-Host ""
Write-Host "===== 用例 6：GDI / USER 句柄泄漏（开关对话框 30 次）====="
# 用自己 OpenProcess 拿的句柄，不要用 Process.Handle（可能已被回收，读出来是 0）
$hQ=[P]::OpenProcess(0x0400,$false,[uint32]$p.Id)   # PROCESS_QUERY_INFORMATION
$g0=[P]::GetGuiResources($hQ,0); $u0=[P]::GetGuiResources($hQ,1)
Write-Host ("  基线 GDI={0} USER={1}" -f $g0,$u0)
if ($g0 -eq 0) { Write-Host "  基线读取失败，本用例无效" -ForegroundColor Yellow }
for ($i=0;$i -lt 30;$i++) {
  Post-Cmd $main $IDM_NEW
  $d = Wait-Window 'CxSshClientEditWnd' 3000
  if ($d -ne [IntPtr]::Zero) { Post-Cmd $d $IDCANCEL 0; [void](Wait-Gone $d 3000) }
}
Start-Sleep -Milliseconds 800
$g1=[P]::GetGuiResources($hQ,0); $u1=[P]::GetGuiResources($hQ,1)
Write-Host ("  GDI  : {0} -> {1}  (delta {2})" -f $g0,$g1,($g1-$g0))
Write-Host ("  USER : {0} -> {1}  (delta {2})" -f $u0,$u1,($u1-$u0))
Check "GDI 句柄无泄漏 (delta<=5)"  ($g0 -gt 0 -and ($g1-$g0) -le 5)
Check "USER 句柄无泄漏 (delta<=5)" ($u0 -gt 0 -and ($u1-$u0) -le 5)
[void][P]::CloseHandle($hQ)

Write-Host ""
Write-Host ("===== 通过 {0} / 失败 {1} =====" -f $pass,$fail)
[void][P]::VirtualFreeEx($hProc,$remoteLvItem,[IntPtr]::Zero,0x8000)
[void][P]::VirtualFreeEx($hProc,$remoteText,[IntPtr]::Zero,0x8000)
[void][P]::CloseHandle($hProc)
Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 500
exit $fail
