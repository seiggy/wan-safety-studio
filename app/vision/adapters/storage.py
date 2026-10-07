"""Store adapters: a local directory (tests, local runner) and the studio's private Blob container."""
from __future__ import annotations

from pathlib import Path, PurePosixPath
import shutil

from azure.core.exceptions import ResourceExistsError, ResourceNotFoundError


def safe_key(key: str):
    path = PurePosixPath(key)
    if not key or path.is_absolute() or any(part in ("", ".", "..") or "\\" in part for part in path.parts):
        raise ValueError("Invalid storage key.")
    return key


class LocalStore:
    def __init__(self, root: Path):
        self.root = root

    def _path(self, key):
        return self.root / safe_key(key)

    def exists(self, key):
        return self._path(key).is_file()

    def read(self, key):
        path = self._path(key)
        return path.read_bytes() if path.is_file() else None

    def write(self, key, data, overwrite=True):
        path = self._path(key)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("wb" if overwrite else "xb") as handle:
            handle.write(data)

    def upload(self, key, path, overwrite=False):
        target = self._path(key)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and not overwrite:
            raise FileExistsError(key)
        shutil.copyfile(path, target)

    def download(self, key, path):
        shutil.copyfile(self._path(key), path)

    def list(self, prefix):
        base = self._path(prefix.rstrip("/"))
        return sorted(p.relative_to(self.root).as_posix() for p in base.rglob("*") if p.is_file()) if base.is_dir() else []


class BlobStore:
    def __init__(self, open_container):
        self._open, self._container = open_container, None

    @property
    def container(self):
        # Opened on first use so startup needs no Azure call or credential.
        if self._container is None:
            self._container = self._open()
        return self._container

    def exists(self, key):
        return self.container.get_blob_client(safe_key(key)).exists()

    def read(self, key):
        try:
            return self.container.get_blob_client(safe_key(key)).download_blob().readall()
        except ResourceNotFoundError:
            return None

    def write(self, key, data, overwrite=True):
        try:
            self.container.get_blob_client(safe_key(key)).upload_blob(data, overwrite=overwrite)
        except ResourceExistsError:
            raise FileExistsError(key) from None

    def upload(self, key, path, overwrite=False):
        try:
            with path.open("rb") as handle:
                self.container.get_blob_client(safe_key(key)).upload_blob(handle, overwrite=overwrite)
        except ResourceExistsError:
            raise FileExistsError(key) from None

    def download(self, key, path):
        with path.open("wb") as handle:
            self.container.get_blob_client(safe_key(key)).download_blob().readinto(handle)

    def list(self, prefix):
        return sorted(blob.name for blob in self.container.list_blobs(name_starts_with=prefix.rstrip("/") + "/"))
