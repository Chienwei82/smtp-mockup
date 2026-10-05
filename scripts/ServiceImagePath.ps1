<#
.SYNOPSIS
    Funciones compartidas por install-service.ps1 y uninstall-service.ps1.

.DESCRIPTION
    Vive en un archivo aparte, y no dentro de uninstall-service.ps1, por dos razones:

      * se puede probar sin Windows, sin Administrador y sin un servicio instalado. Es la única
        parte con parsing de texto de los scripts, y es justo la que más fácil se rompe en
        silencio: una comilla colgando hace que -DeleteData no encuentre el ejecutable y aborte.
      * install-service.ps1 puede reutilizarla si necesita leer la ruta de un servicio existente.
#>

function Get-ServiceExecutablePath {
    <#
    .SYNOPSIS
        Extrae el ejecutable del valor ImagePath del registro de un servicio.

    .DESCRIPTION
        El SCM guarda ImagePath entre comillas cuando el ejecutable tiene espacios, y puede añadir
        argumentos. Se toma sólo el ejecutable: si empieza por comilla, todo hasta la comilla de
        cierre; si no, hasta el primer espacio.

        El orden importa: recortar espacios ANTES de quitar comillas evita el caso '"...exe" ' con
        espacio final, donde un Trim('"') a secas deja una comilla colgando y luego Test-Path falla
        con una ruta que no existe.

    .PARAMETER ImagePath
        Valor de ImagePath tal cual lo devuelve el registro.

    .OUTPUTS
        La ruta del ejecutable, o $null si la entrada estaba vacía.
    #>
    [OutputType([string])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string] $ImagePath
    )

    if ([string]::IsNullOrWhiteSpace($ImagePath)) {
        return $null
    }

    $trimmed = $ImagePath.Trim()

    if ($trimmed.StartsWith('"')) {
        $closing = $trimmed.IndexOf('"', 1)

        if ($closing -gt 0) {
            return $trimmed.Substring(1, $closing - 1)
        }

        # Comilla de apertura sin cierre: es un ImagePath malformado, pero mejor devolver lo que hay
        # que abortar en Test-Path con un mensaje de ruta ilegible y no con "no se pudo resolver el
        # ejecutable".
        return $trimmed.Trim('"')
    }

    return ($trimmed -split '\s+', 2)[0]
}