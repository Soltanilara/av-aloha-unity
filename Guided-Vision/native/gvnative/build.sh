#!/usr/bin/env bash
# Builds libgvnative.so straight into Assets/Plugins/Android/libs/arm64-v8a/.
# Unity infers the ABI from that directory name, so no .meta fiddling is needed.
#
# Uses the NDK that ships with the Unity Android module -- deliberately, so the
# toolchain matches whatever the Editor builds the rest of the app with.
set -euo pipefail

UNITY_VERSION="${UNITY_VERSION:-6000.5.9f1}"
NDK="${ANDROID_NDK_ROOT:-/Applications/Unity/Hub/Editor/${UNITY_VERSION}/PlaybackEngines/AndroidPlayer/NDK}"
API="${API:-29}"          # matches PlayerSettings minSdkVersion

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/../../Assets/Plugins/Android/libs/arm64-v8a"

HOST="$(ls -d "$NDK"/toolchains/llvm/prebuilt/* | head -1)"
CLANG="$HOST/bin/clang"
[ -x "$CLANG" ] || { echo "no clang at $CLANG" >&2; exit 1; }

mkdir -p "$OUT"
"$CLANG" \
  --target=aarch64-linux-android$API \
  --sysroot="$HOST/sysroot" \
  -shared -fPIC -O2 -fvisibility=hidden \
  -Wall -Wextra -Werror \
  -o "$OUT/libgvnative.so" \
  "$HERE/gvnative.c" \
  -llog -landroid -lGLESv3

echo "built $OUT/libgvnative.so"
"$HOST/bin/llvm-nm" -D --defined-only "$OUT/libgvnative.so" | grep -E "Java_|JNI_OnLoad" || true
ls -la "$OUT/libgvnative.so"
