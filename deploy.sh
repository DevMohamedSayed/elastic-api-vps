#!/bin/bash
set -e

echo "=== Deployment started at $(date) ==="

cd /home/mohammed/compose-stack

echo "Pulling latest code..."
git pull origin main

echo "Rebuilding API..."
docker compose build api

echo "Restarting API..."
docker compose up -d api

echo "=== Deployment finished at $(date) ==="
docker compose ps api
