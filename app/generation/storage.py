"""Pick the object store from the environment: local folder (incl. an NFS/SMB mount), Azurite, or Azure Blob."""
from __future__ import annotations

import os
from pathlib import Path

from vision.adapters.storage import BlobStore, LocalStore

BACKENDS = ("local", "azurite", "azure")


def store_from_env(env=os.environ, default_dir: Path = Path(".dev-data/blob")):
    backend = env.get("WAN_STUDIO_STORAGE", "local")
    container = env.get("WAN_STUDIO_CONTAINER", "studio")
    if backend == "local":
        # A LAN share is just a mounted path (\\server\share or /mnt/share); no special client.
        root = Path(env.get("WAN_STUDIO_STORAGE_PATH") or default_dir)
        root.mkdir(parents=True, exist_ok=True)
        return LocalStore(root), f"folder {root}"
    from azure.storage.blob import BlobServiceClient
    if backend == "azurite":
        connection = env.get("WAN_STUDIO_AZURITE_CONNECTION") or env.get("ConnectionStrings__blobs")
        if not connection:
            raise ValueError("Azurite storage needs ConnectionStrings__blobs (set by the AppHost) or WAN_STUDIO_AZURITE_CONNECTION.")
        service = BlobServiceClient.from_connection_string(connection)
        label = "Azurite"
    elif backend == "azure":
        from azure.identity import DefaultAzureCredential
        account = env.get("WAN_STUDIO_STORAGE_ACCOUNT")
        if not account:
            raise ValueError("Azure storage needs WAN_STUDIO_STORAGE_ACCOUNT.")
        service = BlobServiceClient(f"https://{account}.blob.core.windows.net", credential=DefaultAzureCredential())
        label = f"Azure Storage {account}"
    else:
        raise ValueError(f"WAN_STUDIO_STORAGE must be one of {BACKENDS}.")

    def open_container():
        client = service.get_container_client(container)
        if not client.exists():
            client.create_container()
        return client

    return BlobStore(open_container), f"{label}/{container}"
