<#
.SYNOPSIS
    UI smoke test of the launcher through Windows UI Automation.

.DESCRIPTION
    Starts the exe and drives the real window: main card, single instance, settings menu, About, the
    New profile form (name hint, defaults, validation, Cancel), closing to the tray and --tray start.
    It never creates, edits or removes a profile and never clicks anything that asks for UAC.

    Run it with Windows PowerShell 5.1 (powershell.exe), which ships the UI Automation client, from a
    signed-in desktop. Windows of the app appear and close on their own; don't use the mouse meanwhile.
    Exits with 1 when a check fails.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tests\ui-smoke.ps1 -Exe publish\ClaudeMultiAccount.exe
#>
param([string]$Exe = (Join-Path $PSScriptRoot '..\publish\ClaudeMultiAccount.exe'))

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this script with Windows PowerShell 5.1 (powershell.exe).' }
$Exe = (Resolve-Path $Exe).Path
if (Get-Process ClaudeMultiAccount -ErrorAction SilentlyContinue) {
    throw 'Claude Multi-Account is already running. Exit it from the tray first: the test needs to own the single instance.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace '' -Name Win32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);'
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$results = New-Object System.Collections.Generic.List[object]
$started = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

function Check($name, $ok, $detail = '') {
    $results.Add([pscustomobject]@{ Ok = [bool]$ok; Check = $name; Detail = "$detail" })
}
function Wait-Until([scriptblock]$cond, [int]$ms = 15000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        try { $r = & $cond; if ($r) { return $r } } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Start-App([string[]]$arguments = @()) {
    $p = if ($arguments) { Start-Process $Exe -ArgumentList $arguments -PassThru } else { Start-Process $Exe -PassThru }
    $started.Add($p)
    $p
}
# Top-level windows of the process plus the windows they own (owned windows nest: main -> New profile -> message box).
function Windows-Of([int]$procId) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $isWindow = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Window)
    $list = New-Object System.Collections.Generic.List[object]; $seen = @{}
    $queue = New-Object System.Collections.Generic.Queue[object]
    foreach ($top in $AE::RootElement.FindAll($TS::Children, $c)) { $queue.Enqueue($top) }
    while ($queue.Count -gt 0) {
        $w = $queue.Dequeue()
        $key = $w.GetRuntimeId() -join '.'
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = 1; $list.Add($w)
        foreach ($child in $w.FindAll($TS::Children, $isWindow)) { $queue.Enqueue($child) }
    }
    $list.ToArray()
}
function Visible-Windows([int]$procId) { @(Windows-Of $procId | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.ControlType -eq $CT::Window }) }
function All([System.Windows.Automation.AutomationElement]$root, $type) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
    @($root.FindAll($TS::Descendants, $c))
}
function ById($root, [string]$id) {
    $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
}
function Named($root, $type, [string[]]$names) {
    All $root $type | Where-Object { $names -contains $_.Current.Name } | Select-Object -First 1
}
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Text($el, [string]$v) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Get-Text($el) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Toggle-State($el) { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Button-With-Text($root, [string[]]$names) {
    $el = Named $root $CT::Text $names
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($el -and $el.Current.ControlType -ne $CT::Button) { $el = $walker.GetParent($el) }
    $el
}
function Menu-Items([int]$procId) {
    $seen = @{}
    @(Windows-Of $procId | ForEach-Object { All $_ $CT::MenuItem } | Where-Object {
        $key = $_.GetRuntimeId() -join '.'
        if ($seen.ContainsKey($key)) { $false } else { $seen[$key] = 1; $true } })
}
function Open-Settings([int]$procId, $main) {
    Invoke (Named $main $CT::Button @([string][char]0x2699))
    Wait-Until { $m = Menu-Items $procId; if ($m.Count -ge 4) { , $m } }
}
# A message box of the app: returns its text and closes it. Its Win32 controls can surface as plain panes,
# so the text is read from the Static labels and the box is closed with WM_CLOSE, which an OK-only box treats as OK.
function Read-And-Close-MessageBox([int]$procId) {
    $box = Wait-Until { Windows-Of $procId | Where-Object { $_.Current.ClassName -eq '#32770' } | Select-Object -First 1 }
    if (-not $box) { return $null }
    $hwnd = [IntPtr]$box.Current.NativeWindowHandle
    $text = (@($AE::FromHandle($hwnd).FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) |
        Where-Object { $_.Current.ClassName -eq 'Static' } | ForEach-Object { $_.Current.Name }) -join ' '
    [Win32]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Wait-Until { -not (Windows-Of $procId | Where-Object { $_.Current.ClassName -eq '#32770' }) } 5000 | Out-Null
    $text
}
function File-Hash([string]$path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm SHA256).Hash } else { '' } }

$profilesFile = Join-Path $env:APPDATA 'ClaudeMultiAccount\profiles.json'
# PowerShell 5.1 emits a JSON array as one object: keep it in a variable before counting.
$saved = if (Test-Path $profilesFile) { Get-Content $profilesFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$profileCount = if ($saved) { @($saved).Count } else { 0 }
$profilesBefore = File-Hash $profilesFile
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runBefore = (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).ClaudeMultiAccount
$usersBefore = @(Get-LocalUser | Where-Object Name -like 'claude-*').Name -join ','
$claudeInstalled = [bool](Get-AppxPackage -Name Claude -ErrorAction SilentlyContinue) -or (Test-Path "$env:LOCALAPPDATA\AnthropicClaude\claude.exe")

try {
    # 1. Start and main window
    $p = Start-App
    $main = Wait-Until { Visible-Windows $p.Id | Select-Object -First 1 } 30000
    Check 'Main window opens' $main ($main.Current.Name)
    if (-not $main) { throw 'no main window' }
    $texts = All $main $CT::Text | ForEach-Object { $_.Current.Name }
    Check 'Main account card is shown' (($texts -contains 'Conta principal') -or ($texts -contains 'Main account')) ''
    Check 'Main account card shows the Windows user' ($texts | Where-Object { $_ -like "*($env:USERNAME)*" }) ''
    $openButtons = @(All $main $CT::Button | Where-Object { $_.Current.Name -in 'Abrir Claude', 'Open Claude' })
    Check 'One "Open Claude" button per account' ($openButtons.Count -eq 1 + $profileCount) "buttons=$($openButtons.Count) profiles=$profileCount"
    $banner = ById $main 'Banner'
    $bannerShown = $banner -and -not $banner.Current.IsOffscreen
    Check '"Claude not found" banner matches the install' ($bannerShown -ne $claudeInstalled) "installed=$claudeInstalled banner=$bannerShown"

    # 2. Single instance
    $p2 = Start-App
    $exited = $p2.WaitForExit(15000)
    Check 'Second launch exits at once (single instance)' ($exited -and $p2.ExitCode -eq 0) "exited=$exited code=$($p2.ExitCode)"
    Check 'Still one launcher process' (@(Get-Process ClaudeMultiAccount -ErrorAction SilentlyContinue).Count -eq 1) ''

    # 3. Settings menu and About
    $items = Open-Settings $p.Id $main
    $names = @($items | ForEach-Object { $_.Current.Name })
    Check 'Settings menu has 4 items' ($names.Count -eq 4) ($names -join ' | ')
    $startup = $items | Where-Object { $_.Current.Name -in 'Iniciar com o Windows', 'Start with Windows' }
    $expected = if ($runBefore) { 'On' } else { 'Off' }
    Check '"Start with Windows" matches the registry' ((Toggle-State $startup) -eq $expected) "menu=$(Toggle-State $startup) registry=$expected"
    Invoke ($items | Where-Object { $_.Current.Name -in 'Sobre', 'About' })
    $about = Read-And-Close-MessageBox $p.Id
    $version = (Get-Item $Exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    Check 'About shows the exe version' ($about -like "*$version*") "version=$version"

    # 4. New profile form: hint, defaults, validation, Cancel. Save is only pressed with names it must refuse.
    Invoke (Button-With-Text $main @('Novo perfil', 'New profile'))
    $form = Wait-Until { Visible-Windows $p.Id | Where-Object { $_.Current.Name -in 'Novo perfil', 'New profile' } | Select-Object -First 1 }
    Check 'New profile window opens' $form ''
    if (-not $form) { throw 'no New profile window' }
    $nameBox = ById $form 'NameBox'; $save = ById $form 'SaveButton'
    $hint = (ById $form 'UserHint').Current.Name
    Check 'Name hint shows an example when empty' ($hint -like '*Pessoal*' -or $hint -like '*Personal*') $hint
    Set-Text $nameBox 'Ação Teste'
    $h = Wait-Until { $t = (ById $form 'UserHint').Current.Name; if ($t -match 'claude-acao-teste\d*$') { $t } }
    Check 'Typing a name suggests the Windows user (accents removed)' $h $h
    Set-Text $nameBox 'Cliente Internacional Grande'
    $h = Wait-Until { $t = (ById $form 'UserHint').Current.Name; if ($t -match '(claude-[a-z0-9-]+)$' -and $Matches[1].Length -le 20 -and $t -like '*claude-cliente-inter*') { $t } }
    Check 'Long names are cut to the 20-character Windows limit' $h $h
    $defaults = @("$env:USERPROFILE\source\repos", 'C:\Git', 'C:\src', 'C:\Projects', 'C:\dev') | Where-Object { Test-Path $_ -PathType Container }
    $folders = @(All (ById $form 'FolderList') $CT::ListItem)
    Check 'Existing default project folders are suggested' ($folders.Count -eq @($defaults).Count) "listed=$($folders.Count) existing=$(@($defaults).Count)"
    Check 'Admin option is off by default' ((Toggle-State (ById $form 'AdminCheck')) -eq 'Off') ''
    Check '"Open Claude when done" is on by default' ((Toggle-State (ById $form 'LaunchCheck')) -eq 'On') ''
    Check 'Eight color chips' ((All $form $CT::RadioButton).Count -eq 8) "count=$((All $form $CT::RadioButton).Count)"

    foreach ($case in @(
            @{ Name = '   '; Check = 'Empty name is refused'; Expect = '*nome ao perfil*', '*profile a name*' },
            @{ Name = 'Cliente "X"'; Check = 'Name with quotes is refused'; Expect = '*aspas*', '*quotes*' })) {
        Set-Text $nameBox $case.Name
        if ((Get-Text $nameBox) -ne $case.Name) { Check $case.Check $false 'could not set the name'; continue } # never press Save then
        Invoke $save
        $msg = Read-And-Close-MessageBox $p.Id
        Check $case.Check (($case.Expect | Where-Object { $msg -like $_ }).Count -gt 0) $msg
    }

    Invoke (Named $form $CT::Button @('Cancelar', 'Cancel'))
    Check 'Cancel closes the window' (Wait-Until { -not (Visible-Windows $p.Id | Where-Object { $_.Current.Name -in 'Novo perfil', 'New profile' }) } 5000) ''

    # 5. Close to tray, then a second launch brings the window back
    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Check 'Closing the window hides it' (Wait-Until { (Visible-Windows $p.Id).Count -eq 0 } 5000) ''
    Check 'App keeps running in the tray' (-not $p.HasExited) ''
    (Start-App).WaitForExit(15000) | Out-Null
    $main = Wait-Until { Visible-Windows $p.Id | Select-Object -First 1 } 10000
    Check 'Second launch shows the hidden window again' $main ''

    # 6. Exit from the settings menu
    Invoke (Open-Settings $p.Id $main | Where-Object { $_.Current.Name -in 'Sair', 'Exit' })
    $p.WaitForExit(15000) | Out-Null
    Check 'Exit quits the app' ($p.HasExited -and $p.ExitCode -eq 0) "code=$($p.ExitCode)"

    # 7. --tray start (what "Start with Windows" uses)
    $t = Start-App '--tray'
    Start-Sleep -Seconds 5
    Check '--tray starts without a window' ((-not $t.HasExited) -and (Visible-Windows $t.Id).Count -eq 0) ''
    (Start-App).WaitForExit(15000) | Out-Null
    $main = Wait-Until { Visible-Windows $t.Id | Select-Object -First 1 } 10000
    Check 'Launching again opens the window of the tray instance' $main ''
    Invoke (Open-Settings $t.Id $main | Where-Object { $_.Current.Name -in 'Sair', 'Exit' })
    $t.WaitForExit(15000) | Out-Null
    Check 'Tray instance exits' $t.HasExited ''
}
catch {
    Check 'Script error' $false ("line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)")
}
finally {
    foreach ($proc in $started) { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } }
    Check 'Profiles file unchanged' ((File-Hash $profilesFile) -eq $profilesBefore) ''
    Check 'Startup entry unchanged' (((Get-ItemProperty $runKey -ErrorAction SilentlyContinue).ClaudeMultiAccount) -eq $runBefore) ''
    Check 'No Windows user created or removed' ((@(Get-LocalUser | Where-Object Name -like 'claude-*').Name -join ',') -eq $usersBefore) ''
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 200
$failed = @($results | Where-Object { -not $_.Ok }).Count
"Passed $($results.Count - $failed) of $($results.Count)"
exit ([int]($failed -gt 0))
