<#
    개인정보 정리 도구

    - 회사 PC를 포맷하지 않고 계속 사용하면서, 개인 흔적만 지우는 용도로 만들었습니다.
    - 검사 결과를 체크박스 목록으로 보여 주고, 체크한 항목만 지웁니다.
    - Windows 기본 PowerShell 5.1에서 동작하도록 작성했습니다.
      (삼항 연산자, ??, && 같은 PowerShell 7 문법은 사용하지 않습니다.)
    - 한글이 깨지지 않도록 이 파일은 반드시 UTF-8 with BOM으로 저장합니다.
#>
param(
    # 켜면 '연습 모드'가 체크된 상태로 시작합니다. 실제로 지우지 않고 로그만 남깁니다.
    [switch]$ScanOnly
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# =====================================================================
# 실행 환경 정보
# =====================================================================

$script:SelfPath = $MyInvocation.MyCommand.Path
$script:Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$script:MySid    = $script:Identity.User.Value
$script:IsAdmin  = (New-Object Security.Principal.WindowsPrincipal($script:Identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# 관리자 권한 상승 시 IT 담당자 계정으로 실행되는 경우가 있습니다.
# 그러면 HKCU, 인증서 저장소, 자격 증명이 모두 그 계정 것이 되므로 화면에 경고를 띄웁니다.
$script:ConsoleUser = $null
try { $script:ConsoleUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch {}
$script:OtherAccount = [bool]($script:ConsoleUser -and ($script:ConsoleUser -ne $script:Identity.Name))

# 회사 PC는 설치 프로그램이 PATH를 덮어써서 System32가 빠져 있는 경우가 있습니다.
# 그러면 'cmdkey.exe'처럼 이름만으로는 실행 파일을 찾지 못하므로, 외부 프로그램은 전체 경로로 호출합니다.
# GetFolderPath('System')는 환경 변수가 아니라 Windows API에서 경로를 받아 오므로 PATH와 무관합니다.
$script:Sys32      = [Environment]::GetFolderPath('System')
$script:CmdkeyExe  = Join-Path $script:Sys32 'cmdkey.exe'
$script:NetshExe   = Join-Path $script:Sys32 'netsh.exe'
# 관리자 권한으로 다시 실행할 때는 지금 실행 중인 PowerShell과 같은 파일을 사용합니다.
$script:PsExe      = Join-Path $PSHOME 'powershell.exe'

# 로그는 도구 폴더에 남깁니다. %TEMP%에 두면 임시 파일 정리 항목이 로그까지 지워 버립니다.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:LogPath = Join-Path (Split-Path -Parent $script:SelfPath) ("log-$stamp.txt")
try { [System.IO.File]::AppendAllText($script:LogPath, '') }
catch { $script:LogPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ("privacy-clean-log-$stamp.txt") }

# 검사 결과 목록입니다. Add-Found가 채우고, 화면은 이 목록을 그대로 보여 줍니다.
$script:Items = New-Object 'System.Collections.Generic.List[object]'
$script:Populating = $false
$script:LogBox = $null

# 화면에 보여 줄 그룹 순서와 제목입니다.
$script:GroupOrder = @(
    @{ Key = 'cert';      Title = '공동인증서' },
    @{ Key = 'certstore'; Title = 'Windows 인증서 저장소 (회사 VPN, 사내 Wi-Fi 인증서가 섞여 있을 수 있음)' },
    @{ Key = 'browser';   Title = '브라우저' },
    @{ Key = 'messenger'; Title = '메신저' },
    @{ Key = 'files';     Title = '개인 파일 (파일 이름으로 찾은 것이라 업무 파일이 섞일 수 있음, 하나씩 보고 체크)' },
    @{ Key = 'history';   Title = '사용 기록, 휴지통, 임시 파일' },
    @{ Key = 'cred';      Title = 'Windows 자격 증명 (업무용 로그인과 섞여 있음)' },
    @{ Key = 'wifi';      Title = 'Wi-Fi 저장 목록' }
)

# =====================================================================
# 공통 도우미
# =====================================================================

function Write-Log([string]$Message) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch {}
    if ($script:LogBox) { $script:LogBox.AppendText($line + "`r`n") }
}

function Format-Size([long]$Bytes, [int]$Count) {
    $text = '-'
    if ($Bytes -ge 1GB)     { $text = '{0:N1} GB' -f ($Bytes / 1GB) }
    elseif ($Bytes -ge 1MB) { $text = '{0:N1} MB' -f ($Bytes / 1MB) }
    elseif ($Bytes -ge 1KB) { $text = '{0:N0} KB' -f ($Bytes / 1KB) }
    elseif ($Bytes -gt 0)   { $text = '{0} B' -f $Bytes }
    if ($Count -gt 1) {
        if ($text -eq '-') { return ('{0}개' -f $Count) }
        return ('{0} ({1}개)' -f $text, $Count)
    }
    return $text
}

# 경로 목록의 전체 크기와 파일 개수를 구합니다. 없는 경로는 건너뜁니다.
function Get-PathStats([string[]]$Paths) {
    $size = [long]0
    $count = 0
    foreach ($p in $Paths) {
        if (-not $p) { continue }
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $it = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if ($it -is [System.IO.FileInfo]) {
            $size += $it.Length
            $count++
        }
        else {
            $m = Get-ChildItem -LiteralPath $p -Recurse -Force -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum
            $size += [long]$m.Sum
            $count += $m.Count
        }
    }
    return [pscustomobject]@{ Size = $size; Count = $count }
}

# 검사 결과 한 줄을 등록합니다.
#   Paths      : 통째로 지울 파일이나 폴더
#   ContentsOf : 폴더는 남기고 안의 내용만 지울 폴더 (임시 파일, 휴지통 등)
#   Action     : 파일 삭제가 아닌 작업 (레지스트리, 인증서 저장소, cmdkey, netsh 등)
#   Forced     : 체크를 풀 수 없는 항목 (개인 공동인증서)
#   BlockReason: 지금 지울 수 없는 이유. 값이 있으면 회색으로 표시하고 체크를 막습니다.
#   Processes  : 지우기 전에 닫아야 하는 프로그램 이름 (확장자 제외)
function Add-Found {
    param(
        [string]$Group,
        [string]$Name,
        [string]$User = '',
        [string]$Detail = '',
        [string[]]$Paths = @(),
        [string[]]$ContentsOf = @(),
        [scriptblock]$Action = $null,
        $Data = $null,
        [bool]$Default = $true,
        [bool]$Forced = $false,
        [string]$BlockReason = '',
        [string[]]$Processes = @(),
        [long]$Size = -1,
        [int]$Count = -1
    )
    # 크기를 따로 넘기지 않으면 경로에서 계산합니다.
    if ($Size -lt 0) {
        $st = Get-PathStats (@($Paths) + @($ContentsOf))
        $Size = $st.Size
        if ($Count -lt 0) { $Count = $st.Count }
    }
    $script:Items.Add([pscustomobject]@{
        Group       = $Group
        Name        = $Name
        User        = $User
        Detail      = $Detail
        Paths       = @($Paths)
        ContentsOf  = @($ContentsOf)
        Action      = $Action
        Data        = $Data
        Default     = $Default
        Forced      = $Forced
        BlockReason = $BlockReason
        Processes   = @($Processes)
        Size        = $Size
        Count       = $Count
    })
}

# =====================================================================
# 검사 대상 사용자
# =====================================================================

# 관리자 권한이 있으면 PC의 모든 사용자 프로필을, 없으면 현재 사용자만 대상으로 합니다.
function Get-TargetProfiles {
    $list = @()
    if ($script:IsAdmin) {
        try {
            $list = @(Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                Where-Object { -not $_.Special -and $_.LocalPath -and (Test-Path -LiteralPath $_.LocalPath) } |
                ForEach-Object {
                    [pscustomobject]@{
                        Path      = $_.LocalPath
                        Sid       = $_.SID
                        Name      = (Split-Path $_.LocalPath -Leaf)
                        IsCurrent = ($_.SID -eq $script:MySid)
                    }
                })
        }
        catch { $list = @() }
    }
    if (-not ($list | Where-Object { $_.IsCurrent })) {
        $list = @($list) + [pscustomobject]@{
            Path = $env:USERPROFILE; Sid = $script:MySid; Name = $env:USERNAME; IsCurrent = $true
        }
    }
    return $list
}

function Get-UserLabel($Prof) {
    if ($Prof.IsCurrent) { return ('{0} (지금 계정)' -f $Prof.Name) }
    return $Prof.Name
}

# =====================================================================
# 1. 공동인증서 (NPKI / GPKI / EPKI)
# =====================================================================

# 인증서 폴더가 있을 만한 위치를 모읍니다. 같은 경로는 한 번만 넣습니다.
function Get-CertRoots($Profiles) {
    $cands = New-Object 'System.Collections.Generic.List[object]'
    foreach ($pf in $Profiles) {
        foreach ($rel in @('AppData\LocalLow\NPKI', 'AppData\Roaming\NPKI', 'AppData\LocalLow\GPKI', 'AppData\LocalLow\EPKI')) {
            $cands.Add([pscustomobject]@{ Path = (Join-Path $pf.Path $rel); Profile = $pf })
        }
    }
    # 고정 디스크 루트의 NPKI 등은 PC 공통 위치입니다. USB 같은 이동식 디스크는 건드리지 않습니다.
    foreach ($drv in [System.IO.DriveInfo]::GetDrives()) {
        if ($drv.DriveType -eq 'Fixed' -and $drv.IsReady) {
            foreach ($n in @('NPKI', 'GPKI', 'EPKI')) {
                $cands.Add([pscustomobject]@{ Path = (Join-Path $drv.RootDirectory.FullName $n); Profile = $null })
            }
        }
    }
    foreach ($pfDir in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($pfDir) { $cands.Add([pscustomobject]@{ Path = (Join-Path $pfDir 'NPKI'); Profile = $null }) }
    }

    $seen = @{}
    $result = @()
    foreach ($c in $cands) {
        $key = $c.Path.ToLower()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        if (Test-Path -LiteralPath $c.Path) { $result += $c }
    }
    return $result
}

# 인증서 파일에서 소유자 이름과 만료일을 읽습니다.
# 읽지 못하면 폴더 이름(cn=홍길동()...,ou=...)에서 이름을 꺼냅니다.
function Get-CertInfo([string]$File, [string]$FallbackName) {
    $name = ''; $subject = ''; $expires = $null
    try {
        $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $File
        $subject = $c.Subject
        $name = $c.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        $expires = $c.NotAfter
    }
    catch {}
    if (-not $name -and $FallbackName -match 'cn=([^,]+)') { $name = $Matches[1] }
    # 국내 인증서 CN은 '이름()일련번호' 형식이라 뒤쪽 일련번호를 잘라 냅니다.
    $name = ($name -replace '\(\)\d*$', '').Trim()
    if (-not $name) { $name = '(이름을 읽지 못함)' }

    $expText = ''
    if ($expires) {
        if ($expires -lt (Get-Date)) { $expText = '만료됨' }
        else { $expText = '만료 ' + $expires.ToString('yyyy-MM-dd') }
    }
    return [pscustomobject]@{ Name = $name; Subject = $subject; ExpText = $expText }
}

# Program Files 아래는 관리자 권한이 있어야 지울 수 있습니다.
function Test-NeedsAdmin([string]$Path) {
    foreach ($pfDir in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($pfDir -and $Path.ToLower().StartsWith($pfDir.ToLower())) { return $true }
    }
    return $false
}

function Find-Certs($Profiles) {
    foreach ($root in (Get-CertRoots $Profiles)) {
        $user = 'PC 공통'
        if ($root.Profile) { $user = Get-UserLabel $root.Profile }
        $kind = (Split-Path $root.Path -Leaf).ToUpper()
        $block = ''
        if ((-not $script:IsAdmin) -and (Test-NeedsAdmin $root.Path)) {
            $block = '관리자 권한이 필요합니다. 위쪽 [관리자 권한으로 다시 실행]을 눌러 주세요.'
        }

        # NPKI 형식: <CA>\USER\<인증서 폴더>\signCert.der + signPri.key
        # 인증서 폴더 하나가 인증서 하나입니다.
        $handled = @{}
        foreach ($sc in @(Get-ChildItem -LiteralPath $root.Path -Recurse -Force -Filter 'signCert.der' -ErrorAction SilentlyContinue)) {
            $dir = $sc.Directory
            $handled[$dir.FullName.ToLower()] = $true
            $info = Get-CertInfo $sc.FullName $dir.Name

            # 법인(사업자) 인증서는 회사 업무(홈택스, 세금계산서 등)에 쓰일 수 있어서 기본 해제합니다.
            # 인증서 DN의 ou 값이나 폴더 이름에 corporation, 법인 등이 들어 있으면 법인으로 봅니다.
            $isCorp = ($info.Subject + ' ' + $dir.Name) -match 'corporation|corp4|법인|사업자'
            $ca = ''
            if ($dir.Parent -and $dir.Parent.Parent) { $ca = $dir.Parent.Parent.Name }
            $detail = (@($ca, $info.ExpText, $dir.FullName) | Where-Object { $_ }) -join ', '

            if ($kind -ne 'NPKI') {
                # GPKI, EPKI는 공무원, 교육기관 업무용 인증서입니다. 기본 체크하되 해제할 수 있습니다.
                Add-Found -Group 'cert' -Name ('[업무용] {0} ({1})' -f $info.Name, $kind) -User $user -Detail $detail `
                    -Paths @($dir.FullName) -Default $true -BlockReason $block
            }
            elseif ($isCorp) {
                Add-Found -Group 'cert' -Name ('[법인] {0}' -f $info.Name) -User $user -Detail $detail `
                    -Paths @($dir.FullName) -Default $false -BlockReason $block
            }
            else {
                Add-Found -Group 'cert' -Name ('{0} (개인)' -f $info.Name) -User $user -Detail $detail `
                    -Paths @($dir.FullName) -Default $true -Forced $true -BlockReason $block
            }
        }

        # GPKI, EPKI는 폴더 없이 <이름>_sig.cer / <이름>_sig.key / <이름>_env.cer 처럼 파일로 들어 있기도 합니다.
        # NPKI 폴더에는 CA 인증서 같은 파일이 있을 수 있어서 이 처리를 하지 않습니다.
        if ($kind -eq 'NPKI') { continue }
        $loose = @(Get-ChildItem -LiteralPath $root.Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            Where-Object { (@('.cer', '.key') -contains $_.Extension.ToLower()) -and -not $handled.ContainsKey($_.DirectoryName.ToLower()) })
        $groups = $loose | Group-Object { Join-Path $_.DirectoryName ($_.BaseName -replace '_(sig|env)$', '') }
        foreach ($g in $groups) {
            $cer = $g.Group | Where-Object { $_.Extension -ieq '.cer' } | Select-Object -First 1
            if (-not $cer) { continue }
            $info = Get-CertInfo $cer.FullName $cer.BaseName
            $detail = (@($info.ExpText, $cer.DirectoryName) | Where-Object { $_ }) -join ', '
            Add-Found -Group 'cert' -Name ('[업무용] {0} ({1})' -f $info.Name, $kind) -User $user -Detail $detail `
                -Paths @($g.Group | ForEach-Object { $_.FullName }) -Default $true -BlockReason $block
        }
    }
}

# =====================================================================
# 2. Windows 인증서 저장소 (현재 사용자)
# =====================================================================

# 개인키가 있는 인증서만 보여 줍니다. 회사 VPN, 사내 Wi-Fi(802.1X) 인증서가 여기 있을 수 있어서
# 지우면 복귀 후 회사망 접속이 안 될 수 있습니다. 그래서 모두 기본 해제합니다.
function Find-CertStore {
    $certs = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue | Where-Object { $_.HasPrivateKey })
    foreach ($c in $certs) {
        $subj = $c.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        $issuer = $c.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $true)
        $exp = '만료 ' + $c.NotAfter.ToString('yyyy-MM-dd')
        if ($c.NotAfter -lt (Get-Date)) { $exp = '만료됨' }
        Add-Found -Group 'certstore' -Name $subj -User (Get-UserLabel ([pscustomobject]@{ Name = $env:USERNAME; IsCurrent = $true })) `
            -Detail ('발급: {0}, {1}' -f $issuer, $exp) -Default $false -Data $c.Thumbprint -Size 0 -Count 1 `
            -Action {
                param($d)
                Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $d.Data) -DeleteKey -Force -ErrorAction Stop
            }
    }
}

# =====================================================================
# 3. 브라우저
# =====================================================================

$script:Browsers = @(
    @{ Name = 'Chrome'; Type = 'chromium'; Proc = 'chrome';  Rel = 'AppData\Local\Google\Chrome\User Data' },
    @{ Name = 'Edge';   Type = 'chromium'; Proc = 'msedge';  Rel = 'AppData\Local\Microsoft\Edge\User Data' },
    @{ Name = '웨일';   Type = 'chromium'; Proc = 'whale';   Rel = 'AppData\Local\Naver\Naver Whale\User Data' },
    @{ Name = 'Brave';  Type = 'chromium'; Proc = 'brave';   Rel = 'AppData\Local\BraveSoftware\Brave-Browser\User Data' },
    @{ Name = 'Firefox'; Type = 'firefox'; Proc = 'firefox'; Rel = 'AppData\Roaming\Mozilla\Firefox\Profiles' }
)

# 프로필 폴더 기준 상대 경로입니다. 북마크(Bookmarks, places.sqlite)는 기본으로 지우지 않습니다.
# SyncBlocked: 계정 동기화가 켜져 있으면 지워도 다시 내려오는 데이터입니다. 쿠키는 동기화되지 않아서 지울 수 있습니다.
$script:BrowserParts = @{
    chromium = @(
        @{ Label = '저장된 비밀번호'; SyncBlocked = $true; Default = $true
           Files = @('Login Data', 'Login Data-journal', 'Login Data For Account', 'Login Data For Account-journal') },
        @{ Label = '로그인 상태 (쿠키, 사이트 저장소)'; SyncBlocked = $false; Default = $true
           Files = @('Network\Cookies', 'Network\Cookies-journal', 'Cookies', 'Cookies-journal', 'Local Storage', 'Session Storage', 'IndexedDB') },
        @{ Label = '방문 기록, 캐시'; SyncBlocked = $true; Default = $true
           Files = @('History', 'History-journal', 'Visited Links', 'Top Sites', 'Top Sites-journal', 'Shortcuts', 'Shortcuts-journal',
                     'Sessions', 'Current Session', 'Current Tabs', 'Last Session', 'Last Tabs',
                     'Network Action Predictor', 'Network Action Predictor-journal', 'Cache', 'Code Cache', 'GPUCache') },
        @{ Label = '자동완성 (주소, 카드, 입력 기록)'; SyncBlocked = $true; Default = $true
           Files = @('Web Data', 'Web Data-journal') }
    )
    firefox = @(
        @{ Label = '저장된 비밀번호'; SyncBlocked = $true; Default = $true
           Files = @('logins.json', 'logins-backup.json', 'key4.db', 'key3.db', 'signons.sqlite') },
        @{ Label = '로그인 상태 (쿠키, 사이트 저장소)'; SyncBlocked = $false; Default = $true
           Files = @('cookies.sqlite', 'cookies.sqlite-wal', 'webappsstore.sqlite', 'storage\default') },
        @{ Label = '자동완성 (주소, 카드, 입력 기록)'; SyncBlocked = $true; Default = $true
           Files = @('formhistory.sqlite', 'autofill-profiles.json') },
        @{ Label = '열린 탭 복원 기록'; SyncBlocked = $false; Default = $true
           Files = @('sessionstore.jsonlz4', 'sessionstore-backups') },
        # Firefox는 방문 기록과 북마크가 한 파일(places.sqlite)에 있어서 따로 두고 기본 해제합니다.
        @{ Label = '방문 기록 (북마크도 함께 지워짐)'; SyncBlocked = $true; Default = $false
           Files = @('places.sqlite', 'places.sqlite-wal', 'favicons.sqlite', 'favicons.sqlite-wal') }
    )
}

# Chromium 계열 브라우저의 Preferences에서 로그인된 계정 메일을 찾습니다.
# JSON 전체를 파싱하면 PowerShell 5.1에서 실패하는 경우가 있어 정규식으로 찾습니다.
function Get-BrowserAccount([string]$Type, [string]$ProfileDir) {
    try {
        if ($Type -eq 'chromium') {
            $txt = [System.IO.File]::ReadAllText((Join-Path $ProfileDir 'Preferences'))
            $m = [regex]::Match($txt, '"account_info"\s*:\s*\[\s*\{[^\]]*?"email"\s*:\s*"([^"]+)"')
            if ($m.Success) { return $m.Groups[1].Value }
        }
        else {
            $f = Join-Path $ProfileDir 'signedInUser.json'
            if (Test-Path -LiteralPath $f) {
                $m = [regex]::Match([System.IO.File]::ReadAllText($f), '"email"\s*:\s*"([^"]+)"')
                if ($m.Success) { return $m.Groups[1].Value }
                return 'Firefox 계정'
            }
        }
    }
    catch {}
    return ''
}

function Find-Browsers($Profiles) {
    foreach ($pf in $Profiles) {
        $user = Get-UserLabel $pf
        foreach ($b in $script:Browsers) {
            $base = Join-Path $pf.Path $b.Rel
            if (-not (Test-Path -LiteralPath $base)) { continue }

            # Chromium은 Preferences 파일이 있는 폴더(Default, Profile 1, ...)가 프로필입니다.
            $profileDirs = @(Get-ChildItem -LiteralPath $base -Directory -Force -ErrorAction SilentlyContinue)
            if ($b.Type -eq 'chromium') {
                $profileDirs = @($profileDirs | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Preferences') })
            }

            foreach ($pd in $profileDirs) {
                $email = Get-BrowserAccount $b.Type $pd.FullName
                foreach ($part in $script:BrowserParts[$b.Type]) {
                    $paths = @($part.Files | ForEach-Object { Join-Path $pd.FullName $_ } |
                        Where-Object { Test-Path -LiteralPath $_ })
                    if ($paths.Count -eq 0) { continue }

                    $block = ''
                    if ($email -and $part.SyncBlocked) {
                        $block = ("계정({0})이 로그인되어 동기화 중이면 지워도 다시 내려옵니다. " +
                                  "브라우저에서 Ctrl+Shift+Delete를 누르고 기간을 '전체 기간'으로 골라 직접 지워 주세요.") -f $email
                    }
                    $detail = $pd.FullName
                    if ($email) { $detail = '로그인된 계정: ' + $email }

                    Add-Found -Group 'browser' -Name ('{0} ({1}) - {2}' -f $b.Name, $pd.Name, $part.Label) -User $user `
                        -Detail $detail -Paths $paths -Default $part.Default -BlockReason $block -Processes @($b.Proc)
                }
            }
        }
    }
}

# =====================================================================
# 4. 메신저
# =====================================================================

# Guard: 프로그램 본체가 데이터 폴더 안에 설치된 경우(사용자별 설치)를 대비한 안전장치입니다.
#        Exe가 폴더 안에 있으면 폴더 전체 대신 Sub 하위 폴더만 지웁니다.
$script:Messengers = @(
    @{ Name = '카카오톡'; Proc = 'KakaoTalk'; Detail = '로그인 정보, 대화 내용, 받은 파일 캐시'
       Rels = @('AppData\Local\Kakao\KakaoTalk'); Guard = @{ Exe = 'KakaoTalk.exe'; Sub = 'users' } },
    @{ Name = '텔레그램'; Proc = 'Telegram'; Detail = '로그인 정보, 대화 캐시'
       Rels = @('AppData\Roaming\Telegram Desktop\tdata'); Guard = $null },
    @{ Name = '라인'; Proc = 'LINE'; Detail = '로그인 정보, 대화 캐시'
       Rels = @('AppData\Local\LINE\Data'); Guard = $null },
    @{ Name = '디스코드'; Proc = 'Discord'; Detail = '로그인 정보'
       Rels = @('AppData\Roaming\discord\Local Storage', 'AppData\Roaming\discord\Session Storage',
                'AppData\Roaming\discord\Network\Cookies', 'AppData\Roaming\discord\Cookies'); Guard = $null }
)

function Find-Messengers($Profiles) {
    foreach ($pf in $Profiles) {
        foreach ($m in $script:Messengers) {
            $paths = @()
            foreach ($rel in $m.Rels) {
                $p = Join-Path $pf.Path $rel
                if (-not (Test-Path -LiteralPath $p)) { continue }
                if ($m.Guard -and (Test-Path -LiteralPath (Join-Path $p $m.Guard.Exe))) {
                    $p = Join-Path $p $m.Guard.Sub
                    if (-not (Test-Path -LiteralPath $p)) { continue }
                }
                $paths += $p
            }
            if ($paths.Count -eq 0) { continue }
            Add-Found -Group 'messenger' -Name $m.Name -User (Get-UserLabel $pf) -Detail $m.Detail `
                -Paths $paths -Default $true -Processes @($m.Proc)
        }
    }
}

# =====================================================================
# 5. 개인 파일 (바탕화면, 문서, 다운로드, 사진)
# =====================================================================

# 파일 이름에 이 단어가 들어 있으면 개인 서류일 가능성이 높다고 봅니다.
# 업무 서류와 겹칠 수 있어서 화면에서는 모두 기본 해제로 보여 줍니다.
$script:FileKeywords = @(
    '주민등록', '등본', '초본', '가족관계', '기본증명', '혼인관계', '신분증', '운전면허', '여권',
    '통장', '계좌', '원천징수', '급여명세', '연말정산', '근로소득', '소득금액', '건강보험',
    '진단서', '소견서', '처방전', '검진', '이력서', '자기소개서', '경력증명', '재직증명',
    '졸업증명', '성적증명', '임대차', '전세', '월세', '보험증권', '청약', '대출', '카드명세',
    '출생', '임신', '출산', '산모', '육아휴직', 'resume', 'passport'
)
$script:FileRegex = ($script:FileKeywords | ForEach-Object { [regex]::Escape($_) }) -join '|'
$script:FileHitLimit = 300

# 사용자 폴더 위치를 구합니다. 현재 사용자는 OneDrive 등으로 옮겨진 실제 위치를 따라갑니다.
function Get-UserFolders($Prof) {
    $list = @()
    if ($Prof.IsCurrent) {
        $list += [Environment]::GetFolderPath('Desktop')
        $list += [Environment]::GetFolderPath('MyDocuments')
        $list += [Environment]::GetFolderPath('MyPictures')
        $dl = $null
        try {
            $dl = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders' -ErrorAction Stop).'{374DE290-123F-4565-9164-39C4925E467B}'
        }
        catch {}
        if (-not $dl) { $dl = Join-Path $Prof.Path 'Downloads' }
        $list += $dl
    }
    else {
        foreach ($n in @('Desktop', 'Documents', 'Downloads', 'Pictures')) { $list += (Join-Path $Prof.Path $n) }
    }
    return @($list | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique)
}

function Find-PersonalFiles($Profiles) {
    foreach ($pf in $Profiles) {
        $user = Get-UserLabel $pf
        $roots = Get-UserFolders $pf

        # 카카오톡 받은 파일 폴더는 통째로 하나의 항목으로 보여 줍니다. (기본 체크)
        $kakaoDirs = @()
        foreach ($r in $roots) {
            $k = Join-Path $r '카카오톡 받은 파일'
            if ((Test-Path -LiteralPath $k) -and ($kakaoDirs -notcontains $k)) { $kakaoDirs += $k }
        }
        foreach ($k in $kakaoDirs) {
            Add-Found -Group 'files' -Name '카카오톡 받은 파일 (폴더 전체)' -User $user -Detail $k -Paths @($k) -Default $true
        }

        $seen = @{}
        $hits = 0
        foreach ($r in $roots) {
            Set-Status ('검사 중: 개인 파일 ({0})' -f $r)
            foreach ($f in (Get-ChildItem -LiteralPath $r -Recurse -Force -File -ErrorAction SilentlyContinue)) {
                if ($seen.ContainsKey($f.FullName)) { continue }
                $seen[$f.FullName] = $true

                # 카카오톡 받은 파일 안은 위에서 폴더째 처리합니다.
                $inKakao = $false
                foreach ($k in $kakaoDirs) { if ($f.FullName.StartsWith($k + '\')) { $inKakao = $true; break } }
                if ($inKakao) { continue }

                # Mac에서 넘어온 파일은 한글이 자모 분리(NFD) 형태라 NFC로 맞춘 뒤 비교합니다.
                $name = $f.Name.Normalize([Text.NormalizationForm]::FormC)
                if ($name -notmatch $script:FileRegex) { continue }

                $hits++
                if ($hits -gt $script:FileHitLimit) { break }
                Add-Found -Group 'files' -Name $name -User $user -Detail $f.DirectoryName `
                    -Paths @($f.FullName) -Default $false -Size $f.Length -Count 1
            }
            if ($hits -gt $script:FileHitLimit) {
                Write-Log ('{0}: 개인 파일 후보가 {1}개를 넘어서 나머지는 표시하지 않았습니다.' -f $user, $script:FileHitLimit)
                break
            }
        }
    }
}

# =====================================================================
# 6. 사용 기록, 휴지통, 임시 파일
# =====================================================================

# 실행 창(Win+R), 탐색기 주소창과 검색, 최근 문서, 파일 열기/저장 창 기록입니다. 현재 사용자만 지울 수 있습니다.
$script:RegHistoryKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU'
)

function Find-History($Profiles) {
    foreach ($pf in $Profiles) {
        $user = Get-UserLabel $pf

        # 최근 항목 바로가기와 작업 표시줄 점프 목록은 파일이라 다른 사용자도 지울 수 있습니다.
        $recent = Join-Path $pf.Path 'AppData\Roaming\Microsoft\Windows\Recent'
        if (Test-Path -LiteralPath $recent) {
            Add-Found -Group 'history' -Name '최근 문서 목록, 점프 목록' -User $user -Detail $recent -ContentsOf @($recent) -Default $true
        }

        $temp = Join-Path $pf.Path 'AppData\Local\Temp'
        if (Test-Path -LiteralPath $temp) {
            Add-Found -Group 'history' -Name '임시 파일' -User $user -Detail ($temp + ' (사용 중인 파일은 건너뜀)') `
                -ContentsOf @($temp) -Default $true
        }

        # 휴지통은 드라이브마다 $Recycle.Bin\<사용자 SID> 폴더에 있습니다.
        $bins = @()
        foreach ($drv in [System.IO.DriveInfo]::GetDrives()) {
            if ($drv.DriveType -ne 'Fixed' -or -not $drv.IsReady) { continue }
            $bin = Join-Path (Join-Path $drv.RootDirectory.FullName '$Recycle.Bin') $pf.Sid
            if (Test-Path -LiteralPath $bin) { $bins += $bin }
        }
        if ($bins.Count -gt 0) {
            Add-Found -Group 'history' -Name '휴지통' -User $user -Detail ($bins -join ', ') -ContentsOf $bins -Default $true
        }

        if (-not $pf.IsCurrent) { continue }

        # 이하 현재 사용자 전용 항목입니다.
        $regCount = 0
        foreach ($k in $script:RegHistoryKeys) {
            if (Test-Path -LiteralPath $k) {
                $it = Get-Item -LiteralPath $k -ErrorAction SilentlyContinue
                if ($it) { $regCount += $it.ValueCount + $it.SubKeyCount }
            }
        }
        if ($regCount -gt 0) {
            Add-Found -Group 'history' -Name '실행 창, 탐색기 검색, 파일 열기 창 기록' -User $user `
                -Detail '레지스트리 기록 (로그아웃 후 다시 로그인하면 완전히 반영됨)' -Default $true -Size 0 -Count $regCount `
                -Action {
                    param($d)
                    foreach ($k in $script:RegHistoryKeys) {
                        if (Test-Path -LiteralPath $k) { Remove-Item -LiteralPath $k -Recurse -Force -ErrorAction Stop }
                    }
                }
        }

        Add-Found -Group 'history' -Name '클립보드, 클립보드 기록 (Win+V)' -User $user -Detail '복사해 둔 내용' `
            -Default $true -Size 0 -Count 0 -Action {
                param($d)
                [System.Windows.Forms.Clipboard]::Clear()
                try {
                    $null = [Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime]
                    [void][Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory()
                }
                catch {
                    Write-Log '  클립보드 기록(Win+V)은 지우지 못했습니다. 설정 > 시스템 > 클립보드에서 [지우기]를 눌러 주세요.'
                }
            }
    }
}

# =====================================================================
# 7. Windows 자격 증명 관리자 (현재 사용자)
# =====================================================================

# cmdkey 출력은 Windows 언어에 따라 '대상:' 또는 'Target:'으로 나옵니다.
function Find-Credentials($Profiles) {
    $user = Get-UserLabel ($Profiles | Where-Object { $_.IsCurrent } | Select-Object -First 1)
    $lines = @(& $script:CmdkeyExe /list 2>$null)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^\s*(?:대상|Target)\s*:\s*(.+?)\s*$') { continue }
        $target = $Matches[1]
        $account = ''
        for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) {
            if ($lines[$j] -match '^\s*(?:사용자|User)\s*:\s*(.+?)\s*$') { $account = $Matches[1]; break }
        }
        $display = $target -replace '^[^:=]+:target=', ''
        $detail = '계정: ' + $account
        if (-not $account) { $detail = $target }
        Add-Found -Group 'cred' -Name $display -User $user -Detail $detail -Default $false -Data $target -Size 0 -Count 1 `
            -Action {
                param($d)
                $p = Start-Process -FilePath $script:CmdkeyExe -ArgumentList ('/delete:"{0}"' -f $d.Data) `
                    -Wait -PassThru -WindowStyle Hidden
                if ($p.ExitCode -ne 0) { throw ('cmdkey 종료 코드 {0}' -f $p.ExitCode) }
            }
    }
}

# =====================================================================
# 8. Wi-Fi 저장 목록 (PC 공통)
# =====================================================================

function Find-Wifi {
    $out = @(& $script:NetshExe wlan show profiles 2>$null)
    $note = '저장된 Wi-Fi와 비밀번호'
    if (-not $script:IsAdmin) { $note += ' (관리자 권한이 필요할 수 있음)' }
    foreach ($l in $out) {
        if ($l -notmatch '^\s*(?:All User Profile|모든 사용자 프로필)\s*:\s*(.+?)\s*$') { continue }
        Add-Found -Group 'wifi' -Name $Matches[1] -User 'PC 공통' -Detail $note -Default $false -Data $Matches[1] -Size 0 -Count 1 `
            -Action {
                param($d)
                $p = Start-Process -FilePath $script:NetshExe -ArgumentList ('wlan delete profile name="{0}"' -f $d.Data) `
                    -Wait -PassThru -WindowStyle Hidden
                if ($p.ExitCode -ne 0) { throw ('netsh 종료 코드 {0}' -f $p.ExitCode) }
            }
    }
}

# =====================================================================
# 삭제
# =====================================================================

# Paths는 통째로, ContentsOf는 안의 내용만 지웁니다.
# Paths 실패는 개수로 돌려주고, ContentsOf는 사용 중인 파일이 늘 있기 마련이라 로그만 남깁니다.
function Remove-Targets($d) {
    $fail = 0
    foreach ($p in $d.Paths) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop }
        catch {
            $fail++
            Write-Log ('  실패: {0} ({1})' -f $p, $_.Exception.Message)
        }
    }
    foreach ($dir in $d.ContentsOf) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $skipped = 0
        foreach ($child in @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
            if ($child.Name -eq 'desktop.ini') { continue }
            try { Remove-Item -LiteralPath $child.FullName -Recurse -Force -ErrorAction Stop }
            catch { $skipped++ }
        }
        if ($skipped -gt 0) { Write-Log ('  사용 중이라 건너뜀: {0} 안의 {1}개' -f $dir, $skipped) }
    }
    return $fail
}

function Get-GroupTitle([string]$Key) {
    foreach ($g in $script:GroupOrder) { if ($g.Key -eq $Key) { return ($g.Title -replace ' \(.*$', '') } }
    return $Key
}

function Show-Message([string]$Text, [string]$Title = '개인정보 정리 도구',
                      $Buttons = [System.Windows.Forms.MessageBoxButtons]::OK,
                      $Icon = [System.Windows.Forms.MessageBoxIcon]::Information) {
    return [System.Windows.Forms.MessageBox]::Show($script:Form, $Text, $Title, $Buttons, $Icon)
}

function Invoke-Clean {
    $sel = @(foreach ($it in $lv.CheckedItems) { $it.Tag })
    if ($sel.Count -eq 0) { [void](Show-Message '선택한 항목이 없습니다.'); return }
    $dry = $chkDry.Checked

    if (-not $dry) {
        $msg = '선택한 {0}개 항목을 삭제합니다. 삭제한 뒤에는 되돌릴 수 없습니다.' -f $sel.Count
        $certCount = @($sel | Where-Object { $_.Group -eq 'cert' }).Count
        if ($certCount -gt 0) {
            $msg += "`n`n공동인증서 {0}개가 포함되어 있습니다. 휴대폰이나 USB에 백업해 두지 않았다면 나중에 은행에서 다시 발급받아야 합니다." -f $certCount
        }
        $fileCount = @($sel | Where-Object { $_.Group -eq 'files' }).Count
        if ($fileCount -gt 0) {
            $msg += "`n`n개인 파일 {0}개가 포함되어 있습니다. 업무 파일이 섞여 있지 않은지 한 번 더 확인해 주세요." -f $fileCount
        }
        $msg += "`n`n계속할까요?"
        $r = Show-Message $msg '삭제 확인' ([System.Windows.Forms.MessageBoxButtons]::YesNo) ([System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }

        # 브라우저, 메신저가 켜져 있으면 파일이 잠겨 있어서 지워지지 않습니다.
        # 지금 세션에서 실행 중인 것만 확인합니다. (다른 사용자 프로그램은 종료할 수 없습니다.)
        $procNames = @($sel | ForEach-Object { $_.Processes } | Where-Object { $_ } | Sort-Object -Unique)
        if ($procNames.Count -gt 0) {
            $mySession = (Get-Process -Id $PID).SessionId
            $running = @(Get-Process -Name $procNames -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession })
            if ($running.Count -gt 0) {
                $names = (@($running | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)) -join ', '
                $r = Show-Message (("다음 프로그램이 실행 중이라 파일이 잠겨 있습니다.`n`n{0}`n`n[예]를 누르면 자동으로 종료하고 계속합니다. " +
                                    "작성 중인 내용이 있다면 [아니요]를 누르고, 저장한 뒤 직접 닫아 주세요.") -f $names) `
                    '프로그램 종료' ([System.Windows.Forms.MessageBoxButtons]::YesNo) ([System.Windows.Forms.MessageBoxIcon]::Warning)
                if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
                $running | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }
        }
    }

    $mode = '삭제'
    if ($dry) { $mode = '연습 모드' }
    Write-Log ('===== {0} 시작: {1}개 항목 =====' -f $mode, $sel.Count)
    $ok = 0; $bad = 0
    foreach ($d in $sel) {
        Set-Status ('처리 중: {0}' -f $d.Name)
        $label = '{0} > {1} [{2}]' -f (Get-GroupTitle $d.Group), $d.Name, $d.User
        if ($dry) {
            Write-Log ('[연습] ' + $label)
            foreach ($p in $d.Paths) { Write-Log ('    ' + $p) }
            foreach ($p in $d.ContentsOf) { Write-Log ('    ' + $p + '\* (안의 내용)') }
            if ($d.Action) { Write-Log '    (파일이 아닌 설정 항목)' }
            $ok++
            continue
        }
        try {
            $fail = Remove-Targets $d
            if ($d.Action) { & $d.Action $d }
            if ($fail -gt 0) { $bad++; Write-Log ('일부 실패: ' + $label) }
            else { $ok++; Write-Log ('완료: ' + $label) }
        }
        catch {
            $bad++
            Write-Log ('실패: {0} ({1})' -f $label, $_.Exception.Message)
        }
    }
    Write-Log ('===== {0} 끝: 성공 {1}, 실패 {2} =====' -f $mode, $ok, $bad)

    $summary = '삭제를 마쳤습니다. 성공 {0}개, 실패 {1}개입니다.' -f $ok, $bad
    if ($dry) { $summary = '연습 모드라 실제로는 지우지 않았습니다. 지울 예정인 목록을 로그에 기록했습니다.' }
    $summary += "`n`n로그 파일: " + $script:LogPath
    if ($bad -gt 0) { $summary += "`n`n실패한 항목은 로그에서 이유를 확인할 수 있습니다." }
    [void](Show-Message $summary)

    if (-not $dry) { Invoke-Scan }
}

# =====================================================================
# 화면
# =====================================================================

function Set-Status([string]$Text) {
    if ($lblSummary) { $lblSummary.Text = $Text }
    [System.Windows.Forms.Application]::DoEvents()
}

function Update-Summary {
    if ($script:Populating) { return }
    $n = 0
    $size = [long]0
    foreach ($it in $lv.CheckedItems) {
        $n++
        if ($it.Tag.Size -gt 0) { $size += $it.Tag.Size }
    }
    $lblSummary.Text = '선택: {0}개 항목, 약 {1}' -f $n, (Format-Size $size 0)
}

function Show-Items {
    $script:Populating = $true
    $lv.BeginUpdate()
    $lv.Items.Clear()
    $lv.Groups.Clear()

    $groups = @{}
    foreach ($g in $script:GroupOrder) {
        $cnt = @($script:Items | Where-Object { $_.Group -eq $g.Key }).Count
        if ($cnt -eq 0) { continue }
        $grp = New-Object System.Windows.Forms.ListViewGroup($g.Key, ('{0}  -  {1}개' -f $g.Title, $cnt))
        [void]$lv.Groups.Add($grp)
        $groups[$g.Key] = $grp
    }

    $boldFont = New-Object System.Drawing.Font($lv.Font, [System.Drawing.FontStyle]::Bold)
    foreach ($d in $script:Items) {
        $name = $d.Name
        $detail = $d.Detail
        if ($d.BlockReason) { $name = '[처리 불가] ' + $name; $detail = $d.BlockReason + '  |  ' + $d.Detail }
        elseif ($d.Forced) { $name = '[필수] ' + $name }

        $it = New-Object System.Windows.Forms.ListViewItem($name)
        [void]$it.SubItems.Add($d.User)
        [void]$it.SubItems.Add((Format-Size $d.Size $d.Count))
        [void]$it.SubItems.Add($detail)
        $it.Group = $groups[$d.Group]
        $it.Tag = $d
        $it.ToolTipText = $detail

        # 체크 상태는 목록에 넣기 전에 정해 두어야 ItemCheck 이벤트가 끼어들지 않습니다.
        if ($d.BlockReason) {
            $it.Checked = $false
            $it.ForeColor = [System.Drawing.Color]::Gray
        }
        elseif ($d.Forced) {
            $it.Checked = $true
            $it.ForeColor = [System.Drawing.Color]::DarkRed
            $it.Font = $boldFont
        }
        else {
            $it.Checked = $d.Default
        }
        [void]$lv.Items.Add($it)
    }
    $lv.EndUpdate()
    $script:Populating = $false
    Update-Summary
}

function Invoke-Scan {
    $btnScan.Enabled = $false
    $btnClean.Enabled = $false
    try {
        $script:Items.Clear()
        $lv.Items.Clear()
        $profiles = @(Get-TargetProfiles)
        Write-Log ('검사 시작: 사용자 {0}명 ({1})' -f $profiles.Count, (($profiles | ForEach-Object { $_.Name }) -join ', '))

        # 검사 하나가 실패해도 나머지는 계속합니다.
        $steps = @(
            @{ Label = '공동인증서';           Run = { Find-Certs $profiles } },
            @{ Label = 'Windows 인증서 저장소'; Run = { Find-CertStore } },
            @{ Label = '브라우저';             Run = { Find-Browsers $profiles } },
            @{ Label = '메신저';               Run = { Find-Messengers $profiles } },
            @{ Label = '개인 파일';            Run = { Find-PersonalFiles $profiles } },
            @{ Label = '사용 기록';            Run = { Find-History $profiles } },
            @{ Label = '자격 증명';            Run = { Find-Credentials $profiles } },
            @{ Label = 'Wi-Fi';               Run = { Find-Wifi } }
        )
        foreach ($s in $steps) {
            Set-Status ('검사 중: {0}...' -f $s.Label)
            try { & $s.Run }
            catch { Write-Log ('{0} 검사 중 오류: {1}' -f $s.Label, $_.Exception.Message) }
        }
        Write-Log ('검사 완료: {0}개 항목' -f $script:Items.Count)
        Show-Items
    }
    finally {
        $btnScan.Enabled = $true
        $btnClean.Enabled = $true
    }
}

# 이벤트 처리 중 오류가 나도 창이 조용히 멈추지 않도록 메시지로 알립니다.
function Invoke-Safe([scriptblock]$Block) {
    try { & $Block }
    catch {
        Write-Log ('오류: ' + $_.Exception.Message)
        [void](Show-Message ("오류가 발생했습니다.`n`n{0}`n`n로그 파일: {1}" -f $_.Exception.Message, $script:LogPath) `
            '오류' ([System.Windows.Forms.MessageBoxButtons]::OK) ([System.Windows.Forms.MessageBoxIcon]::Error))
    }
}

# ---------- 창 구성 ----------

$form = New-Object System.Windows.Forms.Form
$script:Form = $form
$form.Text = '개인정보 정리 도구'
$form.Size = New-Object System.Drawing.Size(1150, 780)
$form.MinimumSize = New-Object System.Drawing.Size(800, 500)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Malgun Gothic', 9)

# 맨 위: 실행 계정, 권한, 검사 범위 안내
$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Dock = 'Top'
$lblInfo.Height = 64
$lblInfo.Padding = New-Object System.Windows.Forms.Padding(8, 6, 8, 0)
$adminText = '없음'
if ($script:IsAdmin) { $adminText = '있음' }
$info = '실행 계정: {0}     관리자 권한: {1}' -f $script:Identity.Name, $adminText
if ($script:IsAdmin) {
    $info += "`n모든 사용자를 검사합니다. 다른 사용자는 파일 항목만 정리합니다. (인증서 저장소, 자격 증명, 실행 기록은 지금 계정만)"
}
else {
    $info += "`n관리자 권한이 없어서 지금 계정만 검사합니다. 다른 사용자까지 정리하려면 [관리자 권한으로 다시 실행]을 누르세요."
}
if ($script:OtherAccount) {
    $info += "`n주의: 로그인한 사용자({0})와 실행 계정이 다릅니다. 로그인한 사용자의 인증서 저장소, 자격 증명, 실행 기록은 처리되지 않습니다." -f $script:ConsoleUser
    $lblInfo.ForeColor = [System.Drawing.Color]::DarkRed
}
$lblInfo.Text = $info

# 버튼 줄
$btnBar = New-Object System.Windows.Forms.FlowLayoutPanel
$btnBar.Dock = 'Top'
$btnBar.Height = 40
$btnBar.Padding = New-Object System.Windows.Forms.Padding(4)

$btnScan = New-Object System.Windows.Forms.Button
$btnScan.Text = '다시 검사'
$btnScan.AutoSize = $true

$btnDefault = New-Object System.Windows.Forms.Button
$btnDefault.Text = '처음 선택으로 되돌리기'
$btnDefault.AutoSize = $true

$btnFiles = New-Object System.Windows.Forms.Button
$btnFiles.Text = '개인 파일 모두 체크'
$btnFiles.AutoSize = $true

$btnAdmin = New-Object System.Windows.Forms.Button
$btnAdmin.Text = '관리자 권한으로 다시 실행'
$btnAdmin.AutoSize = $true
$btnAdmin.Visible = -not $script:IsAdmin

$btnBar.Controls.AddRange(@($btnScan, $btnDefault, $btnFiles, $btnAdmin))

# 가운데: 검사 결과 목록
$lv = New-Object System.Windows.Forms.ListView
$lv.Dock = 'Fill'
$lv.View = 'Details'
$lv.CheckBoxes = $true
$lv.FullRowSelect = $true
$lv.ShowItemToolTips = $true
$lv.ShowGroups = $true
[void]$lv.Columns.Add('항목', 380)
[void]$lv.Columns.Add('사용자', 130)
[void]$lv.Columns.Add('크기', 110)
[void]$lv.Columns.Add('상세', 520)

# 아래: 연습 모드, 선택 요약, 삭제 버튼
$bottomBar = New-Object System.Windows.Forms.FlowLayoutPanel
$bottomBar.Dock = 'Bottom'
$bottomBar.Height = 44
$bottomBar.Padding = New-Object System.Windows.Forms.Padding(4)

$btnClean = New-Object System.Windows.Forms.Button
$btnClean.AutoSize = $true
$btnClean.Font = New-Object System.Drawing.Font($form.Font, [System.Drawing.FontStyle]::Bold)

$chkDry = New-Object System.Windows.Forms.CheckBox
$chkDry.Text = '연습 모드 (지우지 않고 로그만 남김)'
$chkDry.AutoSize = $true
$chkDry.Margin = New-Object System.Windows.Forms.Padding(12, 8, 12, 0)
$chkDry.Checked = [bool]$ScanOnly

$lblSummary = New-Object System.Windows.Forms.Label
$lblSummary.AutoSize = $true
$lblSummary.Margin = New-Object System.Windows.Forms.Padding(12, 9, 0, 0)

$bottomBar.Controls.AddRange(@($btnClean, $chkDry, $lblSummary))

# 맨 아래: 진행 로그
$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Dock = 'Bottom'
$logBox.Height = 140
$logBox.Multiline = $true
$logBox.ReadOnly = $true
$logBox.ScrollBars = 'Vertical'
$script:LogBox = $logBox

# Dock은 나중에 추가한 컨트롤이 바깥쪽에 붙습니다.
# 그래서 Fill(목록)을 먼저 넣고, 위쪽은 버튼 줄 > 안내 순, 아래쪽은 삭제 줄 > 로그 순으로 넣습니다.
$form.Controls.Add($lv)
$form.Controls.Add($btnBar)
$form.Controls.Add($lblInfo)
$form.Controls.Add($bottomBar)
$form.Controls.Add($logBox)

function Update-CleanButton {
    if ($chkDry.Checked) { $btnClean.Text = '연습 실행 (지우지 않음)' }
    else { $btnClean.Text = '선택 항목 삭제' }
}
Update-CleanButton

# ---------- 이벤트 ----------

# 필수 항목은 체크를 풀 수 없고, 처리 불가 항목은 체크할 수 없게 막습니다.
$lv.Add_ItemCheck({
    param($sender, $e)
    $d = $sender.Items[$e.Index].Tag
    if (-not $d) { return }
    if ($d.BlockReason) { $e.NewValue = [System.Windows.Forms.CheckState]::Unchecked }
    elseif ($d.Forced) { $e.NewValue = [System.Windows.Forms.CheckState]::Checked }
})
$lv.Add_ItemChecked({ Update-Summary })

$chkDry.Add_CheckedChanged({ Update-CleanButton })

$btnScan.Add_Click({ Invoke-Safe { Invoke-Scan } })
$btnClean.Add_Click({ Invoke-Safe { Invoke-Clean } })

$btnDefault.Add_Click({
    foreach ($it in $lv.Items) {
        $d = $it.Tag
        $it.Checked = ($d.Forced -or $d.Default) -and -not $d.BlockReason
    }
})

$btnFiles.Add_Click({
    foreach ($it in $lv.Items) { if ($it.Tag.Group -eq 'files' -and -not $it.Tag.BlockReason) { $it.Checked = $true } }
})

$btnAdmin.Add_Click({
    try {
        $argList = '-NoProfile -ExecutionPolicy Bypass -STA -File "{0}"' -f $script:SelfPath
        if ($chkDry.Checked) { $argList += ' -ScanOnly' }
        Start-Process -FilePath $script:PsExe -ArgumentList $argList -Verb RunAs -ErrorAction Stop
        $form.Close()
    }
    catch {
        [void](Show-Message '관리자 권한으로 실행하지 못했습니다. 지금 계정 기준으로 계속 사용할 수 있습니다.')
    }
})

# 창이 뜨면 바로 검사를 시작합니다.
$form.Add_Shown({
    $form.Activate()
    Invoke-Safe { Invoke-Scan }
})

Write-Log ('개인정보 정리 도구 시작. 실행 계정: {0}, 관리자: {1}, 로그인 사용자: {2}' -f `
    $script:Identity.Name, $script:IsAdmin, $script:ConsoleUser)

try {
    [void]$form.ShowDialog()
}
catch {
    Write-Log ('치명적 오류: ' + $_.Exception.Message)
    [System.Windows.Forms.MessageBox]::Show(("오류가 발생해 종료합니다.`n`n{0}`n`n로그 파일: {1}" -f $_.Exception.Message, $script:LogPath)) | Out-Null
    exit 1
}
exit 0
