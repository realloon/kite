#!/bin/sh
set -e

OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
  Darwin)
    [ "$ARCH" = "arm64" ] && ASSET="kite-osx-arm64" || { echo "Unsupported architecture: $ARCH (macOS requires Apple Silicon)"; exit 1; }
    ;;
  Linux)
    [ "$ARCH" = "x86_64" ] && ASSET="kite-linux-x64" || { echo "Unsupported architecture: $ARCH (Linux requires x86_64)"; exit 1; }
    ;;
  *)
    echo "Unsupported OS: $OS"
    exit 1
    ;;
esac

INSTALL_DIR="${KITE_INSTALL_DIR:-$HOME/.local/bin}"
mkdir -p "$INSTALL_DIR"

URL="https://github.com/realloon/kite/releases/latest/download/$ASSET"
echo "Downloading kite from $URL..."
curl -fsSL "$URL" -o "$INSTALL_DIR/kite"
chmod +x "$INSTALL_DIR/kite"

echo "kite installed to $INSTALL_DIR/kite"
case ":$PATH:" in
  *:"$INSTALL_DIR":*) ;;
  *)
    PROFILE="$HOME/.profile"
    case "$SHELL" in
      */zsh) PROFILE="$HOME/.zshrc" ;;
      */bash) [ -f "$HOME/.bashrc" ] && PROFILE="$HOME/.bashrc" || PROFILE="$HOME/.bash_profile" ;;
    esac
    grep -qs "$INSTALL_DIR" "$PROFILE" 2>/dev/null || echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$PROFILE"
    if [ -t 1 ] && [ -r /dev/tty ]; then
      echo "Refreshing shell..."
      exec "${SHELL:-/bin/sh}" < /dev/tty
    else
      echo "Note: Add $INSTALL_DIR to your PATH or run: source $PROFILE"
    fi
    ;;
esac
