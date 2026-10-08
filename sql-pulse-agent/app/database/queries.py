"""Pre-defined read-only monitoring queries for SQL Server."""

# Simple connectivity check
CONNECTIVITY_CHECK = "SELECT 1 AS connected"

# Server version info
SERVER_VERSION = "SELECT @@VERSION AS version"

# Current database name
CURRENT_DATABASE = "SELECT DB_NAME() AS database_name"

# Discover accessible user databases (excludes system DBs)
DISCOVER_DATABASES = """
SELECT
    d.name,
    d.state_desc AS status
FROM sys.databases d
WHERE d.database_id > 4
    AND d.state_desc = 'ONLINE'
    AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name;
"""
