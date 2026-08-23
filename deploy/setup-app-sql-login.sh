#!/usr/bin/env bash
set -euo pipefail

# Uygulamanin SQL Server login'ini kurar/onarir (denetim O-11 Faz 3a).
#
# Uygulama `sa` ile baglaniyordu. `sa` sysadmin oldugu icin konteynerde kod
# calistiran biri instance'in TAMAMINI devralabiliyordu: login yaratma,
# xp_cmdshell'i acip sqlserver konteynerinde komut calistirma, istedigi yere
# `BACKUP DATABASE` ile dokum alma, instance'i dusurme. Bu login yalnizca
# OrderDeckLicense uzerinde `db_owner`; sunucu duzeyinde hicbir yetkisi yok.
#
# `db_owner` bilerek secildi (db_datareader/writer degil): uygulama acilista
# `Database.Migrate()` ve Hangfire `PrepareSchemaIfNecessary` calistiriyor,
# ikisi de DDL istiyor. Bunlari deploy zamanina tasiyip runtime'i DDL'siz
# birakmak (raporun onerdigi iki-kimlik kurgusu) her migration'i bir onceki
# uygulama surumuyle geriye donuk uyumlu olmaya mecbur birakirdi; kazanci
# "uygulama kendi tablosunu dusuremesin" ile sinirliyken bedeli her PR'a
# yayiliyordu. Bilincli olarak yapilmadi.
#
# Script IDEMPOTENT. Iki durumda calistirilir:
#   1. Ilk kurulum.
#   2. FELAKET KURTARMA — `.bak` restore'undan SONRA. Restore veritabani
#      KULLANICISINI getirir ama LOGIN'i getirmez (login `master`'da yasar).
#      Kullanici yetim kalir, SID'i yeni instance'ta hicbir login'e uymaz ve
#      uygulama "Login failed" ile acilmaz. Asagidaki ALTER USER ... WITH LOGIN
#      esleme kirigini onarir. Bkz. HA-PLAYBOOK.md cutover adim 1b.

ENV_FILE=/opt/orderdeck/.env
SQL_CONTAINER=orderdeck-sqlserver
DB=OrderDeckLicense
LOGIN=orderdeck_app

[ -f "$ENV_FILE" ] || { echo "HATA: $ENV_FILE yok" >&2; exit 1; }

SA_PWD=$(grep '^SQL_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)
[ -n "$SA_PWD" ] || { echo "HATA: .env'de SQL_PASSWORD yok" >&2; exit 1; }

# Parola varsa KORUNUR. Kurtarmada yeniden uretmek, calisan compose'daki
# baglanti dizesini de guncellemeyi gerektirirdi — bir adim daha, bir hata daha.
if grep -q '^APP_SQL_PASSWORD=' "$ENV_FILE"; then
    APP_PWD=$(grep '^APP_SQL_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)
    echo "APP_SQL_PASSWORD .env'de mevcut, korunuyor."
else
    # Yalniz alfanumerik: deger hem baglanti dizesine hem compose degisken
    # ikamesine giriyor; ';' '$' '"' gibi karakterler sessizce bozardi.
    APP_PWD=$(openssl rand -base64 96 | tr -dc 'A-Za-z0-9' | head -c 40)
    [ ${#APP_PWD} -eq 40 ] || { echo "HATA: parola uretilemedi" >&2; exit 1; }
    printf 'APP_SQL_PASSWORD=%s\n' "$APP_PWD" >> "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo "APP_SQL_PASSWORD uretildi ve .env'e yazildi (${#APP_PWD} karakter)."
fi

sa_sql() {
    docker exec -i "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$SA_PWD" -C -No -b "$@"
}

echo "Login + kullanici + rol uyelugu uygulaniyor..."
sa_sql -Q "
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = '${LOGIN}')
    CREATE LOGIN [${LOGIN}] WITH PASSWORD = '${APP_PWD}', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [${LOGIN}] WITH PASSWORD = '${APP_PWD}';

USE [${DB}];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '${LOGIN}')
    CREATE USER [${LOGIN}] FOR LOGIN [${LOGIN}] WITH DEFAULT_SCHEMA = dbo;
ELSE
    -- Yetim kullanici onarimi (restore sonrasi SID uyusmazligi).
    ALTER USER [${LOGIN}] WITH LOGIN = [${LOGIN}], DEFAULT_SCHEMA = dbo;

IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members m
    JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
    JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
    WHERE r.name = 'db_owner' AND u.name = '${LOGIN}')
    ALTER ROLE db_owner ADD MEMBER [${LOGIN}];
"

# Dogrulama, YENI login'le. Yetkiyi verdigini varsaymak yetmez: yetim kullanici
# durumunda CREATE/ALTER hatasiz gecer ama baglanti yine de reddedilir.
echo "Dogrulaniyor..."
app_sql() {
    docker exec -i "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U "$LOGIN" -P "$APP_PWD" -d "$DB" -C -No -b -h -1 -W "$@"
}

app_sql -Q "SELECT TOP 1 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;" >/dev/null
app_sql -Q "CREATE TABLE dbo.__perm_probe(id int); DROP TABLE dbo.__perm_probe;" >/dev/null
app_sql -Q "CREATE TABLE HangFire.__perm_probe(id int); DROP TABLE HangFire.__perm_probe;" >/dev/null

SYSADMIN=$(app_sql -Q "SELECT CAST(ISNULL(IS_SRVROLEMEMBER('sysadmin'),-1) AS varchar);" | head -1 | tr -d '[:space:]')
[ "$SYSADMIN" = "0" ] || { echo "HATA: login sysadmin cikti ($SYSADMIN) — isin amaci bu degildi" >&2; exit 1; }

echo "OK: ${LOGIN} baglanabiliyor, dbo + HangFire semalarinda DDL yapabiliyor, sysadmin DEGIL."
echo
echo "Sonraki adim: /opt/orderdeck/docker-compose.yml zaten APP_SQL_PASSWORD"
echo "kullaniyorsa  docker compose up -d license-server  yeterli."
