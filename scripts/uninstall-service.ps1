<#
.SYNOPSIS
    Desinstala el Windows Service de smtp-mockup.

.DESCRIPTION
    Para el servicio y, opcionalmente, quita el origen del Event Log.

    NO borra ni la carpeta data\ (los correos capturados) ni logs\, ni certs\, por defecto: son
    datos del usuario y borrarlos por sorpresa sería peor que dejar archivos huérfanos. Para
    una desinstalación limpia, bórralos a mano o con -DeleteData.

    Si el servicio está parado y borrarlo falla con "marked for deletion", normalmente es que
    un handle sigue abierto: reinicia la consola de PowerShell (o la sesión) y vuelve a
    intentarlo.

.PARAMETER Name
    Nombre del servicio a quitar. Por defecto 'smtp-mockup'.

.PARAMETER KeepEventLogSource
    No quitar el origen del Event Log. Útil si se comparte con otra instalación.

.PARAMETER EventLogSource
    Origen del Event Log a quitar. Por defecto se lee 'Hosting:EventLogSource' del appsettings.json
    que hay junto al ejecutable y, si no está, se usa el nombre del servicio. Pásalo explícitamente
    si instalaste con un origen distinto al nombre del servicio.

.PARAMETER DeleteData
    Borra además data\, logs\ y certs\ del directorio del ejecutable. Destruye los correos
    guardados: es opt-in explícito.

.EXAMPLE
    .\uninstall-service.ps1

.EXAMPLE
    .\uninstall-service.ps1 -Name smtp-mockup-dev -DeleteData
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter()]
    [string] $Name = 'smtp-mockup',

    [Parameter()]
    [switch] $KeepEventLogSource,

    [Parameter()]
    [string] $EventLogSource,

    [Parameter()]
    [switch] $DeleteData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# El parsing de ImagePath está en un archivo aparte para poder testearlo sin Windows ni un servicio
# instalado: es la única parte con parsing de texto y la que más fácil se rompe en silencio.
. (Join-Path $PSScriptRoot 'ServiceImagePath.ps1')

$service = Get-Service -Name $Name -ErrorAction SilentlyContinue

if (-not $service) {
    Write-Host "The service '$Name' is not installed. Nothing to do."
    return
}

# La ruta del ejecutable se lee del registro ANTES de borrar el servicio: es el único sitio donde
# queda, y hace falta para el aviso de datos y para -DeleteData.
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
$imagePath = (Get-ItemProperty -Path $key -Name ImagePath -ErrorAction SilentlyContinue).ImagePath

$exePath = $null

if ($imagePath) {
    $exePath = Get-ServiceExecutablePath -ImagePath $imagePath
}

if ($service.Status -ne 'Stopped') {
    Write-Host "Stopping '$Name'..."

    # Stop-Service espera al estado 'Stopped', pero el proceso puede tardar el ShutdownTimeout
    # (5 s) en cerrar los circuitos de Blazor y los listeners SMTP.
    Stop-Service -Name $Name -Force

    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    Write-Host 'Stopped.'
}

Write-Host "Deleting '$Name'..."
& sc.exe delete $Name | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete $Name failed with exit code $LASTEXITCODE. If it says 'marked for deletion', close every handle to the executable and try again."
}

Write-Host "The service '$Name' was removed."

if (-not $KeepEventLogSource) {
    try {
        # El origen se resuelve antes de borrar nada: install-service.ps1 lo escribe en
        # 'Hosting:EventLogSource' del appsettings.json, y ese valor puede no ser el nombre del
        # servicio. Sin esto se intentaría quitar un origen que no existe y quedaría el real.
        $source = $EventLogSource

        if (-not $source -and $exePath) {
            $settingsPath = Join-Path (Split-Path -Parent $exePath) 'appsettings.json'

            if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
                $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                $configured = $settings.PSObject.Properties['Hosting']

                if ($configured -and $configured.Value.PSObject.Properties['EventLogSource']) {
                    $source = [string] $configured.Value.EventLogSource
                }
            }
        }

        if (-not $source) {
            $source = $Name
        }

        # El origen puede no existir (si el servicio se instaló a mano, o ya se quitó): es
        # information, no un fallo del script.
        if (Get-WinEvent -ListLog Application -ErrorAction SilentlyContinue) {
            Remove-EventLog -Source $source -ErrorAction Stop
            Write-Host "Removed the Event Log source '$source'."
        }
    }
    catch {
        Write-Warning "Could not remove the Event Log source: $($_.Exception.Message)"
    }
}

if ($DeleteData) {
    if (-not $exePath -or -not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw '-DeleteData was requested but the executable path could not be resolved from the registry.'
    }

    $root = Split-Path -Parent $exePath

    foreach ($folder in @('data', 'logs', 'certs')) {
        $path = Join-Path $root $folder

        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
            Write-Host "Deleted $path"
        }
    }
}
elseif ($exePath) {
    $root = Split-Path -Parent $exePath
    Write-Host ''
    Write-Host "The stored mail is still in: $root\data"
    Write-Host 'Pass -DeleteData to remove data\, logs\ and certs\ as well.'
}

Write-Host ''
Write-Host 'Done.'
