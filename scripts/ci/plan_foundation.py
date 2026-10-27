"""Create and validate one non-destructive saved foundation plan."""
import argparse
from pathlib import Path
from foundation import plan

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tfvars", type=Path, required=True)
    parser.add_argument("--backend", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    plan(args.tfvars, args.backend, args.work)
