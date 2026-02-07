#!/bin/bash
set -e

echo "=== Deployment started at $(date) ==="

cd /home/mohammed/compose-stack

# Pull latest code
echo "Pulling latest code..."
git pull origin main

# Rebuild and restart only the API
echo "Rebuilding API..."
docker compose build api

echo "Restarting API..."
docker compose up -d api

echo "=== Deployment finished at $(date) ==="
echo "API status:"
docker compose ps api
