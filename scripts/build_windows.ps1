$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore work/src/VisorIP.csproj --configfile work/NuGetAI.Config
    if($LASTEXITCODE -ne 0){throw 'Falló la restauración de NuGet.'}
    dotnet build work/src/VisorIP.csproj -c Release -o work/build --no-restore --nologo
    if($LASTEXITCODE -ne 0){throw 'Falló la compilación.'}
    Write-Output 'Compilado en work/build. Para reproducir video, agregá VLC x64 en work/build/motor.'
} finally { Pop-Location }
