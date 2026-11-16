#!/usr/bin/env bash
# v0.4.0 resource migration for dsh-remote-gateway.
#
# Old layout (v0.3.0):
#   /opt/dsh-remote/apk/                     all APKs of every app (DSH Remote + SmartBill)
#   /opt/dsh-remote/resources/{relay,docs,image,misc}/<kind files>
#
# New layout (v0.4.0):
#   /opt/dsh-remote/resources/<id>/<file>    id in {dsh-remote, smartbill, relay, docs}
#
# Rules:
#   - dshremote-*.apk -> resources/dsh-remote/, newest 3 by mtime stay, the rest
#     move to resources/_archive/dsh-remote/ (archived, NOT deleted, not served)
#   - smartbill-*.apk -> resources/smartbill/ (currently exactly 3 files)
#   - resources/relay and resources/docs stay where they are (they are ids now)
#   - resources/image and resources/misc are empty legacy dirs; removed
#   - /opt/dsh-remote/apk stays as an empty compatibility directory
#   - nothing is deleted: every moved file is reachable in _archive if pruned
#
# Run as root on the VPS. Idempotent: re-running finds nothing to move.
set -euo pipefail

ROOT=/opt/dsh-remote
RES=$ROOT/resources
APK=$ROOT/apk
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
KEEP=3

say() { printf '%s\n' "$*"; }

say "== v0.4.0 migration ($STAMP)"
say "root: $ROOT"

mkdir -p "$RES/dsh-remote" "$RES/smartbill" "$RES/_archive/dsh-remote"

# 1. Record what existed before touching anything.
if compgen -G "$APK/*.apk" > /dev/null; then
  ( cd "$APK" && sha256sum *.apk ) > "/root/dsh-apk-manifest-$STAMP.txt"
  say "manifest: /root/dsh-apk-manifest-$STAMP.txt ($(wc -l < "/root/dsh-apk-manifest-$STAMP.txt") files)"
fi

# 2. smartbill APKs -> resources/smartbill/ (keep all current versions; the next
#    keepVersions upload prunes to the policy).
if compgen -G "$APK/smartbill-*.apk" > /dev/null; then
  mv -n "$APK"/smartbill-*.apk "$RES/smartbill/"
  say "smartbill -> resources/smartbill/"
fi

# 3. dshremote APKs -> resources/dsh-remote/, newest $KEEP by mtime stay.
if compgen -G "$APK/dshremote-*.apk" > /dev/null; then
  mapfile -t KEEP_FILES < <( cd "$APK" && ls -t dshremote-*.apk | head -n "$KEEP" )
  for file in "${KEEP_FILES[@]}"; do
    mv -n "$APK/$file" "$RES/dsh-remote/"
    say "keep     $file"
  done
  for path in "$APK"/dshremote-*.apk; do
    [ -e "$path" ] || continue
    mv -n "$path" "$RES/_archive/dsh-remote/"
    say "archived $(basename "$path")"
  done
fi

# 4. Empty legacy kind dirs are not categories any more.
for legacy in image misc; do
  if [ -d "$RES/$legacy" ] && [ -z "$(ls -A "$RES/$legacy")" ]; then
    rmdir "$RES/$legacy"
    say "removed empty legacy dir resources/$legacy"
  fi
done

# 5. Permissions: regular files 644, dirs 755 (service runs as root).
find "$RES/dsh-remote" "$RES/smartbill" "$RES/relay" "$RES/docs" "$RES/_archive" -type f -exec chmod 644 {} +
find "$RES" -mindepth 1 -maxdepth 2 -type d -exec chmod 755 {} +

# 6. Report the final layout (secret-free).
say '--- final layout ---'
for dir in dsh-remote smartbill relay docs; do
  count=$(find "$RES/$dir" -maxdepth 1 -type f 2>/dev/null | wc -l)
  say "$(printf '%-14s %s file(s)' "$dir" "$count")"
  find "$RES/$dir" -maxdepth 1 -type f -printf '    %f (%s bytes, %TY-%Tm-%Td %TH:%TM)\n' 2>/dev/null | sort
done
say "archived dshremote APKs: $(find "$RES/_archive/dsh-remote" -maxdepth 1 -type f | wc -l)"
say "legacy APK_DIR now: $(find "$APK" -maxdepth 1 -type f | wc -l) file(s)"
say 'done'
