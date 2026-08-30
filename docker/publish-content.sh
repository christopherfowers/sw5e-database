#!/bin/sh
#
# Publishes the canonical content and JSON schemas baked into this image into a
# shared volume, so the API container can read them from disk.
#
# The copy is verified rather than assumed. A partial or truncated publish would
# leave the API serving an incomplete catalogue while looking healthy, so every
# file is compared by SHA-256 against the source tree both after staging and
# after it lands in the target. Anything short of a byte-exact match exits
# non-zero and gates the rest of the stack.

set -eu

CONTENT_SOURCE="${SW5E_CONTENT_SOURCE:-/opt/sw5e/content}"
SCHEMA_SOURCE="${SW5E_SCHEMA_SOURCE:-/opt/sw5e/schemas}"
CONTENT_TARGET="${SW5E_CONTENT_TARGET:-/srv/content}"
SCHEMA_TARGET="${SW5E_SCHEMA_TARGET:-/srv/schemas}"

STAGING_NAME='.sw5e-publish.tmp'

log() {
    echo "sw5e-publish: $*"
}

fail() {
    echo "sw5e-publish: ERROR: $*" >&2
    exit 1
}

# A sorted "<sha256>  <relative path>" listing of a tree. Comparing manifests
# rather than file counts catches truncated and corrupted files, not only
# missing ones.
manifest() {
    (
        cd "$1" || exit 1
        find . -type f | LC_ALL=C sort | while IFS= read -r file; do
            sha256sum "$file"
        done
    )
}

publish() {
    source_dir="$1"
    target_dir="$2"

    [ -d "$source_dir" ] \
        || fail "source directory '$source_dir' is missing from the image"

    file_count=$(find "$source_dir" -type f | wc -l)
    [ "$file_count" -gt 0 ] \
        || fail "source directory '$source_dir' is empty; refusing to publish an empty catalogue"

    mkdir -p "$target_dir" \
        || fail "cannot create '$target_dir' as uid $(id -u); mount the volume so it is writable by this user"
    [ -w "$target_dir" ] \
        || fail "'$target_dir' is not writable by uid $(id -u); mount the volume so it is writable by this user"

    staging="$target_dir/$STAGING_NAME"
    rm -rf "$staging"
    mkdir "$staging" \
        || fail "cannot create a staging directory inside '$target_dir'"

    cp -a "$source_dir/." "$staging/" \
        || fail "copying '$source_dir' into staging failed"

    expected=$(manifest "$source_dir")
    staged=$(manifest "$staging")
    [ "$expected" = "$staged" ] \
        || fail "staged copy of '$source_dir' does not match the source; the volume may be full"

    # Swap the staged tree into place. Source and staging share a filesystem, so
    # each move is a rename rather than a second copy.
    find "$target_dir" -mindepth 1 -maxdepth 1 ! -name "$STAGING_NAME" -exec rm -rf {} \; \
        || fail "could not clear the previous contents of '$target_dir'"

    find "$staging" -mindepth 1 -maxdepth 1 -exec mv {} "$target_dir/" \; \
        || fail "could not move the staged files into '$target_dir'"

    rmdir "$staging" \
        || fail "staging directory '$staging' was not empty after the publish"

    published=$(manifest "$target_dir")
    [ "$expected" = "$published" ] \
        || fail "published tree at '$target_dir' does not match the source"

    log "published $file_count file(s) to $target_dir"
}

[ "$CONTENT_TARGET" != "$SCHEMA_TARGET" ] \
    || fail "SW5E_CONTENT_TARGET and SW5E_SCHEMA_TARGET must be different directories"

log "running as uid $(id -u), gid $(id -g)"

publish "$CONTENT_SOURCE" "$CONTENT_TARGET"
publish "$SCHEMA_SOURCE" "$SCHEMA_TARGET"

log "done"
