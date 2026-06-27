#!/usr/bin/env bash
set -euo pipefail

configuration="Debug"

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)
            if [[ $# -lt 2 ]]; then
                echo "Missing value for $1." >&2
                exit 2
            fi
            configuration="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: $0 [-c|--configuration Debug|Release]" >&2
            exit 2
            ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Configuration must be Debug or Release." >&2
        exit 2
        ;;
esac

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

"$script_dir/build-linux.sh" --configuration "$configuration"
dotnet run --no-build --project "$repo_root/RDPilot.Client/RDPilot.Client.csproj" -c "$configuration"
