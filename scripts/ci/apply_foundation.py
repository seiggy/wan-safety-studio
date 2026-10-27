"""Apply only the current protected run's exact approved saved plan."""
import argparse
from pathlib import Path
from foundation import apply

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tfvars", type=Path, required=True)
    parser.add_argument("--backend", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    apply(args.tfvars, args.backend, args.work)
