#!/usr/bin/env python3
"""
Manda al smtp-mockup tres correos distintos, cada uno con una historia clasica y
graciosa generada (una fabula, un cuento de tres y un chiste).

Es la version "de verdad" del ejemplo del README (seccion "Mandar correo al mockup"),
pensada para probar la UI: los tres mensajes aparecen solos en
http://127.0.0.1:8888/. Cada uno prueba una forma distinta del mismo mensaje:

  1. solo texto plano, con un par de cabeceras X- propias
  2. texto + HTML (los dos cuerpos a la vez)
  3. texto + HTML + un adjunto .txt con la historia

Todo el trabajo de SMTP y de generacion lo hace scripts/mockup_mailer.py, que esta
al lado y se importa como modulo.

Uso:
    python3 scripts/send_story_mails.py                       # 127.0.0.1:8025
    python3 scripts/send_story_mails.py --starttls             # 127.0.0.1:8443, STARTTLS
    python3 scripts/send_story_mails.py --host 192.168.1.20    # otro host de la red
    python3 scripts/send_story_mails.py --to buzon@example.com
    python3 scripts/send_story_mails.py --seed 7               # historias reproducibles
    python3 scripts/send_story_mails.py --dry-run              # genera y no envia

Salida: 0 si los tres salieron, 1 si alguno fallo, 130 si se interrumpio con Ctrl-C.
"""

from __future__ import annotations

import argparse
import random
import sys
from dataclasses import dataclass
from pathlib import Path

# El modulo hermano vive junto a este script; se anade su carpeta al path para poder
# importarlo tanto con "python3 scripts/send_story_mails.py" como desde cualquier CWD.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from mockup_mailer import (  # noqa: E402  (import despues de tocar sys.path, a proposito)
    DEFAULT_HOST,
    DEFAULT_PLAIN_PORT,
    DEFAULT_RECIPIENT,
    DEFAULT_SENDER,
    DEFAULT_STARTTLS_PORT,
    DEFAULT_TIMEOUT,
    Attachment,
    Console,
    MockupMailer,
    Palette,
    SmtpSendError,
    Story,
    color_enabled,
    configure_stdio,
    generate_story,
)


@dataclass(frozen=True)
class MailPlan:
    """Un correo de la serie: que historia lleva y como de completo es el mensaje."""

    kind: str
    subject: str
    note: str
    with_html: bool
    with_attachment: bool


# Los tres correos, en orden. Uno de cada tipo de historia, y cada uno con una forma
# distinta de mensaje, para que la UI y los JSON ensenen cosas diferentes.
PLANS = (
    MailPlan(
        kind="fabula",
        subject="Historia clásica 1/3: una fábula generada",
        note="solo texto",
        with_html=False,
        with_attachment=False,
    ),
    MailPlan(
        kind="cuento_de_tres",
        subject="Historia clásica 2/3: el cuento de los tres",
        note="texto + HTML",
        with_html=True,
        with_attachment=False,
    ),
    MailPlan(
        kind="chiste",
        subject="Historia clásica 3/3: el chiste del bar",
        note="texto + HTML + adjunto",
        with_html=True,
        with_attachment=True,
    ),
)


def compose_text(story: Story, index: int, total: int) -> str:
    """Cuerpo en texto plano: un preambulo corto y la historia."""
    return (
        "Hola,\n\n"
        f"Aquí va la historia {index} de {total}, generada al azar por "
        "scripts/send_story_mails.py.\n\n"
        f"— {story.title} —\n\n"
        f"{story.text}\n\n"
        "Un saludo,\n"
        "el mockup (que no relega a nadie y lo guarda todo tal cual)\n"
    )


def attachment_for(story: Story) -> Attachment:
    """La historia como adjunto .txt; el nombre sale del tipo de historia."""
    underline = "=" * len(story.title)
    return Attachment(
        filename=f"historia-{story.kind}.txt",
        text=f"{story.title}\n{underline}\n\n{story.text}\n",
        content_type="text/plain",
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="send_story_mails.py",
        description=(
            "Manda al smtp-mockup tres correos con historias generadas "
            "(fabula, cuento de tres y chiste)."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Ejemplos:\n"
            "  python3 scripts/send_story_mails.py                 # 127.0.0.1:8025\n"
            "  python3 scripts/send_story_mails.py --starttls       # 127.0.0.1:8443\n"
            "  python3 scripts/send_story_mails.py --dry-run        # sin enviar\n"
            "  python3 scripts/send_story_mails.py --host 10.0.0.5 --port 9025\n"
        ),
    )
    parser.add_argument(
        "--host",
        default=DEFAULT_HOST,
        help=f"Host del mockup (por defecto {DEFAULT_HOST}).",
    )
    parser.add_argument(
        "--port",
        type=int,
        default=None,
        help=f"Puerto SMTP. Por defecto {DEFAULT_PLAIN_PORT} "
        f"({DEFAULT_STARTTLS_PORT} con --starttls).",
    )
    parser.add_argument(
        "--starttls",
        action="store_true",
        help=f"Subir la conexion a TLS con STARTTLS (normalmente el puerto "
        f"{DEFAULT_STARTTLS_PORT}).",
    )
    parser.add_argument(
        "--verify-cert",
        action="store_true",
        help="Exigir un certificado valido; el del mockup es autofirmado, asi que "
        "sin esto no se comprueba (como en el ejemplo del README).",
    )
    parser.add_argument(
        "--from",
        dest="sender",
        default=DEFAULT_SENDER,
        metavar="DIRECCION",
        help=f"Remitente (por defecto {DEFAULT_SENDER}).",
    )
    parser.add_argument(
        "--to",
        action="append",
        metavar="DIRECCION",
        help=f"Destinatario, repetible. Por defecto {DEFAULT_RECIPIENT}.",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Semilla del generador aleatorio (misma semilla, mismos tres correos).",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=DEFAULT_TIMEOUT,
        help=f"Tiempo maximo de la conexion, en segundos (por defecto {DEFAULT_TIMEOUT}).",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Generar y ensenar los tres correos sin enviarlos.",
    )
    parser.add_argument("--no-color", action="store_true", help="Salida sin codigos de color.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    configure_stdio()
    console = Console(Palette(color_enabled(args.no_color)))

    console.banner("smtp-mockup - tres correos con historias")

    port = args.port
    if port is None:
        port = DEFAULT_STARTTLS_PORT if args.starttls else DEFAULT_PLAIN_PORT
    recipients = args.to or [DEFAULT_RECIPIENT]

    mailer = MockupMailer(
        host=args.host,
        port=port,
        starttls=args.starttls,
        sender=args.sender,
        timeout=args.timeout,
        verify_certificate=args.verify_cert,
    )

    console.kv("servidor", mailer.endpoint)
    console.kv("de", args.sender)
    console.kv("para", ", ".join(recipients))
    if not args.starttls and args.host not in ("127.0.0.1", "localhost"):
        console.warn("sin cifrar a un host que no es localhost; es un mockup de pruebas")

    rng = random.Random(args.seed)
    if args.seed is not None:
        console.info(f"semilla {args.seed}: las historias se repiten tal cual")

    total = len(PLANS)
    failures = 0

    for index, plan in enumerate(PLANS, 1):
        story = generate_story(plan.kind, rng)
        console.step(index, total, plan.subject)
        console.kv("historia", f"{story.title} ({plan.note})")

        text = compose_text(story, index, total)
        html = story.html() if plan.with_html else None
        attachments = (attachment_for(story),) if plan.with_attachment else ()
        headers = {
            "X-Mockup-Script": "send_story_mails.py",
            "X-Mockup-Story": story.kind,
            "X-Mockup-Index": f"{index}/{total}",
        }

        if args.dry_run:
            console.info("dry-run: este correo NO se envia")
            console.detail(story.text)
            continue

        try:
            result = mailer.send(
                to=recipients,
                subject=plan.subject,
                text=text,
                html=html,
                attachments=attachments,
                headers=headers,
            )
        except SmtpSendError as exception:
            failures += 1
            console.fail(str(exception))
            continue

        console.ok(f"el mockup respondio {result.code} {result.response}".rstrip())
        if result.message_id:
            console.kv("id", result.message_id)
        console.kv("destinatarios", ", ".join(result.accepted))
        if result.refused:
            console.warn(f"rechazados: {', '.join(result.refused)}")
        console.kv("cuerpos", "texto + HTML" if plan.with_html else "texto")
        console.kv("adjuntos", str(len(attachments)))

    print()

    if args.dry_run:
        console.banner("previsualizacion: nada enviado")
        console.info("quita --dry-run para enviarlos de verdad")
        return 0

    if failures:
        console.banner("envio con problemas")
        console.fail(f"{failures} de {total} correos no salieron")
        console.info("¿esta arrancado el mockup? dotnet run --project src/SmtpMockup.Host")
        return 1

    console.banner("los tres correos salieron")
    console.info("en disco: data/messages/AAAA/MM/DD/<ULID>.json")
    console.info("en la UI: http://127.0.0.1:8888/")
    console.info("repetible con: python3 scripts/send_story_mails.py")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\ninterrumpido", file=sys.stderr)
        sys.exit(130)
