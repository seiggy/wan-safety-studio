"""Authenticate to an SMB share (UNC path) for this process's logon session, so ShareRetrieval can read ComfyUI's output.

Windows uses WNetAddConnection2 (the password never appears on a command line). On Linux/macOS mount the share yourself
(mount.cifs / mount_smbfs) and point ComfyUi:OutputDir at the mount.
"""
from __future__ import annotations

import logging
import sys

LOGGER = logging.getLogger("wan.smb")
ERROR_ALREADY_ASSIGNED = (1219, 85)  # multiple connections with other creds / name already in use


def share_root(unc: str) -> str:
    parts = unc.replace("/", "\\").lstrip("\\").split("\\")
    if unc[:2] not in ("\\\\", "//") or len(parts) < 2 or not all(parts[:2]):
        raise ValueError(r"Expected a UNC path like \\server\share[\folder].")
    return "\\\\" + parts[0] + "\\" + parts[1]


def connect_share(unc: str, user: str, password: str, connector=None) -> str:
    root = share_root(unc)
    connector = connector or _wnet_connect
    code = connector(root, user, password)
    if code and code not in ERROR_ALREADY_ASSIGNED:
        raise OSError(f"Could not connect to {root} as {user} (Windows error {code}).")
    LOGGER.info("Connected to %s as %s", root, user)
    return root


def _wnet_connect(root: str, user: str, password: str) -> int:
    if sys.platform != "win32":
        raise OSError("SMB credentials are applied on Windows only; mount the share and use its mount path instead.")
    import ctypes
    from ctypes import wintypes

    class NetResource(ctypes.Structure):
        _fields_ = [("dwScope", wintypes.DWORD), ("dwType", wintypes.DWORD), ("dwDisplayType", wintypes.DWORD),
                    ("dwUsage", wintypes.DWORD), ("lpLocalName", wintypes.LPWSTR), ("lpRemoteName", wintypes.LPWSTR),
                    ("lpComment", wintypes.LPWSTR), ("lpProvider", wintypes.LPWSTR)]

    resource = NetResource(0, 1, 0, 0, None, root, None, None)  # RESOURCETYPE_DISK
    return ctypes.windll.mpr.WNetAddConnection2W(ctypes.byref(resource), password, user, 0)
