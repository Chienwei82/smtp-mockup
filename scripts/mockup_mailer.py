#!/usr/bin/env python3
"""
Envio de correo al smtp-mockup, sin dependencias externas (solo la biblioteca
estandar) y portable a Windows, Linux y macOS.

Es la version programable del ejemplo "Python (smtplib, sin dependencias)" del
README (seccion "Mandar correo al mockup"): el mismo servidor y los mismos puertos,
pero con el mensaje construido con la API `email` en vez de con una cadena a mano, y
con historias generadas de relleno.

Que hay aqui:
  * Character / Attachment / Story / SendResult  - tipos pequenos con nombre.
  * MockupMailer  - conecta a 127.0.0.1:8025 (en claro) o a 127.0.0.1:8443
                    (STARTTLS) y envia un mensaje. El mockup no pide credenciales,
                    asi que aqui tampoco hay ninguna. send() devuelve el 250 del
                    servidor (y si su texto trae "id=", lo deja en message_id).
  * generate_story / generate_stories  - generan historias "clasicas y graciosas"
                    (fabula, cuento de tres y chiste) combinando piezas de un
                    catalogo. Son deterministas si se les pasa una semilla.
  * Palette / Console  - salida por consola, compartida con send_story_mails.py.

El mockup NUNCA relega: cada mensaje se guarda como un JSON en
data/messages/AAAA/MM/DD/<ULID>.json y aparece solo en la UI,
http://127.0.0.1:8888/.

Uso como herramienta, para ver historias sin enviar nada:
    python3 scripts/mockup_mailer.py                 # 3 historias al azar
    python3 scripts/mockup_mailer.py --seed 7          # siempre las mismas
    python3 scripts/mockup_mailer.py --only chiste --html

Para enviar de verdad, ver scripts/send_story_mails.py.
"""

from __future__ import annotations

import argparse
import os
import random
import smtplib
import ssl
import sys
from dataclasses import dataclass, field
from email.message import EmailMessage
from email.utils import formatdate
from html import escape

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PLAIN_PORT = 8025
DEFAULT_STARTTLS_PORT = 8443
DEFAULT_SENDER = "dev@example.com"
DEFAULT_RECIPIENT = "destino@example.com"
DEFAULT_TIMEOUT = 10.0

# El orden fijo de la "serie" de tres historias. Uno de cada tipo, para que los tres
# correos se distingan de un vistazo en la UI.
STORY_ORDER = ("fabula", "cuento_de_tres", "chiste")


# --------------------------------------------------------------------------------------
# Consola (mismo estilo que scripts/publish.py)
# --------------------------------------------------------------------------------------


class Palette:
    """ANSI, apagado si no hay TTY, si NO_COLOR esta puesto o si se pide --no-color."""

    def __init__(self, enabled: bool) -> None:
        self.enabled = enabled

    def _wrap(self, code: str, text: str) -> str:
        return f"\033[{code}m{text}\033[0m" if self.enabled else text

    def bold(self, text: str) -> str:
        return self._wrap("1", text)

    def dim(self, text: str) -> str:
        return self._wrap("2", text)

    def red(self, text: str) -> str:
        return self._wrap("31", text)

    def green(self, text: str) -> str:
        return self._wrap("32", text)

    def yellow(self, text: str) -> str:
        return self._wrap("33", text)

    def blue(self, text: str) -> str:
        return self._wrap("34", text)

    def cyan(self, text: str) -> str:
        return self._wrap("36", text)


def color_enabled(no_color_flag: bool) -> bool:
    if no_color_flag:
        return False
    # Convencion de https://no-color.org: cualquier valor (incluso vacio) desactiva.
    if os.environ.get("NO_COLOR") is not None:
        return False
    if os.environ.get("TERM") == "dumb":
        return False
    return sys.stdout.isatty()


def configure_stdio() -> None:
    """Deja la salida en UTF-8 para que los acentos de las historias no revienten.

    En Windows, con una consola de codigo heredado (cp1252), imprimir una tilde sin
    esto lanza UnicodeEncodeError. Los mensajes de consola de estos scripts son ASCII,
    pero una historia previsualizada no (lleva tildes y enes). Si el interprete no
    soporta reconfigure (Python viejo), no pasa nada: se queda como estaba.
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


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
    def __init__(self, palette: Palette) -> None:
        self.c = palette

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
# Modelos
# --------------------------------------------------------------------------------------


class SmtpSendError(RuntimeError):
    """Fallo al hablar con el mockup, con un mensaje pensado para leerse en consola."""


@dataclass(frozen=True)
class Attachment:
    """Adjunto en memoria. Si se da `data` va crudo; si no, `text` se codifica UTF-8."""

    filename: str
    text: str | None = None
    data: bytes | None = None
    content_type: str = "text/plain"

    def as_bytes(self) -> bytes:
        if self.data is not None:
            return self.data
        return (self.text or "").encode("utf-8")

    @property
    def maintype(self) -> str:
        return self.content_type.split("/", 1)[0]

    @property
    def subtype(self) -> str:
        return self.content_type.split("/", 1)[-1]


@dataclass
class SendResult:
    """Lo que devolvio el servidor al aceptar un mensaje."""

    code: int
    response: str  # p.ej. "2.0.0 OK id=01JQ8Z3K7F9A2B3C4D5E6F7G8H"
    accepted: list[str] = field(default_factory=list)
    refused: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return self.code // 100 == 2

    @property
    def message_id(self) -> str | None:
        """El id ULID que el mockup puso al mensaje, si el 250 lo trae."""
        for token in self.response.split():
            if token.startswith("id="):
                return token[3:]
        return None


@dataclass(frozen=True)
class Story:
    """Una historia generada: tipo, titulo y cuerpo en texto plano."""

    kind: str
    title: str
    text: str

    def html(self) -> str:
        """Render minimo y seguro: titulo en <h2> y un <p> por parrafo."""
        body = [f"<h2>{escape(self.title)}</h2>"]
        for block in self.text.split("\n\n"):
            block = block.strip()
            if not block:
                continue
            inner = "<br>\n".join(escape(line) for line in block.splitlines())
            body.append(f"<p>{inner}</p>")
        return (
            '<!DOCTYPE html>\n<html lang="es">\n<head>\n<meta charset="utf-8">\n'
            f"<title>{escape(self.title)}</title>\n</head>\n<body>\n"
            + "\n".join(body)
            + "\n</body>\n</html>\n"
        )


# --------------------------------------------------------------------------------------
# Construccion del mensaje y cliente SMTP
# --------------------------------------------------------------------------------------


def build_message(
    *,
    sender: str,
    recipients: list[str],
    subject: str,
    text: str,
    html: str | None = None,
    attachments: tuple[Attachment, ...] = (),
    headers: dict[str, str] | None = None,
) -> EmailMessage:
    """Monta el mensaje.

    `set_content` + `add_alternative` dan los dos cuerpos (texto y HTML) y
    `add_attachment` convierte a multipart/mixed por su cuenta, asi que no hay que
    montar el arbol MIME a mano.
    """
    message = EmailMessage()
    message["From"] = sender
    message["To"] = ", ".join(recipients)
    message["Subject"] = subject
    message["Date"] = formatdate(localtime=True)
    for name, value in (headers or {}).items():
        # X-* y cualquier cabecera extra: el mockup las conserva en "headers".
        message[name] = value

    message.set_content(text)
    if html:
        message.add_alternative(html, subtype="html")

    for attachment in attachments:
        message.add_attachment(
            attachment.as_bytes(),
            maintype=attachment.maintype,
            subtype=attachment.subtype,
            filename=attachment.filename,
        )
    return message


class MockupMailer:
    """Cliente minimo para el smtp-mockup.

    No autentica, no cifra en el 8025 y acepta el certificado autofirmado del 8443
    salvo que se le pida lo contrario (verify_certificate=True). Es exactamente lo que
    hace el ejemplo del README, pero encapsulado y con errores que se entienden.
    """

    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PLAIN_PORT,
        *,
        starttls: bool = False,
        sender: str = DEFAULT_SENDER,
        timeout: float = DEFAULT_TIMEOUT,
        verify_certificate: bool = False,
    ) -> None:
        self.host = host
        self.port = port
        self.starttls = starttls
        self.sender = sender
        self.timeout = timeout
        self.verify_certificate = verify_certificate

    @property
    def endpoint(self) -> str:
        modo = "STARTTLS" if self.starttls else "claro"
        return f"{self.host}:{self.port} ({modo})"

    def _ssl_context(self) -> ssl.SSLContext:
        if self.verify_certificate:
            return ssl.create_default_context()
        # El certificado del mockup es autofirmado y de desarrollo: aceptarlo es lo que
        # espera quien lo usa. No comprueba nada, y eso aqui es lo correcto.
        return ssl._create_unverified_context()

    def connect(self) -> smtplib.SMTP:
        try:
            client = smtplib.SMTP(self.host, self.port, timeout=self.timeout)
        except OSError as exception:
            raise SmtpSendError(
                f"no se pudo conectar a {self.host}:{self.port} ({exception}).\n"
                "¿Esta arrancado el mockup? (dotnet run --project src/SmtpMockup.Host)"
            ) from exception

        # EHLO/HELO antes que nada: el mockup responde 501 a MAIL si no ha habido
        # saludo. sendmail()/send_message() lo mandan por su cuenta, pero aqui se usan
        # los comandos a mano, asi que hay que pedirlo. De paso, es lo que rellena
        # las capacidades que consulta has_extn().
        client.ehlo_or_helo_if_needed()

        if self.starttls:
            if not client.has_extn("starttls"):
                client.close()
                raise SmtpSendError(
                    f"{self.endpoint}: el servidor no anuncia STARTTLS. Prueba el puerto "
                    f"en claro ({DEFAULT_PLAIN_PORT}) o quita --starttls."
                )
            client.starttls(context=self._ssl_context())
        return client

    def send(
        self,
        *,
        to: str | list[str],
        subject: str,
        text: str,
        html: str | None = None,
        attachments: tuple[Attachment, ...] = (),
        headers: dict[str, str] | None = None,
    ) -> SendResult:
        """Envia el mensaje y devuelve el 250 del mockup (con su id ULID).

        Se usan los comandos a mano (mail/rcpt/data) en vez de send_message porque
        `data()` devuelve la respuesta final del servidor, y esa respuesta es la que
        trae el id que el mockup le ha puesto al mensaje. Con send_message no se ve.
        """
        recipients = [to] if isinstance(to, str) else list(to)
        message = build_message(
            sender=self.sender,
            recipients=recipients,
            subject=subject,
            text=text,
            html=html,
            attachments=attachments,
            headers=headers,
        )

        client = self.connect()
        try:
            code, response = client.mail(self.sender)
            if code // 100 != 2:
                raise SmtpSendError(f"el mockup rechazo el remitente: {code} {response!r}")

            accepted: list[str] = []
            refused: list[str] = []
            for address in recipients:
                code, response = client.rcpt(address)
                (accepted if code // 100 == 2 else refused).append(address)
            if not accepted:
                raise SmtpSendError(f"el mockup no acepto ningun destinatario: {refused}")

            code, response = client.data(message.as_bytes())
            if code // 100 != 2:
                raise SmtpSendError(f"el mockup rechazo el mensaje: {code} {response!r}")
            return SendResult(
                code=code,
                response=response.decode("utf-8", "replace"),
                accepted=accepted,
                refused=refused,
            )
        finally:
            try:
                client.quit()
            except (smtplib.SMTPException, OSError):
                client.close()


# --------------------------------------------------------------------------------------
# Historias generadas (clasicas y graciosas)
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Character:
    """Un personaje de fabula: como se nombra, su clave y su genero."""

    sujeto: str  # con articulo: "el zorro"
    clave: str   # sin articulo: "zorro"
    genero: str  # "m" o "f"

    def g(self, masculino: str, femenino: str) -> str:
        """La forma segun el genero, para no dejar un 'zorra' donde iba 'zorro'."""
        return masculino if self.genero == "m" else femenino


FABLE_CAST = (
    Character("el zorro", "zorro", "m"),
    Character("la liebre", "liebre", "f"),
    Character("el búho", "búho", "m"),
    Character("la tortuga", "tortuga", "f"),
    Character("el cuervo", "cuervo", "m"),
    Character("la hormiga", "hormiga", "f"),
    Character("el burro", "burro", "m"),
    Character("la urraca", "urraca", "f"),
)

FABLE_MORALS = (
    "El que corre sin mirar no llega antes; llega donde no quería.",
    "La prisa es una deuda que se paga con intereses.",
    "Ser el más listo sin ser el más paciente no sirve de nada.",
    "Hay dos formas de llegar pronto: correr, o salir antes.",
    "Quien presume de atajos suele acabar dando un buen rodeo.",
)


def _fable_intro(c: Character, rng: random.Random) -> str:
    return rng.choice(
        [
            f"Érase una vez {c.sujeto}, que {c.g('se tenía por el más listo', 'se tenía por la más lista')} de todo el valle.",
            f"En el valle vivía {c.sujeto}, con fama de {c.g('sabiondo', 'sabionda')} y la lengua siempre por delante.",
            f"Cuentan que {c.sujeto} no dejaba pasar un día sin recordarle a alguien lo mucho que valía.",
            f"Había en el valle {c.sujeto} que presumía de conocer todos los atajos, aunque nunca usara ninguno.",
        ]
    )


def _fable_meeting(c2: Character, rng: random.Random) -> str:
    return rng.choice(
        [
            f"Un día se cruzó con {c2.sujeto}, que iba a lo suyo sin ninguna prisa.",
            f"A la sombra de una higuera se encontró con {c2.sujeto}, {c2.g('sentado', 'sentada')} y muy {c2.g('entretenido', 'entretenida')} en no hacer nada.",
            f"Topó con {c2.sujeto}, que llevaba media hora mirando el mismo rincón del valle.",
            f"Se topó de bruces con {c2.sujeto}, que volvía de dar un paseo muy largo y muy lento.",
        ]
    )


def _fable_advice(c2: Character, rng: random.Random) -> str:
    line = rng.choice(
        [
            "Ser listo es saber cuándo no hace falta demostrarlo.",
            "El que corre sin mirar no llega antes: llega a otro sitio.",
            "La prisa es una deuda que se paga con intereses.",
            "Guarda una buena idea para el día en que fallen todas las demás.",
            "Nadie es tan rápido como para no poder esperar un momento.",
        ]
    )
    return f"{c2.sujeto.capitalize()} solo dijo:\n  —{line}"


def _fable_reaction(c1: Character, rng: random.Random) -> str:
    sujeto = c1.sujeto.capitalize()
    return rng.choice(
        [
            f"{sujeto} asintió con la cabeza y no escuchó ni una palabra.",
            f"{sujeto} se echó a reír y se fue {c1.g('tan contento', 'tan contenta')} sin aprender nada.",
            f"{sujeto} fingió escuchar, que es la forma educada de no escuchar.",
            f"{sujeto} dio las gracias y siguió {c1.g('convencido', 'convencida')} de que sabía más que nadie.",
        ]
    )


def _fable_ending(c1: Character, c2: Character, rng: random.Random) -> str:
    return rng.choice(
        [
            f"Poco después, {c1.sujeto} se metió en un lío que {c1.g('él mismo', 'ella misma')} se había buscado, "
            f"mientras {c2.sujeto} seguía a lo suyo, {c2.g('tan tranquilo', 'tan tranquila')}.",
            f"Al primer intento de lucirse, {c1.sujeto} tropezó, se le escapó la idea y se enteró el valle entero. "
            f"{c2.sujeto.capitalize()}, en cambio, terminó el día igual de {c2.g('tranquilo', 'tranquila')} que lo empezó.",
            f"Y fue entonces cuando {c1.sujeto} entendió que hablar era mucho más fácil que hacer, "
            f"con {c2.sujeto} mirando desde lejos sin decir nada.",
        ]
    )


def build_fable(rng: random.Random) -> Story:
    c1, c2 = rng.sample(FABLE_CAST, 2)
    paragraphs = [
        _fable_intro(c1, rng),
        _fable_meeting(c2, rng),
        _fable_advice(c2, rng),
        _fable_reaction(c1, rng),
        _fable_ending(c1, c2, rng),
        f"Moraleja: {rng.choice(FABLE_MORALS)}",
    ]
    return Story(
        kind="fabula",
        title=f"Fábula: {c1.sujeto} y {c2.sujeto}",
        text="\n\n".join(paragraphs),
    )


TRIO_GROUPS = (
    {"plural": "los tres ratones", "uno": "el mayor", "dos": "el mediano", "tres": "el pequeño"},
    {"plural": "los tres cabritillos", "uno": "el más alto", "dos": "el del medio", "tres": "el más bajo"},
    {
        "plural": "los tres hermanos cerditos",
        "uno": "el que sabía de todo",
        "dos": "el que no sabía de nada",
        "tres": "el que no decía nada",
    },
)

THREE_OBJECTIVES = (
    "cruzar la cocina sin despertar al gato",
    "montar una despensa para todo el invierno",
    "llegar hasta la despensa grande de la esquina",
    "escapar del sótano y volver a casa",
    "cruzar el río sin mojarse",
)

THREE_FIRST_FAILS = (
    "con un plan enorme y ninguna idea de por dónde empezar",
    "con mucha prisa y muy poco sigilo",
    "convencido de que su forma era la única forma",
    "con demasiadas piezas y ninguna en su sitio",
)

THREE_SECOND_FAILS = (
    "y le salió todavía peor, que ya es decir",
    "y acabó en el mismo sitio del que había salido, pero más cansado",
    "y lo único que consiguió fue hacer más ruido que el primero",
    "con la idea de arreglar lo que había roto el otro, y rompió algo más",
)

THREE_GOOD_IDEAS = (
    "propuso hacerlo despacio y por partes",
    "tuvo la ocurrencia más tonta de las tres: empezar por el principio",
    "se quedó quieto a mirar y encontró la rendija que los otros dos habían pasado por alto",
    "propuso esperar a que los demás dejaran de discutir",
)

THREE_RESULTS = (
    "salieron del paso los tres",
    "el problema quedó resuelto sin que nadie tuviera que gritar",
    "llegaron a donde querían con tiempo de sobra",
    "quedó claro quién había estado haciendo las cosas con cabeza",
)

THREE_MORALS = (
    "A veces el plan más listo es el más corto.",
    "El que hace menos ruido suele ser el que encuentra la puerta.",
    "No hace falta ser el más rápido; hace falta hacerlo bien una vez.",
    "Los que más gritan suelen ser los que aún no han probado nada.",
)


def build_three_tale(rng: random.Random) -> Story:
    group = rng.choice(TRIO_GROUPS)
    paragraphs = [
        f"Un día, {group['plural']} decidieron {rng.choice(THREE_OBJECTIVES)}: "
        f"{group['uno']}, {group['dos']} y {group['tres']}. El plan era sencillo; "
        "ponerlo en práctica, ya era otro cantar.",
        f"{group['uno'].capitalize()} fue primero, {rng.choice(THREE_FIRST_FAILS)}.",
        f"{group['dos'].capitalize()} lo intentó después, {rng.choice(THREE_SECOND_FAILS)}.",
        f"Solo {group['tres']} {rng.choice(THREE_GOOD_IDEAS)}, y así, sin ruido ni "
        f"aspavientos, {rng.choice(THREE_RESULTS)}.",
        f"Moraleja: {rng.choice(THREE_MORALS)}",
    ]
    return Story(
        kind="cuento_de_tres",
        title=f"Cuento de tres: {group['plural']}",
        text="\n\n".join(paragraphs),
    )


JOKE_PROTAGONISTS = (
    "un informático",
    "una profesora de matemáticas",
    "un fontanero",
    "una abogada",
    "un músico de bodas",
    "una repostera",
    "un monitor de gimnasio",
    "una traductora",
)

JOKE_REQUESTS = (
    "un café solo, sin hielo, sin leche y sin café",
    "un vaso de agua, pero con exactamente siete hielos",
    "una tostada que no esté tostada",
    "lo que tome el de la mesa tres, pero al revés",
    "una cerveza sin alcohol, sin gluten y sin alegría",
)

JOKE_BARMAN_LINES = (
    "Aquí no servimos milagros, y menos a las once de la mañana.",
    "Mire, yo cobro por servir copas, no por arreglarle el día a nadie.",
    "Eso lo piden todos los lunes. Los lunes cerramos.",
    "Perfecto. ¿Y de beber?",
)

JOKE_REPLIES = (
    "Entonces póngame lo de siempre, que ya sabe lo que me gusta.",
    "No era una petición, era un examen. Ha suspendido.",
    "Tranquilo, que yo venía a lo mismo: a quejarme.",
    "Es lo que tiene ser exigente: que luego no hay quien me aguante.",
)

JOKE_REACTIONS = (
    "se ríe a carcajadas",
    "aplaude sin entender nada",
    "sigue a lo suyo, que es tomarse el café en paz",
)

JOKE_ENDINGS = (
    "y quedan los dos como viejos amigos, que es lo que acaba pasando siempre en los bares",
    "y al final lo de siempre: se pide lo fácil y se brinda por lo difícil",
    "y el cliente se va contento por haber ganado una discusión que no era la suya",
)


def build_joke(rng: random.Random) -> Story:
    protagonista = rng.choice(JOKE_PROTAGONISTS)
    paragraphs = [
        f"Entra {protagonista} en un bar y pide {rng.choice(JOKE_REQUESTS)}.",
        f"El dueño se lo queda mirando y le suelta:\n  —{rng.choice(JOKE_BARMAN_LINES)}",
        f"{protagonista.capitalize()} responde sin pestañear:\n  —{rng.choice(JOKE_REPLIES)}",
        f"Y así, {rng.choice(JOKE_REACTIONS)}, {rng.choice(JOKE_ENDINGS)}.",
        "Moraleja: en un bar, la razón y las ganas nunca llegan a la vez.",
    ]
    return Story(
        kind="chiste",
        title="Chiste: el cliente del bar",
        text="\n\n".join(paragraphs),
    )


STORY_BUILDERS = {
    "fabula": build_fable,
    "cuento_de_tres": build_three_tale,
    "chiste": build_joke,
}


# --------------------------------------------------------------------------------------
# API publica de historias y herramienta de previsualizacion
# --------------------------------------------------------------------------------------


def generate_story(kind: str | None = None, rng: random.Random | None = None) -> Story:
    """Genera UNA historia. Sin `kind`, elige uno de los tres tipos al azar.

    Con un `rng` compartido, dos llamadas seguidas dan historias distintas pero
    reproducibles: pasar `random.Random(semilla)` es lo que hace que dos ejecuciones
    con la misma semilla den exactamente lo mismo.
    """
    rng = rng or random.Random()
    kind = kind or rng.choice(STORY_ORDER)
    builder = STORY_BUILDERS.get(kind)
    if builder is None:
        valid = ", ".join(STORY_BUILDERS)
        raise ValueError(f"tipo de historia desconocido: {kind!r}. Validos: {valid}")
    return builder(rng)


def generate_stories(rng: random.Random | None = None) -> list[Story]:
    """Las tres historias de la serie, una de cada tipo y siempre en el mismo orden."""
    rng = rng or random.Random()
    return [STORY_BUILDERS[kind](rng) for kind in STORY_ORDER]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="mockup_mailer.py",
        description=(
            "Genera las historias que manda scripts/send_story_mails.py y las ensena "
            "por pantalla, sin enviar nada."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Ejemplos:\n"
            "  python3 scripts/mockup_mailer.py                  # las tres al azar\n"
            "  python3 scripts/mockup_mailer.py --seed 7          # siempre las mismas\n"
            "  python3 scripts/mockup_mailer.py --only chiste     # una sola\n"
            "  python3 scripts/mockup_mailer.py --html            # ensena tambien el HTML\n"
        ),
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Semilla del generador aleatorio (misma semilla, mismas historias).",
    )
    parser.add_argument(
        "--only",
        choices=STORY_ORDER,
        metavar="TIPO",
        help=f"Generar solo un tipo ({', '.join(STORY_ORDER)}). Por defecto, los tres.",
    )
    parser.add_argument(
        "--html",
        action="store_true",
        help="Ensenar tambien el cuerpo HTML de cada historia.",
    )
    parser.add_argument("--no-color", action="store_true", help="Salida sin codigos de color.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    configure_stdio()
    console = Console(Palette(color_enabled(args.no_color)))

    console.banner("smtp-mockup - historias generadas")
    rng = random.Random(args.seed)
    if args.seed is not None:
        console.info(f"semilla {args.seed}: estas historias se repiten tal cual")

    kinds = [args.only] if args.only else list(STORY_ORDER)
    for index, kind in enumerate(kinds, 1):
        story = generate_story(kind, rng)
        console.step(index, len(kinds), story.title)
        print(story.text)
        if args.html:
            print()
            console.detail("HTML:")
            print(story.html())

    print()
    console.info(
        "esto solo previsualiza; para enviarlos: python3 scripts/send_story_mails.py"
    )
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\ninterrumpido", file=sys.stderr)
        sys.exit(130)
