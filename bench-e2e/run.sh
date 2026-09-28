#!/usr/bin/env bash
# Alternates the three builds; records wall-clock, TUnit-reported duration, failures and distinct test-body threads.
set -u
export TUNIT_DISABLE_HTML_REPORTER=true DOTNET_CLI_TELEMETRY_OPTOUT=1
declare -A DIRS=([main]=${MAIN:?} [final]=${PR:?} [y]=${Y:?})  # TUnit checkouts, each with benchmarks/Issue6904.Bench built in Release
RUNS=${RUNS:-7}
for scenario in "$@"; do
  for i in $(seq 1 $RUNS); do
    for build in main final y; do
      dir=${DIRS[$build]}/benchmarks/Issue6904.Bench/bin/Release/net10.0
      tf=$(mktemp); export ISSUE6904_THREADS_FILE=$tf
      start=$(date +%s%N)
      out=$(cd $dir && timeout 300 ./Issue6904.Bench --treenode-filter "/*/*/$scenario/*" --no-progress 2>&1)
      end=$(date +%s%N)
      dur=$(grep -oE "duration: [0-9sm ]+ms" <<<"$out" | tail -1 | python3 -c "import sys,re; t=sys.stdin.read(); m=re.findall(r'(\d+)(m|s|ms)\b', t); print(sum(int(v)*{'m':60000,'s':1000,'ms':1}[u] for v,u in m))")
      failed=$(grep -oE "failed: [0-9]+" <<<"$out" | grep -oE "[0-9]+")
      threads=$(grep -oE "threads=[0-9]+" $tf | head -1 | cut -d= -f2); rm -f $tf
      echo "$scenario $build run=$i wall=$(( (end-start)/1000000 )) tunit=$dur failed=$failed threads=${threads:-na}"
    done
  done
done
