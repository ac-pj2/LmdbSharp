#!/usr/bin/env bash
# Benchmark regression guard: run the same workload through the C# engine and
# native liblmdb on this machine, then require the C#/native throughput ratio
# per phase to clear a floor. Ratios are machine-independent, so this is safe
# on shared CI runners (absolute thresholds are not).
#
#   scripts/bench.sh              # 1M keys, 3 reps (~1-2 min)
#   COUNT=200000 scripts/bench.sh # smaller/faster
set -euo pipefail
cd "$(dirname "$0")/.."

export COUNT="${COUNT:-1000000}" REPS="${REPS:-3}"

echo "== building =="
dotnet build -c Release tests/Lmdb.QuickBench/Lmdb.QuickBench.csproj --nologo -v q

echo "== C# engine =="
CS_OUT=$(dotnet run -c Release --no-build --project tests/Lmdb.QuickBench -- cs)
echo "== native liblmdb =="
NATIVE_OUT=$(dotnet run -c Release --no-build --project tests/Lmdb.QuickBench -- native)

echo
echo "phase          C# ops/s      native ops/s   ratio   floor"
FAIL=0
# Floors: generous margins below current steady-state ratios so only real
# regressions trip them (current: seq-write ~4.5x, others ~0.9-1.4x).
for spec in seq-write:2.0 rnd-write:0.6 get-hit:0.7 get-miss:0.7 cursor-scan:0.5 overwrite:0.6 delete-half:0.6; do
  phase="${spec%%:*}"; floor="${spec##*:}"
  cs=$(awk -v p="$phase" '$1=="RESULT" && $3==p {print $4}' <<<"$CS_OUT")
  nat=$(awk -v p="$phase" '$1=="RESULT" && $3==p {print $4}' <<<"$NATIVE_OUT")
  if [ -z "$cs" ] || [ -z "$nat" ]; then
    echo "$phase: MISSING RESULT (cs='$cs' native='$nat')"; FAIL=1; continue
  fi
  ratio=$(awk -v a="$cs" -v b="$nat" 'BEGIN{printf "%.2f", a/b}')
  ok=$(awk -v r="$ratio" -v f="$floor" 'BEGIN{print (r>=f) ? "" : "  << BELOW FLOOR"}')
  [ -n "$ok" ] && FAIL=1
  printf "%-13s %13.0f %15.0f %7s %7s%s\n" "$phase" "$cs" "$nat" "$ratio" "$floor" "$ok"
done

if [ "$FAIL" -ne 0 ]; then
  echo; echo "BENCH GUARD FAILED"; exit 1
fi
echo; echo "BENCH GUARD PASSED"
