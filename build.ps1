<#
    Builds GameSpeed into .\bin

      gsphook64.dll / gsphook32.dll     the hook, needs a C compiler
      GameSpeed.exe / GameSpeed32.exe   injector GUI, needs the .NET 8 SDK
      testclock.exe / testclock32.exe   test target, needs a C compiler

    The C half is tried with whatever compiler is available, in order:
    zig cc, MSVC cl.exe, MinGW gcc. Zig is also looked for in the directory
    winget installs it to, so a fresh install works without reopening the
    shell that PATH change needs.

    -SkipNative     build only the .NET parts
    -SkipInjector   build only the C parts
    -Package        also make the end-user release in .\dist: a self-contained
                    single-file HearthstoneSpeedUp.exe (no .NET install
                    needed) plus gsunity64.dll, zipped
    -Version        version stamped into the release exe and zip name
#>
[CmdletBinding()]
param(
    [switch]$SkipNative,
    [switch]$SkipInjector,
    [switch]$Package,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin  = Join-Path $root 'bin'
$hook  = Join-Path $root 'src\hook\gsphook.c'
$unity = Join-Path $root 'src\hook\gsunity.c'
$test  = Join-Path $root 'src\testtarget\testclock.c'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

function Find-Tool($name) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    # winget puts a portable install here and only edits PATH for new shells.
    $p = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $p) {
        $hit = Get-ChildItem $p -Recurse -Filter "$name.exe" -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Build-Native {
    $zig = Find-Tool 'zig'
    if ($zig) {
        Write-Host "Native: zig cc ($zig)" -ForegroundColor Cyan
        $targets = @(
            @{ T = 'x86_64-windows-gnu'; Hook = 'gsphook64.dll'; Unity = 'gsunity64.dll'; Test = 'testclock.exe'   },
            @{ T = 'x86-windows-gnu';    Hook = 'gsphook32.dll'; Unity = 'gsunity32.dll'; Test = 'testclock32.exe' }
        )
        foreach ($t in $targets) {
            & $zig cc -target $t.T -shared -O2 -s -o (Join-Path $bin $t.Hook) $hook -lkernel32 -luser32
            if ($LASTEXITCODE -ne 0) { throw "zig cc failed building $($t.Hook)" }
            & $zig cc -target $t.T -shared -O2 -s -o (Join-Path $bin $t.Unity) $unity -lkernel32 -luser32
            if ($LASTEXITCODE -ne 0) { throw "zig cc failed building $($t.Unity)" }
            & $zig cc -target $t.T -O2 -s -o (Join-Path $bin $t.Test) $test -lkernel32 -lwinmm
            if ($LASTEXITCODE -ne 0) { throw "zig cc failed building $($t.Test)" }
            Write-Host "  -> $($t.Hook), $($t.Unity), $($t.Test)"
        }
        # zig emits an import library next to a DLL; nothing links against it.
        Remove-Item (Join-Path $bin 'gsphook.lib'), (Join-Path $bin 'gsunity.lib') `
            -Force -ErrorAction SilentlyContinue
        return
    }

    if (Find-Tool 'cl') {
        Write-Host 'Native: MSVC cl.exe' -ForegroundColor Cyan
        Push-Location $bin
        try {
            & cl /nologo /O2 /W3 /LD $hook /Fegsphook64.dll /link kernel32.lib user32.lib
            if ($LASTEXITCODE -ne 0) { throw 'cl failed building gsphook64.dll' }
            & cl /nologo /O2 /W3 /LD $unity /Fegsunity64.dll /link kernel32.lib user32.lib
            if ($LASTEXITCODE -ne 0) { throw 'cl failed building gsunity64.dll' }
            & cl /nologo /O2 /W3 $test /Fetestclock.exe /link kernel32.lib winmm.lib
            if ($LASTEXITCODE -ne 0) { throw 'cl failed building testclock.exe' }
            Remove-Item -Force -ErrorAction SilentlyContinue *.obj, *.exp, *.lib
            Write-Host '  -> gsphook64.dll, gsunity64.dll, testclock.exe'
            Write-Host '  (run again from an x86 Native Tools prompt for the 32-bit set)'
        }
        finally { Pop-Location }
        return
    }

    if (Find-Tool 'gcc') {
        Write-Host 'Native: MinGW gcc' -ForegroundColor Cyan
        & gcc -O2 -shared -s -o (Join-Path $bin 'gsphook64.dll') $hook -lkernel32 -luser32
        if ($LASTEXITCODE -ne 0) { throw 'gcc failed building gsphook64.dll' }
        & gcc -O2 -shared -s -o (Join-Path $bin 'gsunity64.dll') $unity -lkernel32 -luser32
        if ($LASTEXITCODE -ne 0) { throw 'gcc failed building gsunity64.dll' }
        & gcc -O2 -s -o (Join-Path $bin 'testclock.exe') $test -lkernel32 -lwinmm
        if ($LASTEXITCODE -ne 0) { throw 'gcc failed building testclock.exe' }
        Write-Host '  -> gsphook64.dll, gsunity64.dll, testclock.exe (one bitness per gcc)'
        return
    }

    Write-Warning @'
Компилятор C не найден, нативную часть собрать нечем.
Поставьте и запустите скрипт снова:
  winget install -e --id zig.zig      (~80 МБ, собирает сразу x64 и x86)
Инжектор при этом всё равно соберётся.
'@
}

if (-not $SkipNative) { Build-Native }

if (-not $SkipInjector) {
    Write-Host 'Injector: dotnet publish' -ForegroundColor Cyan
    $proj = Join-Path $root 'src\injector\GameSpeed.csproj'
    & dotnet publish $proj -c Release -r win-x64 --self-contained false `
        -p:Platform=x64 -o $bin -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (x64) failed' }
    & dotnet publish $proj -c Release -r win-x86 --self-contained false `
        -p:Platform=x86 -p:AssemblyName=GameSpeed32 -o $bin -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (x86) failed' }
    Remove-Item (Join-Path $bin '*.pdb') -Force -ErrorAction SilentlyContinue
    Write-Host '  -> GameSpeed.exe, GameSpeed32.exe'
}

if ($Package) {
    $dll = Join-Path $bin 'gsunity64.dll'
    if (-not (Test-Path $dll)) { throw 'gsunity64.dll is missing: build the native part first' }

    $dist  = Join-Path $root 'dist'
    $name  = "HearthstoneSpeedUp-v$Version"
    $stage = Join-Path $dist $name
    Remove-Item $stage, "$stage.zip" -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    Write-Host 'Package: self-contained single-file exe' -ForegroundColor Cyan
    $proj = Join-Path $root 'src\injector\GameSpeed.csproj'
    & dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -p:AssemblyName=HearthstoneSpeedUp -p:Version=$Version `
        -o $stage -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (package) failed' }

    Copy-Item $dll $stage
    Copy-Item (Join-Path $root 'package\README.txt') $stage
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath "$stage.zip" -Force
    Write-Host "  -> dist\$name.zip ($([math]::Round((Get-Item "$stage.zip").Length / 1MB, 1)) MB)"
}

Write-Host ''
Write-Host 'bin:' -ForegroundColor Green
Get-ChildItem $bin -File |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    Select-Object Name, Length |
    Format-Table -AutoSize
