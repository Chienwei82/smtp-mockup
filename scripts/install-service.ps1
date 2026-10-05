<#
.SYNOPSIS
    Instala smtp-mockup como Windows Service.

.DESCRIPTION
    Registra el ejecutable como servicio del SCM con arranque automático y reinicio ante fallo
    (SPEC §10.4), y prepara el Event Log.

    El binario detecta solo si lo arrancó el SCM (D-10): no hay un flag que mantener en
    sincronía con el registro. Por eso, ejecutar el mismo .exe a mano desde una terminal sigue
    funcionando en modo consola, con el log en pantalla.

.PARAMETER Path
    Ruta del ejecutable smtp-mockup.exe. Por defecto, el .exe que está junto al script, o el de
    la carpeta publish\win-x64 del repo.

.PARAMETER Name
    Nombre del servicio. Es también el origen en el Event Log, así que suele querer que coincida
    con el nombre del producto.

.PARAMETER DisplayName
    Nombre visible en services.msc. Por defecto, el valor de -Name.

.PARAMETER Description
    Descripción del servicio.

.PARAMETER StartupType
    Automatic (por defecto), AutomaticDelayedStart, Manual o Disabled.

.PARAMETER Account
    Cuenta con la que corre. Por defecto LocalSystem. Se aplica siempre con 'sc config obj=', también
    para las cuentas de sistema: omitirlo dejaría al servicio corriendo como LocalSystem.
      * LocalSystem   - sin permisos que configurar, pero su 'CurrentUser\My' es el de SYSTEM:
                        NO ve el certificado de 'dotnet dev-certs' del usuario interactivo y usa
                        el PFX autogenerado (docs/certificate-trust.md §4.1).
      * NetworkService - privilegios de red mínimos; es la opción razonable en desarrollo.
      * LocalService   - sin red: el mockup sigue funcionando en localhost.
      * DOMINIO\usuario - requiere -Password y hereda los certificados de ese usuario.

.PARAMETER Password
    Contraseña de la cuenta, sólo si -Account no es una cuenta de sistema.

.PARAMETER LogDirectory
    Carpeta de logs. Relativa ⇒ bajo el directorio del ejecutable. El script escribe el valor en
    'Hosting:LogDirectory' del appsettings.json que hay junto al .exe; NO crea la carpeta ni ajusta
    permisos: la crea el proceso al arrancar, con los permisos de la cuenta del servicio.

.PARAMETER DelayedStart
    Atajo para -StartupType AutomaticDelayedStart (arranca tras el resto de los servicios).

.PARAMETER EventLogSource
    Origen en el Event Log de Application. Se escribe en 'Hosting:EventLogSource' del
    appsettings.json y el script lo registra si falta.

.EXAMPLE
    .\install-service.ps1 -Path C:\tools\smtp-mockup\smtp-mockup.exe

.EXAMPLE
    .\install-service.ps1 -Name smtp-mockup-dev -Account '.\devuser' -Password 'secret' -DelayedStart
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter()]
    [string] $Path,

    [Parameter()]
    [string] $Name = 'smtp-mockup',

    [Parameter()]
    [string] $DisplayName,

    [Parameter()]
    [string] $Description = 'Local fake SMTP server with a web UI for development.',

    [Parameter()]
    [ValidateSet('Automatic', 'AutomaticDelayedStart', 'Manual', 'Disabled')]
    [string] $StartupType = 'Automatic',

    [Parameter()]
    [string] $Account = 'LocalSystem',

    [Parameter()]
    [string] $Password,

    [Parameter()]
    [string] $LogDirectory = 'logs',

    [Parameter()]
    [switch] $DelayedStart,

    [Parameter()]
    [string] $EventLogSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DelayedStart) {
    $StartupType = 'AutomaticDelayedStart'
}

if (-not $EventLogSource) {
    $EventLogSource = $Name
}

if (-not $DisplayName) {
    $DisplayName = $Name
}

# 'sc create' sólo acepta auto|demand|disabled para start=, y el arranque diferido es un valor
# aparte, delayed-auto, que únicamente existe en 'sc config'. Por eso se traduce el nombre legible a
# lo que espera el SCM y el arranque diferido se corrige después con un config.
$scStartupType = switch ($StartupType) {
    'Automatic' { 'auto' }
    'AutomaticDelayedStart' { 'auto' }
    'Manual' { 'demand' }
    'Disabled' { 'disabled' }
}

$needsDelayedStart = $StartupType -eq 'AutomaticDelayedStart'

function Resolve-Executable {
    param([string] $Candidate)

    if ($Candidate) {
        return (Resolve-Path -LiteralPath $Candidate -ErrorAction Stop).Path
    }

    # Junto al script primero; si no, la carpeta de publish del repo. $PSScriptRoot y no
    # $MyInvocation.MyCommand.Path, que es el mismo valor pero sólo funciona dentro de la función.
    $here = $PSScriptRoot
    $candidates = @(
        (Join-Path $here 'smtp-mockup.exe'),
        (Join-Path $here '..\publish\win-x64\smtp-mockup.exe'),
        (Join-Path $here '..\src\SmtpMockup.Host\bin\Release\net10.0\win-x64\publish\smtp-mockup.exe')
    )

    foreach ($option in $candidates) {
        if (Test-Path -LiteralPath $option -PathType Leaf) {
            return (Resolve-Path -LiteralPath $option).Path
        }
    }

    throw 'Could not find smtp-mockup.exe. Pass -Path with the full path to the executable.'
}

function Invoke-Sc {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)

    # sc.exe devuelve 1060/1072/etc. con texto en el estándar de error y NO lanza excepción, así
    # que hay que mirar el código de salida a mano: envolverlo en try/catch no detectaría nada.
    $output = & sc.exe @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code ${exitCode}:`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

$exe = Resolve-Executable -Candidate $Path
$exeDirectory = Split-Path -Parent $exe
$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

if ($existing) {
    throw "The service '$Name' already exists (state: $($existing.Status)). Run uninstall-service.ps1 first, or pass a different -Name."
}

# -LogDirectory y -EventLogSource tienen que acabar en appsettings.json, que es donde la app los
# lee: pasarlos sólo como variables de PowerShell no cambiaría nada en el servicio. Se parchea el
# archivo que hay junto al .exe, que es el que va a leer el proceso.
$settingsPath = Join-Path $exeDirectory 'appsettings.json'

if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json

    if (-not $settings.PSObject.Properties['Hosting']) {
        $settings | Add-Member -NotePropertyName 'Hosting' -NotePropertyValue ([pscustomobject]@{})
    }

    $settings.Hosting | Add-Member -NotePropertyName 'LogDirectory' -NotePropertyValue $LogDirectory -Force
    $settings.Hosting | Add-Member -NotePropertyName 'EventLogSource' -NotePropertyValue $EventLogSource -Force

    $settings | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host "Updated $settingsPath (Hosting:LogDirectory='$LogDirectory', Hosting:EventLogSource='$EventLogSource')"
}
else {
    throw "appsettings.json not found next to the executable at '$exeDirectory'. The host resolves its configuration from AppContext.BaseDirectory, so it is required."
}

Write-Host "Installing '$Name' from $exe"

# A partir de 'sc create' el servicio existe: cualquier fallo posterior dejaría un servicio a medio
# configurar (cuenta equivocada, sin reinicio, sin Event Log), que es peor que no tener nada. Se
# agrupan los pasos y se deshace el 'sc create' si alguno falla.
$serviceCreated = $false

try {
    # El working directory NO lo fija el SCM: el proceso arranca con C:\Windows\System32. Por eso la
    # app resuelve rutas contra AppContext.BaseDirectory en vez de confiar en el CWD, y aquí no hay
    # ningún 'binPath' que lo fije.
    Invoke-Sc create $Name `
        "binPath= `"$exe`"" `
        "start= $scStartupType" `
        "DisplayName= `"$DisplayName`"" `
        "Description= `"$Description`"" | Out-Null

    $serviceCreated = $true

    if ($needsDelayedStart) {
        Invoke-Sc config $Name 'start= delayed-auto' | Out-Null
    }

    # La cuenta se cambia después de crear el servicio porque 'sc create' no acepta credenciales.
    # Omitir 'obj=' NO deja la cuenta por defecto: el servicio ya nace corriendo como LocalSystem y
    # en un dominio eso no es lo que se pidió. Se pasa siempre, y para una cuenta de sistema no hace
    # falta contraseña.
    $isSystemAccount = $Account -in @(
        'LocalSystem', 'LocalService', 'NetworkService',
        'NT AUTHORITY\LocalSystem', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\NetworkService')

    if ($isSystemAccount) {
        Invoke-Sc config $Name "obj= $Account" | Out-Null
        Write-Host "Running as $Account (built-in service account)"
    }
    else {
        if (-not $Password) {
            throw "Account '$Account' is not a built-in service account: pass -Password."
        }

        Invoke-Sc config $Name "obj= $Account" "password= $Password" | Out-Null
        Write-Host "Running as $Account"
    }

    # Reinicio ante fallo: un mockup caído y que no vuelve deja al desarrollador sin SMTP justo
    # cuando lo está usando.
    Invoke-Sc failure $Name 'reset= 86400' 'actions= restart/60000/restart/60000/restart/60000' | Out-Null
    Invoke-Sc failureflag $Name 1 | Out-Null

    # El origen del Event Log tiene que existir antes del primer arranque: sin él el provider falla
    # al escribir, y en un entorno sin consola ese error no se ve.
    $applicationLog = Get-WinEvent -ListLog Application -ErrorAction SilentlyContinue

    if ($applicationLog) {
        $source = $applicationLog.Sources | Where-Object { $_.SourceName -eq $EventLogSource }

        if ($source) {
            Write-Host "Event Log source '$EventLogSource' already exists"
        }
        else {
            New-EventLog -LogName Application -Source $EventLogSource
            Write-Host "Registered Event Log source '$EventLogSource'"
        }
    }
}
catch {
    if ($serviceCreated) {
        Write-Warning "Rolling back the partially configured service '$Name'."
        & sc.exe delete $Name | Out-Null
    }

    throw
}

# Aviso, no error: la carpeta la crea el proceso al arrancar, con los permisos de su cuenta. Si esa
# cuenta no puede escribir en el directorio del ejecutable, tampoco podrá escribir los JSON ni el
# PFX, y eso sí aparece en el log de arranque.
$logPath = if ([System.IO.Path]::IsPathRooted($LogDirectory)) {
    $LogDirectory
}
else {
    Join-Path (Split-Path -Parent $exe) $LogDirectory
}

Write-Host ''
Write-Host 'Installed:'
Write-Host "  Name       : $Name"
Write-Host "  Executable : $exe"
Write-Host "  Startup    : $StartupType"
Write-Host "  Log folder : $logPath"
Write-Host "  Event Log  : Application / $EventLogSource"
Write-Host ''
Write-Host "Start it with:   Start-Service -Name $Name"
Write-Host "Follow the log:  Get-Content `"$logPath\smtp-mockup-$(Get-Date -Format yyyy-MM-dd).log`" -Tail 50 -Wait"
Write-Host "Remove it with:  .\uninstall-service.ps1 -Name $Name"
