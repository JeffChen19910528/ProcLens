#!/bin/sh
# Installs or removes ProcLens. Run it from the extracted package.
#
#   ./install.sh                      per-user install under ~/.local (or /usr/local when run as root)
#   ./install.sh --prefix /opt/proclens
#   ./install.sh --uninstall
#
# It installs only the files it ships (bin/proclens and share/doc/proclens) and never touches your audit log or
# your policy file. It does not change your shell configuration; it tells you what to add to PATH.

set -eu

usage() {
    cat <<'EOF'
Usage: install.sh [--prefix DIR] [--uninstall]

  --prefix DIR   install under DIR (binary in DIR/bin, documentation in DIR/share/doc/proclens)
                 default: /usr/local when run as root, otherwise $HOME/.local
  --uninstall    remove what this script installed
  -h, --help     show this help
EOF
}

die() { echo "install.sh: $*" >&2; exit 1; }

prefix=""
action="install"
while [ $# -gt 0 ]; do
    case "$1" in
        --prefix) [ $# -ge 2 ] || { usage >&2; exit 2; }; prefix="$2"; shift 2 ;;
        --uninstall) action="uninstall"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "install.sh: unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [ -z "$prefix" ]; then
    if [ "$(id -u)" -eq 0 ]; then prefix="/usr/local"; else prefix="${HOME:?HOME is not set}/.local"; fi
fi

here=$(cd "$(dirname "$0")" && pwd)
bin_dir="$prefix/bin"
doc_dir="$prefix/share/doc/proclens"

if [ "$action" = "uninstall" ]; then
    rm -f "$bin_dir/proclens"
    if [ -d "$doc_dir" ]; then
        rm -f "$doc_dir"/*.md "$doc_dir/README.txt"
        rmdir "$doc_dir" 2>/dev/null || true
    fi
    echo "ProcLens was removed from $prefix."
    echo "Your audit log and policy were NOT touched (audit log: ${XDG_DATA_HOME:-$HOME/.local/share}/ProcLens)."
    exit 0
fi

[ -f "$here/proclens" ] || die "proclens was not found next to this script. Run install.sh from the extracted package."

mkdir -p "$bin_dir" "$doc_dir"
# 'install' replaces the file instead of writing into it, so a running copy does not cause "text file busy".
install -m 0755 "$here/proclens" "$bin_dir/proclens"
if [ -d "$here/docs" ]; then cp "$here"/docs/*.md "$doc_dir/"; fi
if [ -f "$here/README.txt" ]; then cp "$here/README.txt" "$doc_dir/"; fi

version=$("$bin_dir/proclens" --version) || die "the installed proclens did not start."
echo "Installed ProcLens $version to $bin_dir/proclens"

case ":$PATH:" in
    *":$bin_dir:"*) ;;
    *) echo "$bin_dir is not on your PATH. Add this to your shell profile:"
       echo "    export PATH=\"$bin_dir:\$PATH\"" ;;
esac

echo "Next: proclens system capabilities   (documentation: $doc_dir/CLI_USAGE.md)"
echo "Verify the archive against SHA256SUMS.txt before first use."
