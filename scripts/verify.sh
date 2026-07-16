#!/usr/bin/env bash
# Full reliability battery for LmdbSharp. Run before merging engine changes.
#   scripts/verify.sh          # standard battery (~3-4 min)
#   scripts/verify.sh --long   # extended soak (~15 min)
set -euo pipefail
cd "$(dirname "$0")/.."

LONG=${1:-}
SEEDS=15; TXNS=120; KILLS=6; DIFFS=4
[ "$LONG" = "--long" ] && { SEEDS=50; TXNS=200; KILLS=15; DIFFS=10; }

echo "== unit + regression suites =="
dotnet test tests/Lmdb.Tests/Lmdb.Tests.csproj --nologo
dotnet test tests/Lmdb.Objects.Tests/Lmdb.Objects.Tests.csproj --nologo
dotnet test tests/LiveView.Tests/LiveView.Tests.csproj --nologo

echo "== randomized model-checked soak (strict walker, zero-leak) =="
dotnet run --project tests/Lmdb.Soak -- soak --seeds "$SEEDS" --txns "$TXNS"

echo "== SIGKILL crash-recovery soak =="
dotnet run --project tests/Lmdb.Soak --no-build -- app --cycles 20
dotnet run --project tests/Lmdb.Soak --no-build -- kill --iterations "$KILLS"

echo "== SIGBUS (disk-full delivery path) crash soak =="
dotnet run --project tests/Lmdb.Soak --no-build -- diskfull --iterations 3

echo "== fuzz smoke (walker + op programs) =="
dotnet run --project tests/Lmdb.Fuzz -- walker-rng --iterations 800
dotnet run --project tests/Lmdb.Fuzz --no-build -- ops-rng --iterations 150

echo "== differential validation vs C LMDB =="
dotnet run --project tests/Lmdb.Soak --no-build -- diff --seeds "$DIFFS" --ops 500

echo "ALL VERIFICATION PASSED"
