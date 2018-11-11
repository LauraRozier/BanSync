**Ban Sync** provides a way for hosters to synchronize bans between servers.

## Features

- Syncronizing bans between two or more servers
- Additional assurance that bans stay even after server wipes

## Configuration

- **Data Store Type : 0 (SQLite) or 1 (MySQL)** -- Which backend to use for storage
- **SQLite - Database Name** -- The SQLite database file
- **MySQL - Host** -- The MySQL database server address
- **MySQL - Port** -- The MySQL database server port
- **MySQL - Database Name** -- The MySQL database name
- **MySQL - Username** -- The MySQL database user
- **MySQL - Password** -- The MySQL database password

```json
{
  "Data Store Type : 0 (SQLite) or 1 (MySQL)": 0,
  "SQLite - Database Name": "BanSync.db",
  "MySQL - Host": "localhost",
  "MySQL - Port": 3306,
  "MySQL - Database Name": "BanSync",
  "MySQL - Username": "root",
  "MySQL - Password": "password"
}
```

## Credits

- sqroot -- For creating the original Python plugin
