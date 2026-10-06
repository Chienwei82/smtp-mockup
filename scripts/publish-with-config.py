#!/usr/bin/env python3
"""
Publica smtp-mockup como scripts/publish.py, pero antes pregunta dos cosas y deja la
respuesta escrita en el appsettings.json que se publica junto al binario:

  1. ¿Quieres el listener con TLS (STARTTLS)?  Por defecto, no.
  2. ¿Los puertos por defecto (8025 claro, 8443 TLS, 8888 UI) o unos nuevos?

Reutiliza scripts/publish.py tal cual: ese sigue siendo el que compila, prueba y
publica. Lo que añade este script es la configuración interactiva y que el
appsettings.json publicado (y, si se pide --zip, el zip) salga ya con los puertos y el
TLS elegidos. El script imprime también los argumentos de línea de comandos
equivalentes, por si se prefieren pasar al ejecutable en vez de editar el JSON.

El listener "en claro" (Smtp:Plain) siempre se activa; el de TLS es Smtp:StartTls.
Ojo: el puerto en claro anuncia STARTTLS aunque el listener de TLS esté desactivado
(así nace el mockup); "TLS: no" aquí significa "no abro el puerto seguro".

Uso:
    python3 scripts/publish-with-config.py                  # interactivo
    python3 scripts/publish-with-config.py --yes             # sin preguntar (TLS no, puertos por defecto)
    python3 scripts/publish-with-config.py --print-config    # solo enseña la config y sale
    python3 scripts/publish-with-config.py --zip             # además, un zip listo para repartir
    python3 scripts/publish-with-config.py --rid linux-x64   # un RID concreto

Salida: 0 si todo fue bien, 1 si algo falló, 130 si se interrumpió con Ctrl-C.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path

# Este script vive junto a publish.py; se añade su carpeta al path para importarlo.
sys.path.insert(0, str(Path(__file__).resolve().parent))

import publish  # noqa: E402  (import después de tocar sys.path, a propósito)

# Puertos por defecto del mockup: los mismos que declara el appsettings.json del Host.
DEFAULT_PLAIN_PORT = 8025
DEFAULT_STARTTLS_PORT = 8443
DEFAULT_WEB_PORT = 8888

YES = {"s", "si", "sí", "y", "yes", "true", "1"}
NO = {"n", "no", "false", "0"}


def configure_stdio() -> None:
    """Deja la salida en UTF-8 para que los acentos de las preguntas no revienten.

    En Windows, con una consola de código heredado (cp1252), imprimir una tilde o un
    signo de apertura sin esto lanza UnicodeEncodeError. Si el intérprete no soporta
    reconfigure (Python viejo), no pasa nada: se queda como estaba.
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


@dataclass(frozen=True)
class ListenConfig:
    """La configuración de escucha elegida para el binario publicado."""

    tls: bool
    plain_port: int
    tls_port: int | None
    web_port: int

    def summary(self) -> str:
        tls = f"TLS {self.tls_port}" if self.tls and self.tls_port is not None else "TLS desactivado"
        return f"SMTP claro {self.plain_port} | {tls} | UI {self.web_port}"

    def cli_args(self) -> list[str]:
        """Las mismas claves, en la forma que acepta el ejecutable por línea de comandos."""
        args = [f"--Smtp:Plain:Port={self.plain_port}"]
        if self.tls and self.tls_port is not None:
            args.append(f"--Smtp:StartTls:Port={self.tls_port}")
        else:
            args.append("--Smtp:StartTls:Enabled=false")
        args.append(f"--Web:Port={self.web_port}")
        return args


def ask(prompt: str) -> str:
    """input() tolerante: con la entrada agotada (por ejemplo por tubería) devuelve ""."""
    try:
        return input(prompt)
    except EOFError:
        print()
        return ""


def ask_yes_no(question: str, *, default: bool) -> bool:
    hint = "[S/n]" if default else "[s/N]"
    while True:
        answer = ask(f"{question} {hint} ").strip().lower()
        if not answer:
            return default
        if answer in YES:
            return True
        if answer in NO:
            return False
        print("  Responde 's' (sí) o 'n' (no).")


def ask_port(question: str, default: int) -> int:
    while True:
        answer = ask(f"{question} [{default}] ").strip()
        if not answer:
            return default
        try:
            port = int(answer, 10)
        except ValueError:
            print("  Tiene que ser un número entero entre 0 y 65535.")
            continue
        if not 0 <= port <= 65535:
            print("  Fuera de rango: 0-65535 (0 = puerto efímero).")
            continue
        return port


def ask_custom_ports(tls: bool) -> ListenConfig:
    """Pide los puertos y repite si chocan, con la misma regla que el validador del mockup."""
    while True:
        plain_port = ask_port("Puerto SMTP en claro", DEFAULT_PLAIN_PORT)
        tls_port = ask_port("Puerto SMTP con TLS (STARTTLS)", DEFAULT_STARTTLS_PORT) if tls else None
        web_port = ask_port("Puerto de la UI web", DEFAULT_WEB_PORT)

        # Los puertos activos no pueden coincidir; el 0 es efímero, así que no cuenta.
        chosen = [port for port in (plain_port, tls_port, web_port) if port]
        if len(chosen) != len(set(chosen)):
            print("  Esos puertos chocan entre sí; prueba otra vez (0 = efímero).")
            continue
        return ListenConfig(tls=tls, plain_port=plain_port, tls_port=tls_port, web_port=web_port)


def ask_configuration() -> ListenConfig:
    print()
    print("  Pulsa Enter para aceptar el valor por defecto del corchete.")
    tls = ask_yes_no("¿Quieres el listener con TLS (STARTTLS)?", default=False)

    defaults = ask_yes_no(
        "¿Usar los puertos por defecto "
        f"({DEFAULT_PLAIN_PORT} SMTP, {DEFAULT_STARTTLS_PORT} TLS, {DEFAULT_WEB_PORT} UI)?",
        default=True,
    )
    if defaults:
        return ListenConfig(
            tls=tls,
            plain_port=DEFAULT_PLAIN_PORT,
            tls_port=DEFAULT_STARTTLS_PORT if tls else None,
            web_port=DEFAULT_WEB_PORT,
        )

    print()
    print("  Puertos nuevos (Enter = el valor por defecto de cada uno).")
    return ask_custom_ports(tls)


def write_appsettings(rid_dir: Path, config: ListenConfig) -> None:
    """Escribe la config en el appsettings.json publicado.

    Se toca solo el JSON publicado (nunca el del repo): es lo que hace que el binario
    arranque con los puertos y el TLS elegidos, y que el zip los lleve dentro.
    """
    path = rid_dir / "appsettings.json"
    data = json.loads(path.read_text(encoding="utf-8"))

    smtp = data.setdefault("Smtp", {})
    smtp.setdefault("Plain", {})["Port"] = config.plain_port

    starttls = smtp.setdefault("StartTls", {})
    starttls["Enabled"] = config.tls
    if config.tls and config.tls_port is not None:
        starttls["Port"] = config.tls_port

    data.setdefault("Web", {})["Port"] = config.web_port

    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="publish-with-config.py",
        description=(
            "Publica smtp-mockup como publish.py, pero pregunta antes si quieres TLS y "
            "qué puertos usar, y los deja escritos en el appsettings.json publicado."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Ejemplos:\n"
            "  python3 scripts/publish-with-config.py                  # interactivo\n"
            "  python3 scripts/publish-with-config.py --yes             # sin preguntar\n"
            "  python3 scripts/publish-with-config.py --print-config    # solo la config\n"
            "  python3 scripts/publish-with-config.py --zip             # + zip para repartir\n"
            "  python3 scripts/publish-with-config.py --rid linux-x64   # un RID concreto\n"
        ),
    )
    parser.add_argument(
        "--rid",
        action="append",
        metavar="RID",
        help="RID a publicar, repetible (linux-x64, win-x64, osx-arm64...). "
        "Por defecto: el del host y win-x64.",
    )
    parser.add_argument(
        "-c",
        "--configuration",
        default="Release",
        help="Configuración de MSBuild (por defecto: Release).",
    )
    parser.add_argument("--skip-tests", action="store_true", help="No ejecutar los tests.")
    parser.add_argument("--skip-build", action="store_true", help="No compilar antes de publicar.")
    parser.add_argument("--skip-restore", action="store_true", help="No ejecutar 'dotnet restore'.")
    parser.add_argument(
        "--clean-data",
        action="store_true",
        help="Borrar también data/, certs/ y logs/ del RID (correos recibidos y PFX).",
    )
    parser.add_argument(
        "--zip",
        action="store_true",
        help="Empaquetar cada RID en publish/smtp-mockup-<rid>.zip, ya con la config dentro.",
    )
    parser.add_argument(
        "--yes",
        "-y",
        action="store_true",
        help="No preguntar: TLS desactivado y puertos por defecto.",
    )
    parser.add_argument(
        "--print-config",
        action="store_true",
        help="Preguntar (o no, con --yes), enseñar la config y salir sin publicar.",
    )
    parser.add_argument("--no-color", action="store_true", help="Salida sin códigos de color.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    configure_stdio()
    console = publish.Console(publish.Palette(publish.color_enabled(args.no_color)))
    console.banner("smtp-mockup - publicación con configuración")

    # ---- 1) Configuración de escucha --------------------------------------------
    if args.yes:
        config = ListenConfig(
            tls=False,
            plain_port=DEFAULT_PLAIN_PORT,
            tls_port=None,
            web_port=DEFAULT_WEB_PORT,
        )
        console.info("--yes: TLS desactivado y puertos por defecto")
    else:
        config = ask_configuration()

    print()
    console.kv("configuración", config.summary())
    console.kv("equivale a", " ".join(config.cli_args()))

    if args.print_config:
        print()
        console.info("--print-config: no publico nada")
        return 0

    # ---- 2) Publicar (reutilizando publish.py) ----------------------------------
    # Se publica SIN --zip a propósito: el zip se arma después, cuando la config ya
    # está escrita en el appsettings.json. Al revés, el zip saldría con los puertos
    # por defecto y no con los elegidos.
    rids = publish.resolve_rids(args.rid, console)

    publish_argv = ["-c", args.configuration]
    for rid in rids:
        publish_argv += ["--rid", rid]
    for flag, enabled in (
        ("--skip-restore", args.skip_restore),
        ("--skip-build", args.skip_build),
        ("--skip-tests", args.skip_tests),
        ("--clean-data", args.clean_data),
        ("--no-color", args.no_color),
    ):
        if enabled:
            publish_argv.append(flag)

    console.info("paso a la publicación de scripts/publish.py...")
    code = publish.main(publish_argv)
    if code != 0:
        console.fail("la publicación falló; no escribo la configuración")
        return code

    # ---- 3) Escribir la configuración en el appsettings.json publicado ----------
    print()
    console.banner("configurando el appsettings.json publicado")
    for rid in rids:
        target = publish.PUBLISH_ROOT / rid
        try:
            write_appsettings(target, config)
        except (OSError, ValueError) as exception:
            console.fail(f"{rid}: no pude escribir appsettings.json ({exception})")
            return 1
        console.ok(f"publish/{rid}/appsettings.json -> {config.summary()}")

    # ---- 4) Zips, ya con la config dentro ---------------------------------------
    if args.zip:
        print()
        console.banner("empaquetando")
        for rid in rids:
            target = publish.PUBLISH_ROOT / rid
            try:
                archive = publish.build_zip(target, rid, console)
            except (FileNotFoundError, OSError) as exception:
                console.fail(f"{rid}: no se pudo empaquetar ({exception})")
                return 1
            problems = publish.verify_zip(archive, rid, console)
            if problems:
                for problem in problems:
                    console.fail(problem)
                return 1
            console.ok(f"{archive.name} verificado (lleva la config elegida)")

    # ---- 5) Resumen -------------------------------------------------------------
    print()
    console.banner("listo")
    for rid in rids:
        target = publish.PUBLISH_ROOT / rid
        name = publish.executable_name(rid)
        print(
            f"  {console.c.green('OK')} publish/{rid}/  "
            f"({publish.human_size(publish.dir_size(target))})"
        )
        print(f"      ejecutar:  ./publish/{rid}/{name}")
        print(f"      UI:        http://127.0.0.1:{config.web_port}/")
        if args.zip:
            archive = publish.PUBLISH_ROOT / publish.zip_name_for(rid)
            print(f"      repartir:  publish/{archive.name}")
    print()
    console.info(f"config aplicada: {config.summary()}")
    console.info("cambiar de idea es volver a ejecutar este script")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\ninterrumpido", file=sys.stderr)
        sys.exit(130)
