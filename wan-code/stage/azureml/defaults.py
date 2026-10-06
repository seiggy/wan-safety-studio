from __future__ import annotations

try:
    from .config import load_foundation
except ImportError:
    from config import load_foundation


def normalize_compute_name(value: str | None) -> str | None:
    if value is None:
        return None
    trimmed = value.strip()
    if not trimmed:
        return None
    return trimmed.removeprefix("azureml:")


_foundation = load_foundation()
DEFAULT_SUBSCRIPTION_ID = _foundation["subscriptionId"]
DEFAULT_RESOURCE_GROUP = _foundation["resourceGroupName"]
DEFAULT_WORKSPACE_NAME = _foundation["workspaceName"]
DEFAULT_COMPUTE = _foundation["computeName"]
DEFAULT_PROFILE = "wan"
DEFAULT_ENVIRONMENT_NAME = "wan"
DEFAULT_MODELS_NAME = "wan-models"

COMPUTE_HELP = "AzureML compute target from the selected studio foundation."
MISSING_COMPUTE_ERROR = "The selected studio foundation must define computeName."
