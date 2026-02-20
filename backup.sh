#!/bin/bash
set -e

# Config
BACKUP_DIR="/home/mohammed/backups"
DATE=$(date +%Y-%m-%d_%H-%M)
BACKUP_PATH="$BACKUP_DIR/$DATE"

echo "=== Backup started at $(date) ==="
mkdir -p "$BACKUP_PATH"

# 1. Backup SQL Server database (using docker exec + sqlcmd)
echo "[1/5] Backing up SQL Server..."
docker exec compose-stack-sqlserver-1 /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "SqlServer2026!" -C \
  -Q "BACKUP DATABASE [ProjectsDb] TO DISK = '/var/opt/mssql/backup_ProjectsDb.bak' WITH INIT"
docker cp compose-stack-sqlserver-1:/var/opt/mssql/backup_ProjectsDb.bak "$BACKUP_PATH/ProjectsDb.bak"
echo "  SQL Server backup: OK"

# 2. Backup Docker volumes (tar compress)
echo "[2/5] Backing up Docker volumes..."
for VOLUME in esdata minio_data redis_data rabbitmq_data grafana_data prometheus_data; do
  docker run --rm -v compose-stack_${VOLUME}:/data -v "$BACKUP_PATH":/backup \
    alpine tar czf /backup/${VOLUME}.tar.gz -C /data .
  echo "  $VOLUME: OK"
done

# 3. Backup config files
echo "[3/5] Backing up config files..."
tar czf "$BACKUP_PATH/configs.tar.gz" \
  -C /home/mohammed/compose-stack \
  docker-compose.yml nginx.conf prometheus.yml deploy.sh \
  api/Program.cs api/Dockerfile \
  frontend/src/ frontend/index.html frontend/package.json

echo "  Configs: OK"

# 4. Calculate total size
echo "[4/5] Backup summary:"
du -sh "$BACKUP_PATH"/*
TOTAL=$(du -sh "$BACKUP_PATH" | cut -f1)
echo "  Total size: $TOTAL"

# 5. Delete backups older than 7 days
echo "[5/5] Cleaning old backups..."
find "$BACKUP_DIR" -maxdepth 1 -type d -mtime +7 -exec rm -rf {} \;
echo "  Old backups cleaned"

echo "=== Backup completed at $(date) ==="
echo "Location: $BACKUP_PATH"
