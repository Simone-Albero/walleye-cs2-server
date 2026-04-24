#!/usr/bin/env python3
"""Generate admin-auth.json content for the WallEye admin panel."""

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
        description="Generate JSON for admin-auth.json.",
    )
    parser.add_argument(
        "--username",
        default="admin",
        help="Username for the admin dashboard login.",
    )
    parser.add_argument(
        "--password",
        help="Plain-text password. If omitted, the script prompts securely.",
    )
    parser.add_argument(
        "--cookie-secure",
        action="store_true",
        help="Set cookie_secure to true for HTTPS deployments.",
    )
    parser.add_argument(
        "--write",
        type=Path,
        help="Write the generated JSON to this path instead of stdout.",
    )
    return parser.parse_args()


def _prompt_password() -> str:
    password = getpass.getpass("Admin password: ")
    password_confirmation = getpass.getpass("Confirm password: ")
    if password != password_confirmation:
        raise SystemExit("Passwords do not match.")
    if not password:
        raise SystemExit("Password cannot be empty.")
    return password


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


def main() -> int:
    args = _parse_args()
    username = args.username.strip()
    if not username:
        raise SystemExit("Username cannot be empty.")

    password = args.password if args.password is not None else _prompt_password()
    if not password:
        raise SystemExit("Password cannot be empty.")

    payload = {
        "username": username,
        "password_hash": _generate_password_hash(password),
        "session_secret": secrets.token_urlsafe(48),
        "cookie_secure": bool(args.cookie_secure),
    }

    if args.write is not None:
        args.write.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    else:
        json.dump(payload, sys.stdout, indent=2)
        sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
