#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "Please run: sudo bash scripts/install-dev-env.sh" >&2
  exit 1
fi

TARGET_USER="${SUDO_USER:-pcl7986}"
TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
if [[ -z "$TARGET_HOME" ]]; then
  echo "Cannot resolve home for user: $TARGET_USER" >&2
  exit 2
fi

. /etc/os-release
if [[ "${ID:-}" != "ubuntu" || "${VERSION_ID:-}" != "24.04" ]]; then
  echo "This installer is validated for Ubuntu 24.04; detected ${PRETTY_NAME:-unknown}." >&2
  exit 3
fi

ARCH="$(dpkg --print-architecture)"
echo "[1/6] Updating apt metadata..."
apt-get update

echo "[2/6] Installing base dependencies and .NET 8 SDK..."
apt-get install -y ca-certificates curl gnupg dotnet-sdk-8.0

echo "[3/6] Configuring Docker official apt repository..."
install -m 0755 -d /etc/apt/keyrings
/usr/bin/curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
cat > /etc/apt/sources.list.d/docker.sources <<DOCKER_REPO
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: ${UBUNTU_CODENAME:-$VERSION_CODENAME}
Components: stable
Architectures: ${ARCH}
Signed-By: /etc/apt/keyrings/docker.asc
DOCKER_REPO
apt-get update

echo "[4/6] Installing Docker Engine + Buildx + Compose plugin..."
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker

echo "[5/6] Granting ${TARGET_USER} access to Docker..."
getent group docker >/dev/null || groupadd docker
usermod -aG docker "$TARGET_USER"

echo "[6/6] Verifying installed tools..."
dotnet --info | sed -n '1,35p'
docker --version
docker compose version
docker info >/dev/null

echo
echo "Environment installation completed."
echo "IMPORTANT: ${TARGET_USER} must log out/in once (or reboot) before running docker without sudo."
echo "After re-login, verify with: docker run --rm hello-world"
