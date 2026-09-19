$ErrorActionPreference = 'Stop'
$native = Join-Path $PSScriptRoot '../src/AutoGame.Attach/Native'
$visualStudio = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Visual Studio C++ build tools were not found.' }
$vcvars = Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars64.bat'
$objectDirectory = Join-Path $native 'obj'
New-Item -ItemType Directory -Force $objectDirectory | Out-Null
$command = 'call "{0}" >nul && cl /nologo /LD /EHsc /MT /O2 "{1}" /Fo"{2}" /link /OUT:"{3}" /IMPLIB:"{4}"' -f $vcvars,(Join-Path $native 'Bootstrap.cpp'),(Join-Path $objectDirectory 'Bootstrap.obj'),(Join-Path $native 'AutoGame.Native.dll'),(Join-Path $objectDirectory 'AutoGame.Native.lib')
& cmd.exe /d /c $command
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
