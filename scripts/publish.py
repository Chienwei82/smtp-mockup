#!/usr/bin/env python3
"""
Compila, prueba y publica smtp-mockup en ./publish/<rid>/.

Sustituye a los comandos 'dotnet build' + 'dotnet test' + 'dotnet publish' que había que
recordar de memoria (README, seccion Publicacion) y hace las tres cosas en el orden
correcto: no se publica un binario que no haya pasado los tests, y no se publica nada si
la compilacion falla.

Puntos de diseno que vienen del proyecto, no de gusto:

  * publish/ NUNCA va al repo. Esta en .gitignore y este script lo comprueba en cada
    ejecucion, avisa si algun dia deja de estar ignorado y, si llega el caso, lo saca del
    indice con 'git rm -r --cached'. Un publish son ~57 MB por RID; subirlos al repo rompe
    el historial para siempre.
  * Al refrescar un RID se PRESERVAN data/, certs/ y logs/: son datos del usuario (los
    correos que le has mandado al mockup y el PFX autofirmado), no artefactos de build.
    Borrarlos por defecto seria la forma mas rapida de perder correos sin avisar. Se borran
    con --clean-data, y el script dice cuantos ficheros ha dejado intactos.
  * Se verifica wwwroot/_framework/blazor.web.js en el artefacto. Sin el la UI "se ve pero
    no funciona" (el circuito de Interactive Server no arranca): el publish parece bueno y
    solo falla en el navegador. Es el fallo mas confundido de este proyecto, y es
    exactamente el que un script de publicacion puede detectar por ti.
  * La version del SDK se contrasta con global.json antes de compilar.

Uso:
    python3 scripts/publish.py                       # RID del host + win-x64
    python3 scripts/publish.py --rid linux-x64
    python3 scripts/publish.py --skip-tests          # solo build + publish
    python3 scripts/publish.py --clean-data          # borra data/, certs/ y logs/
    python3 scripts/publish.py --no-color            # salida sin ANSI

Salida: 0 si todo fue bien, 1 si algo fallo, 130 si se interrumpio con Ctrl-C.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------------------
# Rutas
# --------------------------------------------------------------------------------------

# El script vive en <raiz>/scripts/, asi que la raiz es su directorio padre. Se resuelve
# por ruta de fichero y no por CWD: llamarlo desde otro sitio tiene que funcionar igual.
ROOT = Path(__file__).resolve().parent.parent
HOST_PROJECT = ROOT / "src" / "SmtpMockup.Host"
PUBLISH_ROOT = ROOT / "publish"
GLOBAL_JSON = ROOT / "global.json"

# Directorios que el refresh de un RID nunca borra (datos del usuario, no de build).
PRESERVED_DIRS = ("data", "certs", "logs")

# Comprobado en el artefacto publicado. Si algo falta, el binario arranca pero la UI se
# queda muerta: mejor fallar aqui que en el navegador de otra persona.
REQUIRED_IN_PUBLISH = (
    "wwwroot/_framework/blazor.web.js",
    "appsettings.json",
)
# Avisado, no bloqueante: el nombre del asset de MudBlazor puede cambiar entre versiones.
EXPECTED_IN_PUBLISH = ("wwwroot/_content/MudBlazor/MudBlazor.min.css",)


# --------------------------------------------------------------------------------------
# Color
# --------------------------------------------------------------------------------------


class Palette:
    """ANSI, apagado si no hay TTY, si NO_COLOR esta puesto o si se pasa --no-color."""

    def __init__(self, enabled: bool) -> None:
        self.enabled = enabled

    def _wrap(self, code: str, text: str) -> str:
        return f"\033[{code}m{text}\033[0m" if self.enabled else text

    def bold(self, t: str) -> str:
        return self._wrap("1", t)

    def dim(self, t: str) -> str:
        return self._wrap("2", t)

    def red(self, t: str) -> str:
        return self._wrap("31", t)

    def green(self, t: str) -> str:
        return self._wrap("32", t)

    def yellow(self, t: str) -> str:
        return self._wrap("33", t)

    def blue(self, t: str) -> str:
        return self._wrap("34", t)

    def cyan(self, t: str) -> str:
        return self._wrap("36", t)


def color_enabled(no_color_flag: bool) -> bool:
    if no_color_flag:
        return False
    # Convencion de https://no-color.org: cualquier valor (incluso vacio) desactiva el color.
    if os.environ.get("NO_COLOR") is not None:
        return False
    if os.environ.get("TERM") == "dumb":
        return False
    return sys.stdout.isatty()


# --------------------------------------------------------------------------------------
# Salida por consola
# --------------------------------------------------------------------------------------


def human_size(num_bytes: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if abs(num_bytes) < 1024 or unit == "GB":
            return f"{num_bytes:.0f} {unit}" if unit == "B" else f"{num_bytes:.1f} {unit}"
        num_bytes /= 1024
    return f"{num_bytes:.1f} GB"


def wrap_center(text: str, width: int) -> list[str]:
    lines: list[str] = []
    current = ""
    for word in text.split():
        candidate = f"{current} {word}".strip()
        if len(candidate) > width:
            lines.append(current)
            current = word
        else:
            current = candidate
    if current:
        lines.append(current)
    return lines


class Console:
    def __init__(self, c: Palette) -> None:
        self.c = c

    def banner(self, text: str) -> None:
        width = 64
        print()
        print(self.c.cyan("+" + "-" * width + "+"))
        for line in wrap_center(text, width - 2):
            print(self.c.cyan("|") + " " + line.ljust(width - 2) + " " + self.c.cyan("|"))
        print(self.c.cyan("+" + "-" * width + "+"))

    def step(self, index: int, total: int, title: str) -> None:
        print()
        print(f"{self.c.blue(f'[{index}/{total}]')} {self.c.bold(title)}")
        print(self.c.dim("-" * 64))

    def info(self, msg: str) -> None:
        print(f"  {self.c.blue('i')} {msg}")

    def ok(self, msg: str) -> None:
        print(f"  {self.c.green('OK')} {msg}")

    def warn(self, msg: str) -> None:
        print(f"  {self.c.yellow('!')} {self.c.yellow(msg)}")

    def fail(self, msg: str) -> None:
        print(f"  {self.c.red('X')} {self.c.red(msg)}")

    def detail(self, msg: str) -> None:
        for line in msg.splitlines():
            print(f"      {self.c.dim(line)}")

    def kv(self, key: str, value: str) -> None:
        print(f"  {self.c.dim(key.ljust(16))} {value}")


# --------------------------------------------------------------------------------------
# Ejecucion de dotnet
# --------------------------------------------------------------------------------------


@dataclass
class Result:
    returncode: int
    output: str
    duration: float


@dataclass
class StepLog:
    """Acumula la salida de un paso para poder enseñarla si algo falla."""

    lines: list[str] = field(default_factory=list)


def run_dotnet(console: Console, step: StepLog, args: list[str]) -> Result:
    """Ejecuta 'dotnet <args>' guardando toda la salida en el StepLog.

    La salida se captura siempre (tambien cuando sale bien) porque al final se resume: el
    ruido de MSBuild no interesa, pero el recuento de tests y los nombres de los que
    fallan si.
    """
    console.detail("dotnet " + " ".join(args))
    started = time.monotonic()

    try:
        completed = subprocess.run(
            ["dotnet", *args], cwd=ROOT, capture_output=True, text=True
        )
        output = (completed.stdout or "") + (completed.stderr or "")
        code = completed.returncode
    except FileNotFoundError:
        output = "No se encontro el ejecutable 'dotnet' en el PATH."
        code = 127

    step.lines.extend(output.splitlines())
    return Result(code, output, time.monotonic() - started)


def last_lines(lines: list[str], count: int) -> str:
    """Ultimas lineas relevantes: el error de MSBuild suele estar al final."""
    relevant = [line.rstrip() for line in lines if line.strip()]
    if not relevant:
        return "(sin salida)"
    return "\n".join(relevant[-count:])



# --------------------------------------------------------------------------------------
# Tests
# --------------------------------------------------------------------------------------

# "Passed!  - Failed: 0, Passed: 143, Skipped: 0, Total: 143, Duration: 497 ms
#  - SmtpMockup.Core.Tests.dll (net10.0)"
SUMMARY_RE = re.compile(
    r"^(?P<verdict>Passed|Failed|Skipped)!\s+-\s+"
    r"Failed:\s+(?P<failed>\d+),\s+Passed:\s+(?P<passed>\d+),\s+"
    r"Skipped:\s+(?P<skipped>\d+),\s+Total:\s+(?P<total>\d+),\s+"
    r"Duration:\s+(?P<duration>.+?)\s+-\s+(?P<assembly>\S+)"
)

# "  Failed SmtpMockup.Smtp.Tests.AlgunaTest [48 ms]", a veces con prefijo
# "[xUnit.net 00:00:01.17]" delante.
FAILED_TEST_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s+)?Failed\s+(?P<name>\S+)\s+\[")

# El bloque "Error Message:" de xUnit, que es lo que un desarrollador necesita para
# arreglarlo. El mensaje viene indentado con 3+ espacios en las lineas siguientes; se
# corta en la linea "Stack Trace:".
ERROR_HEADER_RE = re.compile(r"^\s*Error Message:\s*$")
ERROR_LINE_RE = re.compile(r"^\s{3,}(\S.*)$")
STOP_MARKERS = ("Stack Trace:", "--- End of stack trace")


@dataclass
class TestReport:
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    assemblies: list[tuple[str, int, int]] = field(default_factory=list)
    failures: list[tuple[str, str]] = field(default_factory=list)

    @property
    def total(self) -> int:
        return self.passed + self.failed + self.skipped


def parse_test_output(output: str) -> TestReport:
    """Lee el resumen por ensamblado y el detalle de los fallos de la salida de VSTest.

    Se recorre linea a linea con una pequena maquina de estados en vez de con una regex
    sobre el bloque entero: el mensaje de un fallo son varias lineas indentadas y puede
    contener casi cualquier cosa, asi que "la linea siguiente" no basta.
    """
    report = TestReport()
    lines = output.splitlines()
    current_test: str | None = None
    collecting = False
    message_parts: list[str] = []

    def flush(state: list[bool]) -> None:
        """Cierra el bloque de error en curso y guarda el mensaje junto a su test.

        El estado se pasa por parametro y no se toca `collecting` desde dentro: asignar a
        una variable de la funcion externa la convertiria en local de `flush`, y python
        protestaria con UnboundLocalError en la primera llamada.
        """
        if state[0] and current_test and message_parts:
            report.failures.append((current_test, " ".join(message_parts)))
        state[0] = False
        message_parts.clear()

    state = [collecting]
    for line in lines:
        summary = SUMMARY_RE.match(line.strip())
        if summary:
            flush(state)
            current_test = None
            report.passed += int(summary["passed"])
            report.failed += int(summary["failed"])
            report.skipped += int(summary["skipped"])
            report.assemblies.append(
                (summary["assembly"], int(summary["total"]), int(summary["failed"]))
            )
            continue

        failed_match = FAILED_TEST_RE.match(line)
        if failed_match:
            flush(state)
            current_test = failed_match["name"]
            continue

        # Fin del bloque de error: empieza el stack trace, que aqui casi no aporta (esta
        # suite no usa Thread.Sleep, asi que los fallos no suelen ser de tiempos).
        if state[0] and any(marker in line for marker in STOP_MARKERS):
            flush(state)
            continue

        if state[0]:
            error_line = ERROR_LINE_RE.match(line)
            if error_line:
                message_parts.append(error_line.group(1).strip())
                continue
            # Una linea sin sangrar dentro del bloque significa que el mensaje ya termino.
            if line.strip():
                flush(state)
            continue

        if ERROR_HEADER_RE.match(line):
            state[0] = True

    flush(state)
    return report


def print_test_report(console: Console, report: TestReport) -> None:
    for assembly, total, failed in report.assemblies:
        name = assembly.replace(".dll", "")
        if failed:
            console.fail(f"{name}: {total - failed} en verde, {failed} en rojo")
        else:
            console.ok(f"{name}: {total} tests")

    summary = f"{report.total} tests - {report.passed} en verde"
    if report.failed:
        summary += f", {report.failed} en rojo"
    if report.skipped:
        summary += f", {report.skipped} saltados"

    if not report.failed:
        console.ok(summary)
        return

    console.fail(summary)
    for name, message in report.failures:
        console.fail(name)
        console.detail(console.c.red(message))



# --------------------------------------------------------------------------------------
# Publicacion
# --------------------------------------------------------------------------------------


def dir_size(path: Path) -> int:
    if not path.exists():
        return 0
    return sum(f.stat().st_size for f in path.rglob("*") if f.is_file())


def host_rid() -> str:
    """RID del equipo actual, para publicar sin tener que acordarse delflags."""
    system = platform.system().lower()
    machine = platform.machine().lower()

    os_part = {"windows": "win", "linux": "linux"}.get(system, system)
    arch = {
        "x86_64": "x64",
        "amd64": "x64",
        "aarch64": "arm64",
        "arm64": "arm64",
        "x86": "x86",
    }.get(machine, machine)

    return f"{os_part}-{arch}"


def executable_name(rid: str) -> str:
    return "smtp-mockup.exe" if rid.startswith("win") else "smtp-mockup"


def refresh_publish_dir(target: Path, clean_data: bool, console: Console) -> None:
    """Limpia el directorio del RID conservando los datos del usuario.

    Sin esto, un publish deja encima los ficheros del build anterior: el binario nuevo con
    el wwwroot viejo es justo la combinacion que hace fallar la UI sin que nada lo diga.
    """
    if not target.exists():
        target.mkdir(parents=True, exist_ok=True)
        return

    kept: list[str] = []
    removed = 0
    for entry in sorted(target.iterdir()):
        if not clean_data and entry.name in PRESERVED_DIRS:
            kept.append(entry.name)
            continue
        if entry.is_dir():
            removed += sum(1 for _ in entry.rglob("*"))
            shutil.rmtree(entry)
        else:
            removed += 1
            entry.unlink()

    if removed:
        console.info(f"{removed} ficheros antiguos borrados de {target.name}/")
    if kept:
        console.info(f"sin tocar: {', '.join(kept)}/ (datos, no output de build)")


def verify_publish(target: Path, rid: str, console: Console) -> list[str]:
    """Comprueba el artefacto. Devuelve la lista de problemas (vacia si todo esta bien)."""
    problems: list[str] = []

    exe = target / executable_name(rid)
    if not exe.is_file():
        problems.append(
            f"falta el ejecutable {exe.name}: sin el no hay nada que instalar ni ejecutar"
        )
    else:
        console.kv("ejecutable", f"{exe.name} ({human_size(exe.stat().st_size)})")

    for relative in REQUIRED_IN_PUBLISH:
        if not (target / relative).is_file():
            hint = ""
            if "_framework" in relative:
                hint = (
                    " - sin el la UI se ve pero no responde. Comprueba que "
                    "RequiresAspNetWebAssets siga a true en SmtpMockup.Host.csproj"
                )
            problems.append(f"falta {relative}{hint}")

    for relative in EXPECTED_IN_PUBLISH:
        if not (target / relative).is_file():
            console.warn(f"no encuentro {relative}; revisa los estilos al abrir la UI")

    files = sum(1 for f in target.rglob("*") if f.is_file())
    console.kv("contenido", f"{files} ficheros, {human_size(dir_size(target))} en total")

    return problems


# --------------------------------------------------------------------------------------
# Empaquetado (SPEC 11.3, criterio 19)
# --------------------------------------------------------------------------------------

# Lo que va al zip. Se empaqueta la CARPETA, no el binario suelto: los static web assets
# de Blazor son ficheros y no se pueden incrustar en el ejecutable, asi que un .exe sin
# su wwwroot arranca con una UI muerta (SPEC 11.3, criterio 19).
ZIP_INCLUDE = (
    "wwwroot",
    "appsettings.json",
    "appsettings.Development.json",
)


def zip_name_for(rid: str) -> str:
    return f"smtp-mockup-{rid}.zip"


def build_zip(target: Path, rid: str, console: Console) -> Path:
    """Empaqueta el RID en publish/smtp-mockup-<rid>.zip, junto a las carpetas.

    El zip queda en publish/ (no dentro de publish/<rid>/) para que un refresh posterior no
    se lo lleve por delante ni acabe incluyendose a si mismo.

    Se excluyen data/, certs/ y logs/ a proposito: son datos de quien prueba el mockup
    (correos recibidos y el PFX autofirmado, que ademas es una clave privada). Repartir
    un certificado dentro de un zip que va a otra gente es justo lo que RNF-07 prohibe.
    """
    archive = PUBLISH_ROOT / zip_name_for(rid)
    exe = executable_name(rid)

    if not (target / exe).is_file():
        raise FileNotFoundError(f"no hay {exe} en {target} que empaquetar")

    before = archive.stat().st_size if archive.is_file() else 0
    if archive.is_file():
        # zipfile abre en modo "w", que recorta el archivo a cero antes de escribir: si se
        # falla a mitad, el zip anterior queda corrupto con nombre de bueno. Se aparta.
        archive.unlink()

    added = 0
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        # El ejecutable se guarda con ZIP_STORED (sin comprimir): son 55 MB de binario
        # autoextraíble que ya vienen comprimidos dentro, y DEFLATED sobre eso solo gasta
        # CPU y no ahorra casi nada.
        bundle.write(target / exe, arcname=exe, compress_type=zipfile.ZIP_STORED)

        for relative in ZIP_INCLUDE:
            source = target / relative
            if source.is_file():
                bundle.write(source, arcname=relative)
                added += 1
            elif source.is_dir():
                for path in sorted(source.rglob("*")):
                    if path.is_file():
                        bundle.write(path, arcname=path.relative_to(target))
                        added += 1

    console.kv("zip", f"{archive.name} ({human_size(archive.stat().st_size)}, {added + 1} ficheros)")
    if before:
        console.info(f"reemplazado el zip anterior ({human_size(before)})")

    return archive


def verify_zip(archive: Path, rid: str, console: Console) -> list[str]:
    """Abre el zip recien escrito y comprueba que no le falta lo que la UI necesita.

    Un zip que se genera bien y no lleva el wwwroot es un zip que parece entregable y no
    funciona: el error sale en el navegador de quien lo descomprime, no aqui.
    """
    problems: list[str] = []

    with zipfile.ZipFile(archive) as bundle:
        broken = bundle.testzip()
        if broken is not None:
            problems.append(
                f"el zip esta corrupto: el CRC de '{broken}' no cuadra"
            )

        names = set(bundle.namelist())

        exe = executable_name(rid)
        if exe not in names:
            problems.append(f"el zip no lleva {exe}")

        if "wwwroot/_framework/blazor.web.js" not in names:
            problems.append(
                "el zip no lleva wwwroot/_framework/blazor.web.js: al descomprimirlo, la UI "
                "se vera pero no respondera"
            )

        if "appsettings.json" not in names:
            problems.append("el zip no lleva appsettings.json: arrancara con la config por defecto")

        leaked = [n for n in names if n.startswith(("data/", "certs/", "logs/"))]
        if leaked:
            problems.append(
                f"el zip incluye datos del usuario que no deben repartirse: {', '.join(leaked[:5])}"
            )

        console.kv("contenido", f"{len(names)} entradas en el zip")

    return problems


def ensure_publish_ignored(console: Console) -> None:
    """publish/ no debe acabar en el repo. Se comprueba en cada ejecucion.

    No es paranoia: el .gitignore es un fichero de texto y un 'git add -A' con la regla
    equivocada mete 57 MB de binario por RID en el historial, que ya no se borra.
    """
    if not shutil.which("git"):
        console.warn("no encuentro 'git'; me salto la comprobacion de .gitignore")
        return

    probe = subprocess.run(
        ["git", "check-ignore", "-q", "publish/"], cwd=ROOT, capture_output=True
    )
    tracked = subprocess.run(
        ["git", "ls-files", "--error-unmatch", "publish"], cwd=ROOT, capture_output=True
    )

    if probe.returncode == 0 and tracked.returncode != 0:
        console.ok("publish/ esta en .gitignore y fuera del indice de git")
        return

    console.warn("publish/ ya no esta correctamente ignorado; lo arreglo")

    if tracked.returncode == 0:
        console.info("habia output de publish en el indice; ejecutando git rm --cached...")
        subprocess.run(
            ["git", "rm", "-r", "--cached", "--quiet", "publish"], cwd=ROOT, check=False
        )

    gitignore = ROOT / ".gitignore"
    content = gitignore.read_text(encoding="utf-8") if gitignore.is_file() else ""
    if "publish/" not in content:
        with gitignore.open("a", encoding="utf-8") as handle:
            handle.write(
                "\n# ---- Salida de scripts/publish.py (binarios de ~57 MB) ----\npublish/\n"
            )
        console.ok("regla 'publish/' anadida a .gitignore")



# --------------------------------------------------------------------------------------
# Preflight
# --------------------------------------------------------------------------------------


def check_dotnet(console: Console) -> bool:
    """Comprueba el SDK antes de compilar. False = no se puede seguir."""
    if not shutil.which("dotnet"):
        console.fail("no encuentro 'dotnet' en el PATH")
        console.detail(
            "Instala el .NET SDK 10 (https://dotnet.microsoft.com/download/dotnet/10.0)\n"
            "o abre una terminal donde el SDK este ya instalado."
        )
        return False

    current = subprocess.run(
        ["dotnet", "--version"], cwd=ROOT, capture_output=True, text=True
    ).stdout.strip()

    if GLOBAL_JSON.is_file():
        try:
            sdk = json.loads(GLOBAL_JSON.read_text(encoding="utf-8")).get("sdk", {})
            wanted = sdk.get("version", "")
            roll_forward = sdk.get("rollForward", "latestPatch")
        except (ValueError, OSError):
            wanted, roll_forward = "", "latestPatch"

        if wanted:
            console.kv("global.json", f"SDK {wanted} (rollForward: {roll_forward})")
            console.kv("SDK en uso", current)
            if not current.startswith(wanted.split(".")[0] + "."):
                console.warn(
                    f"el SDK en uso ({current}) no es el que fija global.json ({wanted}); "
                    "el resultado puede no ser reproducible"
                )
    else:
        console.kv("SDK en uso", current)

    console.kv("RID del host", host_rid())
    return True


def resolve_rids(requested: list[str] | None, console: Console) -> list[str]:
    if requested:
        # dict.fromkeys quita duplicados conservando el orden en que se pediron.
        return list(dict.fromkeys(requested))

    host = host_rid()
    rids = [host]

    # El servicio de Windows se instala con scripts/install-service.ps1, que necesita un
    # .exe. Publicarlo siempre evita el "funciona en Linux y en Windows no".
    if not host.startswith("win"):
        rids.append("win-x64")

    console.info(f"sin --rid, publico: {', '.join(rids)}")
    return rids


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="publish.py",
        description="Compila, prueba y publica smtp-mockup en ./publish/<rid>/.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Ejemplos:\n"
            "  python3 scripts/publish.py                 # RID del host + win-x64\n"
            "  python3 scripts/publish.py --rid linux-x64  # solo un RID\n"
            "  python3 scripts/publish.py --skip-tests      # build + publish\n"
            "  python3 scripts/publish.py --clean-data      # borra data/, certs/, logs/\n"
            "  python3 scripts/publish.py --zip             # ademas, un .zip para repartir\n"
            "  python3 scripts/publish.py --zip-only         # reempaqueta sin republicar\n"
        ),
    )
    parser.add_argument(
        "--rid",
        action="append",
        metavar="RID",
        help="RID a publicar, repetible (linux-x64, win-x64...). "
        "Por defecto: el del host y win-x64.",
    )
    parser.add_argument(
        "-c",
        "--configuration",
        default="Release",
        help="Configuracion de MSBuild (por defecto: Release).",
    )
    parser.add_argument(
        "--skip-tests", action="store_true", help="No ejecutar los tests (build + publish)."
    )
    parser.add_argument(
        "--skip-build", action="store_true", help="No compilar antes de publicar."
    )
    parser.add_argument(
        "--skip-restore", action="store_true", help="No ejecutar 'dotnet restore'."
    )
    parser.add_argument(
        "--clean-data",
        action="store_true",
        help="Borrar tambien data/, certs/ y logs/ del RID (correos recibidos y PFX).",
    )
    parser.add_argument(
        "--no-color", action="store_true", help="Salida sin codigos de color."
    )
    parser.add_argument(
        "--zip",
        action="store_true",
        help="Ademas de publicar, empaqueta cada RID en publish/smtp-mockup-<rid>.zip "
        "listo para repartir (SPEC 11.3, criterio 19).",
    )
    parser.add_argument(
        "--zip-only",
        action="store_true",
        help="No publicar: solo re-empaquetar en zip los RIDs que ya estan en publish/.",
    )
    parser.add_argument(
        "--list-rids",
        action="store_true",
        help="Mostrar los RIDs que se publicarian y salir.",
    )
    return parser

# --------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------

PUBLISH_ARGS = (
    # Self-contained y single-file: un unico ejecutable es lo que hace que instalar el
    # servicio sea copiar un archivo (D-13).
    "--self-contained",
    "-p:PublishSingleFile=true",
    # SIN trimming: MailKit/MimeKit y el binder de Options usan reflexion, y un publish
    # recortado rompe en runtime, no al compilar (DESIGN 9).
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=false",
    # Sin esto Windows bloquea el .exe mientras se extrae la cache nativa.
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=embedded",
    "-p:SatelliteResourceLanguages=en",
)


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    console = Console(Palette(color_enabled(args.no_color)))
    console.banner("smtp-mockup - publicacion")

    rids = resolve_rids(args.rid, console)

    if args.list_rids:
        for rid in rids:
            print(f"  {rid}")
        return 0

    # --zip-only implica --zip: empaquetar lo ya publicado es justamente el caso de uso,
    # asi que se deduce en vez de exigir los dos flags y que uno se olvide del otro.
    if args.zip_only:
        args.zip = True

    # ---- Solo empaquetar ----------------------------------------------------------
    if args.zip_only:
        console.step(1, 1 + len(rids), "Reempaquetando los RIDs ya publicados")
        ensure_publish_ignored(console)

        zip_failures: dict[str, list[str]] = {}
        for rid in rids:
            target = PUBLISH_ROOT / rid
            if not target.is_dir():
                console.fail(f"publish/{rid}/ no existe: publicalo antes de empaquetarlo")
                zip_failures[rid] = ["no hay nada publicado que empaquetar"]
                continue

            try:
                archive = build_zip(target, rid, console)
            except (FileNotFoundError, OSError) as exception:
                console.fail(f"no se pudo empaquetar {rid}: {exception}")
                zip_failures[rid] = [str(exception)]
                continue

            problems = verify_zip(archive, rid, console)
            if problems:
                zip_failures[rid] = problems
                for problem in problems:
                    console.fail(problem)
            else:
                console.ok(f"{archive.name} verificado (ejecutable + wwwroot + appsettings)")

        print()
        if zip_failures:
            console.banner("empaquetado con problemas")
            for rid, problems in zip_failures.items():
                console.fail(f"{rid}:")
                for problem in problems:
                    console.detail(problem)
            return 1

        console.banner("empaquetado completado")
        for rid in rids:
            archive = PUBLISH_ROOT / zip_name_for(rid)
            print(f"  {console.c.green('OK')} publish/{archive.name}  ({human_size(archive.stat().st_size)})")
        return 0

    total_steps = 3 + len(rids)
    step_index = 0

    # ---- Preflight ---------------------------------------------------------------
    step_index += 1
    console.step(step_index, total_steps, "Comprobando el entorno")
    if not check_dotnet(console):
        return 1

    for rid in rids:
        console.kv(f"RID {rid}", f"-> publish/{rid}/")

    ensure_publish_ignored(console)

    common = ["-c", args.configuration]

    # ---- Restore -----------------------------------------------------------------
    if not args.skip_restore:
        step_index += 1
        console.step(step_index, total_steps, "Restaurando paquetes")
        step = StepLog()
        # Sin -c: 'dotnet restore' no acepta el switch (MSB1001, "Unknown switch"); la
        # configuración sólo aplica a build/test/publish. El restore no compila, asi que
        # no tiene nada que restaurar de forma distinta por configuracion.
        result = run_dotnet(console, step, ["restore"])
        if result.returncode != 0:
            console.fail(f"'dotnet restore' fallo (codigo {result.returncode})")
            console.detail(last_lines(step.lines, 20))
            console.detail(
                "Si el error es de NuGet (NU****), suele ser una caida de red o una fuente\n"
                "mal configurada. Prueba: dotnet restore --verbosity normal"
            )
            return 1
        console.ok(f"paquetes restaurados en {result.duration:.1f}s")

    # ---- Build -------------------------------------------------------------------
    if not args.skip_build:
        step_index += 1
        console.step(step_index, total_steps, f"Compilando ({args.configuration})")
        step = StepLog()
        # -warnaserror: el proyecto ya trae TreatWarningsAsErrors, pero el flag tambien
        # cubre los avisos que emiten MSBuild y las tareas, no solo el compilador de C#.
        result = run_dotnet(console, step, ["build", *common, "-warnaserror"])
        if result.returncode != 0:
            console.fail(f"la compilacion fallo (codigo {result.returncode})")
            errors = [line for line in step.lines if ": error" in line]
            if errors:
                console.detail(f"{len(errors)} error(es):")
                for line in errors[:10]:
                    console.detail(console.c.red(line.strip()))
            console.detail(last_lines(step.lines, 20))
            return 1
        console.ok(f"compilado sin errores ni avisos en {result.duration:.1f}s")

    # ---- Tests -------------------------------------------------------------------
    if not args.skip_tests:
        step_index += 1
        console.step(step_index, total_steps, "Ejecutando los tests")
        console.info(
            "los tests de SMTP abren puertos reales y los de UI montan el circuito: tarda ~1 min"
        )
        step = StepLog()
        result = run_dotnet(console, step, ["test", *common])

        report = parse_test_output(result.output)
        print_test_report(console, report)

        if result.returncode != 0 or report.failed:
            console.fail("no se publica nada con los tests en rojo")
            console.detail(
                "Arregla los tests y vuelve a lanzar el script.\n"
                "Si el fallo es de puertos efimeros o de red, reintenta: los puertos los\n"
                "elige el sistema y una prueba puede chocar con otra."
            )
            return 1

        if report.total == 0:
            console.warn(
                "no encontre ningun test en la salida; me fio solo del codigo de salida"
            )
        else:
            console.ok(f"suite en verde en {result.duration:.1f}s")


    # ---- Publish, un RID cada vez ------------------------------------------------
    problems_by_rid: dict[str, list[str]] = {}

    for rid in rids:
        step_index += 1
        console.step(step_index, total_steps, f"Publicando {rid}")
        target = PUBLISH_ROOT / rid

        refresh_publish_dir(target, args.clean_data, console)

        step = StepLog()
        # Los flags van explicitos en vez de -p:PublishProfile=win-x64 a proposito: el
        # .pubxml fija PublishDir con barras invertidas, que en Linux crean un
        # directorio literal en vez de publish/win-x64. Pasando -o el RID manda en todas
        # las plataformas, y asi el script no depende de un perfil que solo cubre Windows.
        result = run_dotnet(
            console,
            step,
            [
                "publish",
                str(HOST_PROJECT),
                *common,
                "-r",
                rid,
                *PUBLISH_ARGS,
                "-o",
                str(target),
            ],
        )

        if result.returncode != 0:
            console.fail(f"'dotnet publish {rid}' fallo (codigo {result.returncode})")
            errors = [line for line in step.lines if ": error" in line]
            for line in errors[:10]:
                console.detail(console.c.red(line.strip()))
            console.detail(last_lines(step.lines, 20))
            problems_by_rid[rid] = [f"dotnet publish devolvio {result.returncode}"]
            continue

        console.ok(f"publicado en publish/{rid}/ en {result.duration:.1f}s")

        problems = verify_publish(target, rid, console)

        # El zip solo se arma si el artefacto esta bien: empaquetar un publish con el
        # wwwroot incompleto produce un zip que parece entregable y no funciona.
        if not problems and args.zip:
            try:
                archive = build_zip(target, rid, console)
            except (FileNotFoundError, OSError) as exception:
                problems = [f"no se pudo empaquetar: {exception}"]
            else:
                zip_problems = verify_zip(archive, rid, console)
                if zip_problems:
                    problems = zip_problems
                else:
                    console.ok("zip verificado (ejecutable + wwwroot + appsettings, sin datos)")

        if problems:
            problems_by_rid[rid] = problems
            for problem in problems:
                console.fail(problem)
        else:
            console.ok("artefacto verificado (binario + wwwroot + appsettings)")

    # ---- Resumen -----------------------------------------------------------------
    print()
    if problems_by_rid:
        console.banner("publicacion con problemas")
        for rid, problems in problems_by_rid.items():
            console.fail(f"{rid}:")
            for problem in problems:
                console.detail(problem)
        return 1

    console.banner("publicacion completada")
    for rid in rids:
        target = PUBLISH_ROOT / rid
        exe = executable_name(rid)
        print(f"  {console.c.green('OK')} publish/{rid}/  ({human_size(dir_size(target))})")
        print(f"      ejecutar:  ./publish/{rid}/{exe}")
        if rid.startswith("win"):
            print(
                f"      servicio:  pwsh -File scripts/install-service.ps1 "
                f"-Path publish/{rid}/{exe}"
            )
        if args.zip:
            archive = PUBLISH_ROOT / zip_name_for(rid)
            print(
                f"      repartir:  publish/{archive.name}  "
                f"({human_size(archive.stat().st_size)})"
            )

    print()
    if args.zip:
        console.info(
            "cada zip lleva el ejecutable y su wwwroot, y NO los correos ni el PFX que "
            "hallas en publish/<rid>/"
        )
        if any(not rid.startswith("win") for rid in rids):
            console.info(
                "al descomprimir en Linux, el binario puede salir sin permiso de "
                "ejecucion: chmod +x smtp-mockup"
            )
    else:
        console.info(
            "reparte la CARPETA completa, no solo el binario: sin wwwroot la UI no funciona"
        )
        console.info("para un zip listo para repartir: python3 scripts/publish.py --zip")
    if args.clean_data:
        console.warn(
            "has usado --clean-data: se han borrado los correos y el certificado del RID"
        )
    console.info("repetible con: python3 scripts/publish.py")

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\ninterrumpido", file=sys.stderr)
        sys.exit(130)

