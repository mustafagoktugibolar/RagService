#!/usr/bin/env bash
# Downloads the local cross-encoder used by the ONNX reranker. Setup-time only: runtime inference needs no network.
# Model:    cross-encoder/ms-marco-MiniLM-L6-v2 (Apache-2.0 model card; trained on MS MARCO, see docs/adr/0003)
# Revision and SHA-256 are pinned: a mismatch aborts, so a changed upstream file cannot slip in silently.
set -euo pipefail

REPO="cross-encoder/ms-marco-MiniLM-L6-v2"
REVISION="233902d25c440f23af6f7d6e94d2946bac0bee0a"
DEST="${1:-models/ms-marco-MiniLM-L6-v2}"
BASE="https://huggingface.co/${REPO}/resolve/${REVISION}"

# path-in-repo | expected sha256 (empty = small text file, pinned by revision only)
FILES=(
  "onnx/model.onnx|5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a"
  "onnx/model_qint8_arm64.onnx|3573b6b9593cb2f75987a31815d409ca3dd8808629118fd20451bb1a5d90cec7"
  "vocab.txt|"
  "tokenizer_config.json|"
  "config.json|"
)

mkdir -p "$DEST"
for entry in "${FILES[@]}"; do
  path="${entry%%|*}"; sha="${entry##*|}"
  out="$DEST/$(basename "$path")"
  if [[ ! -f "$out" ]]; then
    echo "downloading $path"
    curl -fL --retry 3 -o "$out.part" "$BASE/$path"
    mv "$out.part" "$out"
  fi
  if [[ -n "$sha" ]]; then
    actual="$(shasum -a 256 "$out" | cut -d' ' -f1)"
    if [[ "$actual" != "$sha" ]]; then
      echo "SHA-256 mismatch for $out (expected $sha, got $actual)" >&2
      rm -f "$out"
      exit 1
    fi
  fi
done
echo "model ready in $DEST"
