from __future__ import annotations

try:
    from .config import build_credential
except ImportError:
    from config import build_credential
