<#
    Собирает всё, что нужно для разбора «не работает», в один вывод.
    Ничего не меняет и никуда не внедряется — только читает.

    Запускать с ЗАПУЩЕННОЙ игрой:

        powershell -ExecutionPolicy Bypass -File diag.ps1 -Name myGame
        powershell -ExecutionPolicy Bypass -File diag.ps1 -TargetPid 1234
        powershell -ExecutionPolicy Bypass -File diag.ps1          # искать Unity сам

    Вывод целиком можно отдавать как есть.
#>
[CmdletBinding()]
param(
    [string]$Name,
    [int]$TargetPid
)

$ErrorActionPreference = 'Continue'
$bin = Join-Path $PSScriptRoot 'bin'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Diag {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr h, out bool wow64);

    // Exactly the rights Injector.Inject asks for: this is the question that
    // matters, not whether the process merely exists.
    public const uint INJECT_ACCESS = 0x0002 | 0x0008 | 0x0020 | 0x0010 | 0x0400;
    public const uint QUERY_LIMITED = 0x1000;
}
'@

function Section($t) { "`n=== $t ===" }

Section 'Окружение'
"ОС            : $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version.Build)"
"PowerShell    : $($PSVersionTable.PSVersion)  ($(if ([Environment]::Is64BitProcess) {'x64'} else {'x86'}))"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
"Админ         : $admin"

Section 'Файлы сборки'
if (-not (Test-Path $bin)) {
    "НЕТ ПАПКИ bin — сборка не выполнялась"
} else {
    $need = @('GameSpeed.exe','GameSpeed32.exe','gsphook64.dll','gsphook32.dll',
              'gsunity64.dll','gsunity32.dll')
    foreach ($f in $need) {
        $p = Join-Path $bin $f
        if (Test-Path $p) {
            $i = Get-Item $p
            "{0,-16} {1,8} байт  {2}" -f $f, $i.Length, $i.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        } else {
            "{0,-16} ОТСУТСТВУЕТ" -f $f
        }
    }
}

# ---- выбор цели ----------------------------------------------------

$targets = @()
if ($TargetPid) {
    $targets = @(Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)
    if (-not $targets) { Section 'Цель'; "PID $TargetPid не найден"; return }
} elseif ($Name) {
    $targets = @(Get-Process -ErrorAction SilentlyContinue |
                 Where-Object { $_.ProcessName -like "*$Name*" })
    if (-not $targets) { Section 'Цель'; "Процессов по шаблону '*$Name*' не найдено"; return }
} else {
    Section 'Поиск Unity-процессов'
    # Пройтись по всем и посмотреть, у кого загружен рантайм Unity.
    $found = @()
    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        try {
            $mods = $p.Modules | ForEach-Object { $_.ModuleName }
            if ($mods | Where-Object { $_ -match '^(mono.*\.dll|GameAssembly\.dll|UnityPlayer\.dll)$' }) {
                $found += $p
            }
        } catch { }   # чужая разрядность или нет прав — молча мимо
    }
    if (-not $found) {
        "Unity-процессов не видно. Либо игра не запущена, либо её модули не читаются"
        "без прав администратора — тогда перезапустите этот скрипт от администратора"
        "или укажите процесс явно: -Name <часть имени> либо -TargetPid <pid>."
        return
    }
    $found | ForEach-Object { "  {0} (PID {1})" -f $_.ProcessName, $_.Id }
    $targets = $found
}

# ---- разбор каждой цели --------------------------------------------

foreach ($t in $targets) {
    Section "Цель: $($t.ProcessName) (PID $($t.Id))"

    "Запущен       : $($t.StartTime)"

    $h = [Diag]::OpenProcess([Diag]::QUERY_LIMITED, $false, $t.Id)
    if ($h -eq [IntPtr]::Zero) {
        "Разрядность   : не определить (нет доступа)"
    } else {
        $wow = $false
        [void][Diag]::IsWow64Process($h, [ref]$wow)
        [void][Diag]::CloseHandle($h)
        "Разрядность   : $(if ($wow) {'x86 — нужен GameSpeed32.exe'} else {'x64 — нужен GameSpeed.exe'})"
    }

    # Главный вопрос: сможет ли инжектор вообще открыть процесс.
    $h = [Diag]::OpenProcess([Diag]::INJECT_ACCESS, $false, $t.Id)
    if ($h -eq [IntPtr]::Zero) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        "OpenProcess   : ОТКАЗ, код $err$(if ($err -eq 5) {' (доступ запрещён — запустите GameSpeed от администратора)'})"
    } else {
        [void][Diag]::CloseHandle($h)
        "OpenProcess   : успех — права на внедрение есть"
    }

    try {
        $mods = @($t.Modules | ForEach-Object { $_.ModuleName })
        "Модулей всего : $($mods.Count)"

        $unity = $mods | Where-Object { $_ -match 'mono|GameAssembly|UnityPlayer' }
        if ($unity) { "Рантайм Unity : $($unity -join ', ')" }
        else        { "Рантайм Unity : не найден -> нужен механизм «часы ОС»" }

        $ours = $mods | Where-Object { $_ -match 'gsphook|gsunity' }
        if ($ours) { "Наши модули   : $($ours -join ', ') — внедрение состоялось" }
        else       { "Наши модули   : НЕ ЗАГРУЖЕНЫ — внедрения не было" }
    } catch {
        "Модули        : не читаются ($($_.Exception.Message.Split([char]10)[0]))"
        "                для 32-битной игры запустите 32-битный PowerShell,"
        "                для игры от администратора — запустите скрипт от администратора"
    }

    foreach ($kind in 'gsunity','gsphook') {
        $log = Join-Path $env:TEMP "${kind}_$($t.Id).log"
        if (Test-Path $log) {
            "`n--- $kind`_$($t.Id).log ---"
            Get-Content $log | Select-Object -Last 40
        }
    }
}

Section 'Прочие логи в %TEMP%'
$logs = Get-ChildItem $env:TEMP -Filter 'gs*_*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 10
if ($logs) { $logs | ForEach-Object { "  $($_.Name)  $($_.LastWriteTime)" } }
else       { "  логов нет — ни один наш модуль ни разу не запускался" }
