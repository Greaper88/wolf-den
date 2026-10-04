#!/usr/bin/env bash

set -e

SOCKET=${WOLF_SOCKET_PATH:-/var/run/wolf/wolf.sock}
TIMEOUT=${WOLF_SOCKET_TIMEOUT:-30}
INTERVAL=1

echo -n "Waiting for Wolf socket: $SOCKET "

elapsed=0
while [ ! -S "$SOCKET" ]; do
    if [ $elapsed -ge $TIMEOUT ]; then
        echo ""
        echo "Wolf socket did not appear within ${TIMEOUT}s. Starting Den with saved app defaults available."
        break
    fi
    echo -n "."
    sleep $INTERVAL
    elapsed=$((elapsed + INTERVAL))
done

# Test if socket is actually working with netcat
if [ -S "$SOCKET" ] && timeout 2 nc -U "$SOCKET" -z 2>/dev/null; then
    echo "Socket is available and responsive"
else
    echo "Wolf API is not ready. Profile operations will be available when Wolf starts."
fi
