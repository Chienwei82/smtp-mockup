#!/usr/bin/env python3
"""
Automatiza la creación de nuevas ramas de trabajo (feature, fix, chore, etc.)
a partir de la rama actual, asegurando que esté actualizada y limpia.

Flujo:
1. Verifica el estado del árbol de trabajo.
   - Si hay cambios, pregunta si quieres guardarlos con `git stash`.
2. Actualiza la rama actual (fast-forward only).
3. Solicita tipo y nombre de la nueva rama (o los toma de argumentos).
4. Crea y cambia a la nueva rama usando `git switch -c`.
5. Si se hizo stash, restaura los cambios con `git stash pop`.
6. (Opcional) Sube la rama al remoto y configura tracking.

Seguridad:
- Usa `--ff-only` para evitar merges accidentales al actualizar.
- Si `stash pop` genera conflictos, te avisa y no los resuelve solo.
- Sanitiza los nombres de rama a kebab-case (minúsculas y guiones).
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from enum import Enum

# ==============================================================================
# CONFIGURACIÓN
# ==============================================================================
class BranchType(str, Enum):
    FEAT = "feat"
    FIX = "fix"
    CHORE = "chore"
    REFACTOR = "refactor"
    HOTFIX = "hotfix"
    DOCS = "docs"

VALID_TYPES = [t.value for t in BranchType]

# ==============================================================================
# GIT WRAPPERS
# ==============================================================================
def git_output(args: list[str]) -> str:
    """Ejecuta git y devuelve stdout. Lanza excepción si falla."""
    try:
        result = subprocess.run(
            ["git", *args], capture_output=True, text=True, check=True
        )
        return result.stdout.rstrip("\n")
    except subprocess.CalledProcessError as e:
        print(f"  [error] git {' '.join(args)}: {e.stderr.strip()}", file=sys.stderr)
        raise

def git_check(args: list[str]) -> bool:
    """Ejecuta git y devuelve True si el código de salida es 0."""
    return subprocess.run(["git", *args], capture_output=True).returncode == 0

# ==============================================================================
# LÓGICA DE NEGOCIO
# ==============================================================================
def has_uncommitted_changes() -> bool:
    """Detecta cambios staged, unstaged o untracked."""
    output = git_output(["status", "--porcelain"])
    return len(output.strip()) > 0

def get_change_summary() -> str:
    """Retorna un resumen legible de los cambios pendientes."""
    output = git_output(["status", "--short"])
    lines = output.strip().splitlines()
    if not lines:
        return ""
    
    staged = sum(1 for l in lines if l[0] != " " and l[0] != "?")
    unstaged = sum(1 for l in lines if l[1] != " " and l[0] != "?")
    untracked = sum(1 for l in lines if l.startswith("??"))
    
    parts = []
    if staged:
        parts.append(f"{staged} staged")
    if unstaged:
        parts.append(f"{unstaged} sin stage")
    if untracked:
        parts.append(f"{untracked} untracked")
    return ", ".join(parts)

def stash_push(message: str, is_dry_run: bool) -> bool:
    """Guarda los cambios locales con un mensaje descriptivo."""
    if is_dry_run:
        print(f"  [dry] git stash push -m \"{message}\"")
        return True

    try:
        # --include-untracked: también guarda archivos nuevos sin trackear
        git_output(["stash", "push", "--include-untracked", "-m", message])
        print("  [ok] Cambios guardados en stash.")
        return True
    except subprocess.CalledProcessError:
        print("  [error] No se pudo hacer stash. Revisa el estado manualmente.", file=sys.stderr)
        return False

def stash_pop(is_dry_run: bool) -> bool:
    """Restaura los cambios del stash. Retorna False si hay conflictos."""
    if is_dry_run:
        print("  [dry] git stash pop")
        return True

    try:
        git_output(["stash", "pop"])
        print("  [ok] Cambios restaurados del stash.")
        return True
    except subprocess.CalledProcessError as e:
        # stash pop falla cuando hay conflictos de merge
        stderr = e.stderr.strip()
        if "CONFLICT" in stderr or "conflict" in stderr.lower():
            print("  [warn] ¡Conflicto al restaurar el stash!", file=sys.stderr)
            print("         Resuelve los conflictos manualmente y luego:", file=sys.stderr)
            print("         git stash drop", file=sys.stderr)
        else:
            print(f"  [error] No se pudo restaurar el stash: {stderr}", file=sys.stderr)
        return False

def get_current_branch() -> str:
    branch = git_output(["rev-parse", "--abbrev-ref", "HEAD"])
    if branch == "HEAD":
        print("  [error] Estás en un estado detached HEAD. No se puede crear rama.", file=sys.stderr)
        sys.exit(1)
    return branch

def update_current_branch(branch: str, is_dry_run: bool) -> bool:
    """Actualiza la rama actual usando fast-forward."""
    print(f"  -> Actualizando '{branch}' desde el remoto (fast-forward)...")
    if is_dry_run:
        print("  [dry] git pull --ff-only")
        return True

    try:
        git_output(["pull", "--ff-only"])
        print(f"  [ok] '{branch}' está al día.")
        return True
    except subprocess.CalledProcessError:
        print("  [error] La rama local ha divergido del remoto.", file=sys.stderr)
        print("          Resuelve los conflictos manualmente.", file=sys.stderr)
        return False

def sanitize_branch_name(name: str) -> str:
    """Convierte cualquier string a un nombre de rama válido (kebab-case)."""
    name = name.lower().strip()
    name = re.sub(r'[\s/_]+', '-', name)
    name = re.sub(r'[^a-z0-9\-]', '', name)
    return name.strip('-')

def prompt_user(message: str, options: list[str] | None = None) -> str:
    """Función auxiliar para prompts interactivos."""
    while True:
        if options:
            opts_str = ", ".join(options)
            user_input = input(f"{message} [{opts_str}]: ").strip().lower()
            if user_input in options:
                return user_input
            print(f"  Opción inválida. Elige una de: {opts_str}")
        else:
            user_input = input(f"{message}: ").strip()
            if user_input:
                return user_input
            print("  El nombre no puede estar vacío.")

def confirm_action(prompt: str, auto_yes: bool) -> bool:
    if auto_yes:
        return True
    try:
        response = input(f"{prompt} [y/N] ").strip().lower()
        return response in ("y", "yes")
    except EOFError:
        return False

def branch_exists(name: str) -> bool:
    """Verifica si la rama ya existe local o remotamente."""
    local_ref = f"refs/heads/{name}"
    remote_ref = f"refs/remotes/origin/{name}"
    return (git_check(["show-ref", "--verify", "--quiet", local_ref]) or
            git_check(["show-ref", "--verify", "--quiet", remote_ref]))

# ==============================================================================
# PUNTO DE ENTRADA
# ==============================================================================
def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Crea y prepara una nueva rama de trabajo a partir de la actual.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Ejemplos:\n"
               "  python3 git_new_branch.py\n"
               "  python3 git_new_branch.py -t fix -n 'login-bug'\n"
               "  python3 git_new_branch.py --push --yes"
    )
    parser.add_argument("--type", "-t", choices=VALID_TYPES, help="Tipo de rama (feat, fix, chore, etc.)")
    parser.add_argument("--name", "-n", help="Nombre descriptivo de la rama (se sanitizará automáticamente)")
    parser.add_argument("--push", "-p", action="store_true", help="Hace push inmediato y configura upstream (-u)")
    parser.add_argument("--yes", "-y", action="store_true", help="Omite las confirmaciones interactivas")
    parser.add_argument("--dry-run", action="store_true", help="Muestra qué haría sin ejecutar cambios")
    return parser.parse_args()

def main() -> None:
    args = parse_arguments()
    is_dry_run = args.dry_run
    auto_yes = args.yes

    status = " (DRY-RUN)" if is_dry_run else ""
    print(f"== Creación de Nueva Rama{status} ==\n")

    # ------------------------------------------------------------------
    # 1. Verificar estado del repositorio
    # ------------------------------------------------------------------
    print("1. Verificando estado del repositorio...")
    current_branch = get_current_branch()
    print(f"  -> Rama base actual: '{current_branch}'")

    did_stash = False
    if has_uncommitted_changes():
        summary = get_change_summary()
        print(f"  [warn] Tienes cambios sin commitear ({summary}).")

        if not confirm_action("  ¿Guardar cambios con 'git stash' y restaurarlos en la nueva rama?", auto_yes):
            print("  [abort] Operación cancelada. Haz commit o stash manualmente.", file=sys.stderr)
            sys.exit(1)

        stash_msg = f"auto-stash: cambios pendientes antes de crear rama desde {current_branch}"
        if not stash_push(stash_msg, is_dry_run):
            sys.exit(1)
        did_stash = True
    else:
        print("  [ok] Árbol de trabajo limpio.")

    # ------------------------------------------------------------------
    # 2. Actualizar rama base
    # ------------------------------------------------------------------
    print("\n2. Sincronizando rama base...")
    if not update_current_branch(current_branch, is_dry_run):
        # Si falló y habíamos hecho stash, intentamos restaurar antes de salir
        if did_stash:
            print("\n  -> Restaurando stash antes de salir...")
            stash_pop(is_dry_run)
        sys.exit(1)

    # ------------------------------------------------------------------
    # 3. Definir nombre de la nueva rama
    # ------------------------------------------------------------------
    print("\n3. Configurando nueva rama...")
    branch_type = args.type or prompt_user("Selecciona el tipo de rama", VALID_TYPES)
    raw_name = args.name or prompt_user("Describe brevemente el trabajo (ej. 'Add login page')")

    clean_name = sanitize_branch_name(raw_name)
    full_branch_name = f"{branch_type}/{clean_name}"

    print(f"  -> Nombre final propuesto: '{full_branch_name}'")

    if branch_exists(full_branch_name):
        print(f"  [error] La rama '{full_branch_name}' ya existe.", file=sys.stderr)
        if did_stash:
            print("\n  -> Restaurando stash antes de salir...")
            stash_pop(is_dry_run)
        sys.exit(1)

    # ------------------------------------------------------------------
    # 4. Crear y cambiar de rama
    # ------------------------------------------------------------------
    print("\n4. Creando y cambiando a la nueva rama...")
    if is_dry_run:
        print(f"  [dry] git switch -c {full_branch_name}")
    else:
        git_output(["switch", "-c", full_branch_name])
        print(f"  [ok] Cambiado a nueva rama: '{full_branch_name}'")

    # ------------------------------------------------------------------
    # 5. Restaurar stash si aplica
    # ------------------------------------------------------------------
    if did_stash:
        print("\n5. Restaurando cambios guardados...")
        if not stash_pop(is_dry_run):
            print("  [warn] La rama fue creada pero el stash tiene conflictos.")
            print("         Revisa 'git status' y 'git stash list'.")

    # ------------------------------------------------------------------
    # 6. Push remoto (Opcional)
    # ------------------------------------------------------------------
    if args.push:
        step = "6" if did_stash else "5"
        print(f"\n{step}. Subiendo rama al remoto y configurando tracking...")
        if is_dry_run:
            print(f"  [dry] git push -u origin {full_branch_name}")
        else:
            git_output(["push", "-u", "origin", full_branch_name])
            print(f"  [ok] Rama publicada en origin/{full_branch_name}")

    print("\n== ¡Listo! A programar. ==")

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\nOperación cancelada por el usuario.", file=sys.stderr)
        sys.exit(130)
    except subprocess.CalledProcessError:
        sys.exit(1)