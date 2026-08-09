#!/usr/bin/env bash
# Start the on-device LLM server that powers the live AI summary (SPEC-10).
#
# The native MLX-Swift path can't build its Metal library from a bare `swift build` on this
# toolchain, so the summary LLM runs here — a local mlx_lm.server the app talks to over
# http://127.0.0.1:8765 (localhost only; nothing leaves the machine). Idempotent: a no-op if
# the server is already up. First run downloads the ~0.8 GB 1B model to the Hugging Face cache.
set -euo pipefail

PORT="${SUMMARY_PORT:-8765}"
MODEL="${SUMMARY_MODEL:-mlx-community/Llama-3.2-1B-Instruct-4bit}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"                 # repo root
VENV="${SUMMARY_VENV:-$ROOT/spike/llm-summary-spike/.venv}"  # reuse the spike venv
LOG="${SUMMARY_LOG:-$ROOT/spike/llm-summary-spike/server.log}"

# Already running? (mlx_lm.server answers /v1/models with 200.)
if curl -s -m 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/v1/models" 2>/dev/null | grep -q 200; then
  echo "✅ summary server already running on :$PORT"
  exit 0
fi

if [ ! -x "$VENV/bin/python" ]; then
  echo "⚠ no venv at $VENV"
  echo "  create it once:"
  echo "    python3 -m venv \"$VENV\" && \"$VENV/bin/pip\" install mlx-lm"
  exit 1
fi

echo "▶ starting summary server on :$PORT (model: $MODEL)…"
echo "  (first run downloads ~0.8 GB to the Hugging Face cache)"
nohup "$VENV/bin/mlx_lm.server" --model "$MODEL" --port "$PORT" >"$LOG" 2>&1 &
echo $! > "$ROOT/spike/llm-summary-spike/server.pid"

# Wait for it to answer (model load can take a bit).
for _ in $(seq 1 60); do
  if curl -s -m 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/v1/models" 2>/dev/null | grep -q 200; then
    echo "✅ summary server ready on :$PORT  (log: $LOG)"
    exit 0
  fi
  sleep 1
done
echo "⚠ server did not answer in time — check $LOG"
exit 1
