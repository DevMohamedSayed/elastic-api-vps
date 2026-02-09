#!/bin/bash
set -e
echo "=== Deployment started at $(date) ==="

cd /home/mohammed/compose-stack

echo "Pulling latest code..."
git pull origin main

echo "Building frontend..."
cd /home/mohammed/compose-stack/frontend
npm install
npm run build

echo "Rebuilding API..."
cd /home/mohammed/compose-stack
docker compose build api

echo "Restarting services..."
docker compose up -d api
docker compose restart nginx

echo "=== Deployment finished at $(date) ==="
docker compose ps api
docker compose ps nginx

