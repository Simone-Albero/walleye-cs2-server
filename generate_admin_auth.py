#!/usr/bin/env python3
"""Manage admin accounts in admin-auth.json for the WallEye admin panel.

Supports multiple admins.  Existing admins are preserved when adding a new one.
Running with the same username updates that admin's password in-place.
"""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
from pathlib import Path
import secrets
import sys


SCRYPT_N = 32768
SCRYPT_R = 8
SCRYPT_P = 1
SCRYPT_KEY_LENGTH = 64
SALT_LENGTH = 16
SCRYPT_MAX_MEMORY = 128 * SCRYPT_N * SCRYPT_R * SCRYPT_P * 2


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Manage admin accounts in admin-auth.json.",
    )
    subparsers = parser.add_subparsers(dest="command")

    # ── add (default for backward compat) ────────────────────────────────────
    add_parser = subparsers.add_parser("add", help="Add or update an admin account.")
    add_parser.add_argument("--username", help="Username. If omitted, the script prompts.")
    add_parser.add_argument("--password", help="Plain-text password. If omitted, the script prompts securely.")
    add_parser.add_argument("--cookie-secure", action="store_true",
                            help="Set cookie_secure to true for HTTPS deployments.")
    add_parser.add_argument("--write", type=Path,
                            help="Path to admin-auth.json. Reads existing admins and appends/updates.")

    # ── remove ────────────────────────────────────────────────────────────────
    rm_parser = subparsers.add_parser("remove", help="Remove an admin account.")
    rm_parser.add_argument("username", help="Username to remove.")
    rm_parser.add_argument("--write", type=Path, required=True,
                           help="Path to admin-auth.json to update in-place.")

    # ── list ─────────────────────────────────────────────────────────────────
    ls_parser = subparsers.add_parser("list", help="List admin usernames in the file.")
    ls_parser.add_argument("--file", type=Path, default=Path("admin-auth.json"),
                           help="Path to admin-auth.json (default: admin-auth.json).")

    # Backward-compat: if no subcommand, treat all args as 'add'
    parser.add_argument("--username", help=argparse.SUPPRESS)
    parser.add_argument("--password", help=argparse.SUPPRESS)
    parser.add_argument("--cookie-secure", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--write", type=Path, help=argparse.SUPPRESS)

    return parser.parse_args()


def _prompt_password() -> str:
    password = getpass.getpass("Admin password: ")
    password_confirmation = getpass.getpass("Confirm password: ")
    if password != password_confirmation:
        raise SystemExit("Passwords do not match.")
    if not password:
        raise SystemExit("Password cannot be empty.")
    return password


def _prompt_username() -> str:
    username = input("Admin username: ").strip()
    if not username:
        raise SystemExit("Username cannot be empty.")
    return username


def _generate_password_hash(password: str) -> str:
    salt = secrets.token_urlsafe(SALT_LENGTH)
    derived_key = hashlib.scrypt(
        password.encode("utf-8"),
        salt=salt.encode("utf-8"),
        n=SCRYPT_N,
        r=SCRYPT_R,
        p=SCRYPT_P,
        dklen=SCRYPT_KEY_LENGTH,
        maxmem=SCRYPT_MAX_MEMORY,
    )
    return f"scrypt:{SCRYPT_N}:{SCRYPT_R}:{SCRYPT_P}${salt}${derived_key.hex()}"


def _load_existing(path: Path) -> dict:
    """Load existing admin-auth.json and normalise to the multi-admin format."""
    if not path.exists():
        return {}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        raise SystemExit(f"Cannot read {path}: {exc}") from exc

    # Migrate old single-admin format → new admins list
    if "admins" not in raw and "username" in raw and "password_hash" in raw:
        raw["admins"] = [{"username": raw.pop("username"),
                          "password_hash": raw.pop("password_hash")}]
    raw.setdefault("admins", [])
    return raw


def _save(path: Path, payload: dict) -> None:
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


def _cmd_add(args: argparse.Namespace) -> int:
    username = (args.username or "").strip() or _prompt_username()
    password = args.password or _prompt_password()

    write_path: Path | None = args.write

    if write_path is not None:
        payload = _load_existing(write_path)
    else:
        payload = {}

    payload.setdefault("session_secret", secrets.token_urlsafe(48))
    payload.setdefault("cookie_secure", bool(getattr(args, "cookie_secure", False)))
    payload.setdefault("admins", [])

    # Update in-place if username already exists, otherwise append
    for entry in payload["admins"]:
        if entry.get("username") == username:
            entry["password_hash"] = _generate_password_hash(password)
            print(f"Updated password for '{username}'.", file=sys.stderr)
            break
    else:
        payload["admins"].append({
            "username": username,
            "password_hash": _generate_password_hash(password),
        })
        print(f"Added admin '{username}'.", file=sys.stderr)

    if write_path is not None:
        _save(write_path, payload)
        print(f"Saved {write_path}  ({len(payload['admins'])} admin(s)).", file=sys.stderr)
    else:
        json.dump(payload, sys.stdout, indent=2)
        sys.stdout.write("\n")

    return 0


def _cmd_remove(args: argparse.Namespace) -> int:
    payload = _load_existing(args.write)
    before = len(payload.get("admins", []))
    payload["admins"] = [e for e in payload.get("admins", [])
                         if e.get("username") != args.username]
    if len(payload["admins"]) == before:
        raise SystemExit(f"Admin '{args.username}' not found.")
    _save(args.write, payload)
    print(f"Removed '{args.username}'. {len(payload['admins'])} admin(s) remaining.", file=sys.stderr)
    return 0


def _cmd_list(args: argparse.Namespace) -> int:
    payload = _load_existing(args.file)
    admins = payload.get("admins", [])
    if not admins:
        print("No admins configured.")
    else:
        for i, entry in enumerate(admins, 1):
            print(f"  {i}. {entry.get('username', '(unnamed)')}")
    return 0


def main() -> int:
    args = _parse_args()
    # Subcommand dispatch
    if args.command == "remove":
        return _cmd_remove(args)
    if args.command == "list":
        return _cmd_list(args)
    # 'add' or no subcommand (backward-compat: top-level flags map to 'add')
    return _cmd_add(args)


if __name__ == "__main__":
    raise SystemExit(main())
