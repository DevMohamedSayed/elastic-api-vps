#!/bin/bash
set -e
echo "=== Zero-Downtime Deployment started at $(date) ==="

cd /home/mohammed/compose-stack

# Step 1: Pull latest code
echo "[1/5] Pulling latest code..."
git pull origin main

# Step 2: Build frontend
echo "[2/5] Building frontend..."
cd /home/mohammed/compose-stack/frontend
npm install
npm run build

# Step 3: Build NEW API image (old container still running and serving traffic!)
echo "[3/5] Building new API image..."
cd /home/mohammed/compose-stack
docker compose build api

# Step 4: Replace old container with new one
echo "[4/5] Starting new API container..."
docker compose up -d api

# Step 5: Wait for new container to be healthy
echo "[5/5] Waiting for API to be healthy..."
MAX_WAIT=60
WAITED=0
while [ $WAITED -lt $MAX_WAIT ]; do
    STATUS=$(docker inspect --format='{{.State.Health.Status}}' compose-stack-api-1 2>/dev/null || echo "unknown")
    if [ "$STATUS" = "healthy" ]; then
        echo "API is healthy after ${WAITED}s!"
        break
    fi
    echo "  Status: $STATUS (waited ${WAITED}s / ${MAX_WAIT}s)"
    sleep 3
    WAITED=$((WAITED + 3))
done

if [ "$STATUS" != "healthy" ]; then
    echo "ERROR: API did not become healthy in ${MAX_WAIT}s!"
    echo "Check logs: docker compose logs api --tail 50"
    exit 1
fi

# Graceful reload (keeps existing connections alive, no dropped requests)
docker compose exec nginx nginx -s reload

echo "=== Deployment finished successfully at $(date) ==="
docker compose ps api
