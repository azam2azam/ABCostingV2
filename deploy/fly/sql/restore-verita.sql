RESTORE DATABASE Verita
FROM DISK = '/var/opt/mssql/backup/Verita.bak'
WITH REPLACE,
MOVE 'Verita' TO '/var/opt/mssql/data/Verita.mdf',
MOVE 'Verita_log' TO '/var/opt/mssql/data/Verita_log.ldf';
