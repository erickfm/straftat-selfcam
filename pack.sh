#!/usr/bin/env bash
# Build the mod and assemble a Thunderstore-style package zip that r2modman can import
# as a local mod — i.e. exactly how an end-user would install it.
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' QuarterViewSelfCam/QuarterViewSelfCam.csproj)

echo ">> building..."
dotnet build -c Release QuarterViewSelfCam/QuarterViewSelfCam.csproj >/dev/null

echo ">> assembling package..."
cp QuarterViewSelfCam/bin/Release/SelfCam.dll package/SelfCam.dll

mkdir -p dist
OUT="dist/SelfCam-$VERSION.zip"
rm -f "$OUT"
python3 - "$OUT" <<'PY'
import sys, zipfile, os
out = sys.argv[1]
files = ["manifest.json", "icon.png", "README.md", "SelfCam.dll"]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(os.path.join("package", f), f)  # store at zip root
print("wrote", out)
PY

echo ">> done: $OUT  (version $VERSION)"
