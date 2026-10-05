<#
.SYNOPSIS
    Tests de las funciones compartidas de los scripts de servicio.

.DESCRIPTION
    Se ejecutan sin Pester, sin Windows, sin Administrador y sin un servicio instalado:

        pwsh -File scripts/ServiceImagePath.Tests.ps1

    Testea el archivo real (dot-source), no una copia de la lógica: si el parsing se rompe, esto
    falla. Es la única parte de los scripts que se puede verificar fuera de Windows, y justo la que
    más fácil se rompe en silencio: una comilla colgando hace que -DeleteData no encuentre el
    ejecutable y aborte después de haber borrado el servicio.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ServiceImagePath.ps1')

$script:failures = 0
$script:total = 0

function Assert-Equal {
    param($Expected, $Actual, [string] $Because)

    $script:total++

    if ($Expected -ceq $Actual) {
        Write-Host "  ok   $Because"
    }
    else {
        $script:failures++
        Write-Host "  FAIL $Because"
        Write-Host "       expected: [$Expected]"
        Write-Host "       actual  : [$Actual]"
    }
}

Write-Host 'Get-ServiceExecutablePath'

# Con comillas, que es como el SCM guarda una ruta con espacios.
Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\tools\smtp-mockup\smtp-mockup.exe"') `
    'quoted path without spaces'

Assert-Equal 'C:\Program Files\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\Program Files\smtp-mockup\smtp-mockup.exe"') `
    'quoted path with spaces'

# Con argumentos detrás: el ejecutable es lo que va hasta la comilla de cierre.
Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\tools\smtp-mockup\smtp-mockup.exe" --Hosting:Mode=Console') `
    'quoted path with arguments'

Assert-Equal 'C:\Program Files\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\Program Files\smtp-mockup\smtp-mockup.exe" --Hosting:Mode=Console') `
    'quoted path with spaces and arguments'

# Sin comillas: hasta el primer espacio.
Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath 'C:\tools\smtp-mockup\smtp-mockup.exe') `
    'unquoted path'

Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath 'C:\tools\smtp-mockup\smtp-mockup.exe --Hosting:Mode=Console') `
    'unquoted path with arguments'

# El SCM no siempre deja la entrada perfectamente recortada: con espacio o tabulador alrededor, un
# Trim('"') a secas dejaba una comilla colgando y la ruta luego no resolvía.
Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\tools\smtp-mockup\smtp-mockup.exe" ') `
    'trailing space after the closing quote'

Assert-Equal 'C:\Program Files\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\Program Files\smtp-mockup\smtp-mockup.exe" ') `
    'trailing space, path with spaces'

Assert-Equal 'C:\Program Files\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '  "C:\Program Files\smtp-mockup\smtp-mockup.exe"  ') `
    'leading and trailing whitespace'

Assert-Equal 'C:\Program Files\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath "`t`"C:\Program Files\smtp-mockup\smtp-mockup.exe`"") `
    'surrounding tabs'

# Vacíos: el llamador usa -not $exePath para avisar, así que hay que devolver $null y no ''.
Assert-Equal '' ([string] (Get-ServiceExecutablePath '')) 'empty string'

Assert-Equal '' ([string] (Get-ServiceExecutablePath '   ')) 'whitespace only'

Assert-Equal '' ([string] (Get-ServiceExecutablePath $null)) 'null'

# Comilla de apertura sin cierre: malformado, pero se devuelve lo que hay en vez de $null para que
# Test-Path falle con un mensaje de ruta ilegible y no con "no se pudo resolver el ejecutable".
Assert-Equal 'C:\tools\smtp-mockup\smtp-mockup.exe' `
    (Get-ServiceExecutablePath '"C:\tools\smtp-mockup\smtp-mockup.exe') `
    'unterminated quote falls back to trimming'

Write-Host ''
Write-Host "  $($script:total - $script:failures)/$($script:total) passed"

if ($script:failures -gt 0) {
    Write-Host "FAILED: $($script:failures) case(s)"
    exit 1
}

Write-Host 'All tests passed.'