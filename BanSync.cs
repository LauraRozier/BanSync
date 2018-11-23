/* --- Contributor information ---
 * Please follow the following set of guidelines when working on this plugin,
 * this to help others understand this file more easily.
 *
 * -- Authors --
 * Thimo (ThibmoRozier) <thibmorozier@live.nl>
 * sqroot <no@email.com>
 *
 * -- Naming --
 * Avoid using non-alphabetic characters, eg: _
 * Avoid using numbers in method and class names (Upgrade methods are allowed to have these, for readability)
 * Private constants -------------------- SHOULD start with a uppercase "C" (PascalCase)
 * Private readonly fields -------------- SHOULD start with a uppercase "C" (PascalCase)
 * Private fields ----------------------- SHOULD start with a uppercase "F" (PascalCase)
 * Arguments/Parameters ----------------- SHOULD start with a lowercase "a" (camelCase)
 * Classes ------------------------------ SHOULD start with a uppercase character (PascalCase)
 * Methods ------------------------------ SHOULD start with a uppercase character (PascalCase)
 * Public properties (constants/fields) - SHOULD start with a uppercase character (PascalCase)
 * Variables ---------------------------- SHOULD start with a lowercase character (camelCase)
 *
 * -- Style --
 * Single-line comments - // Single-line comment
 * Multi-line comments -- Just like this comment block!
 */
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("BanSync", "ThibmoRozier", "2.0.4")]
    [Description("Synchronizes bans across servers.")]
    public class BanSync : CovalencePlugin
    {
        #region Types
        /// <summary>
        /// Data storage type
        /// </summary>
        private enum DataStoreType
        {
            SQLite,
            MySql
        }

        /// <summary>
        /// The config type class
        /// </summary>
        private class ConfigData
        {
            [JsonProperty("Data Store Type : 0 (SQLite) or 1 (MySQL)")]
            public DataStoreType DataStoreType { get; set; } = DataStoreType.SQLite;
            [JsonProperty("SQLite - Database Name")]
            public string SQLiteDb { get; set; } = "BanSync.db";
            [JsonProperty("MySQL - Host")]
            public string MySQLHost { get; set; } = "localhost";
            [JsonProperty("MySQL - Port")]
            public int MySQLPort { get; set; } = 3306;
            [JsonProperty("MySQL - Database Name")]
            public string MySQLDb { get; set; } = "BanSync";
            [JsonProperty("MySQL - Username")]
            public string MySQLUser { get; set; } = "root";
            [JsonProperty("MySQL - Password")]
            public string MySQLPass { get; set; } = "password";
        }

        /// <summary>
        /// Banned user object class
        /// </summary>
        private class BannedPlayer
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Reason { get; set; }
        }

        /// <summary>
        /// Banned Player object comparer class
        /// </summary>
        private class CompareBannedPlayers : IEqualityComparer<BannedPlayer>
        {
            /// <summary>
            /// Determine if two objects are equal
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <returns></returns>
            public bool Equals(BannedPlayer x, BannedPlayer y)
            {
                // Check whether the objects are the same object. 
                if (ReferenceEquals(x, y))
                    return true;

                return x != null && y != null && x.Id.Equals(y.Id, StringComparison.InvariantCultureIgnoreCase) &&
                                                 x.Name.Equals(y.Name, StringComparison.InvariantCulture);
            }

            /// <summary>
            /// Retrieve the hash code for an object
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            public int GetHashCode(BannedPlayer obj)
            {
                int idHash = obj.Id.GetHashCode();
                int nameHash = obj.Name.GetHashCode();
                return idHash ^ nameHash;
            }
        }

        /// <summary>
        /// Helper class to determine which users are still banned
        /// </summary>
        private class BanDiff
        {
            public List<BannedPlayer> NewBans;
            public List<BannedPlayer> RemovedBans;
            public bool NotEmpty;

            /// <summary>
            /// Class constructor
            /// </summary>
            /// <param name="aOldList">The old banned user list</param>
            /// <param name="aNewList">The current banned user list</param>
            public BanDiff(IEnumerable<BannedPlayer> aOldList, IEnumerable<BannedPlayer> aNewList)
            {
                CompareBannedPlayers comparer = new CompareBannedPlayers();
                NewBans = aNewList.Except(aOldList, comparer).ToList();
                RemovedBans = aOldList.Except(aNewList, comparer).ToList();
                NotEmpty = NewBans.Count > 0 || RemovedBans.Count > 0;
            }
        }
        #endregion Types

        #region Utility methods
        /// <summary>
        /// Log an error message to the logfile
        /// </summary>
        /// <param name="aMessage"></param>
        private void LogError(string aMessage)
        {
            LogToFile(string.Empty, $"[{DateTime.Now.ToString("hh:mm:ss")}] ERROR > {aMessage}", this);

            if (FSqlCon != null) {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FSqlCon.Con?.Close();
                } else {
                    FSqlite.CloseDb(FSqlCon);
                }
            }

            timer.Once(0.01f, () => Interface.Oxide.UnloadPlugin("BanSync"));
        }

        /// <summary>
        /// Log an informational message to the logfile
        /// </summary>
        /// <param name="aMessage"></param>
        private void LogInfo(string aMessage) =>
            LogToFile(string.Empty, $"[{DateTime.Now.ToString("hh:mm:ss")}] INFO > {aMessage}", this);

        /// <summary>
        /// Log a debugging message to the logfile
        /// </summary>
        /// <param name="aMessage"></param>
        private void LogDebug(string aMessage)
        {
            if (CDebugEnabled)
                LogToFile(string.Empty, $"[{DateTime.Now.ToString("hh:mm:ss")}] DEBUG > {aMessage}", this);
        }

        /// <summary>
        /// Retrieve the list of banned users
        /// </summary>
        /// <returns></returns>
        private List<BannedPlayer> GetBannedUserList()
        {
            #if RUST
                IEnumerable<ServerUsers.User> users = ServerUsers.GetAll(ServerUsers.UserGroup.Banned).OrderBy(u => u.username).ThenBy(u => u.steamid);
            #else
                IEnumerable<IPlayer> users = players.All.Where(player => { return player.IsBanned; }).OrderBy(u => u.Name).ThenBy(u => u.Id);
            #endif
            LogDebug($"GetBannedUserList > Retrieved the banned user list, users.Count = {users.Count()}");
            return (
                from user in users
                select new BannedPlayer {
                    #if RUST
                        Id = user.steamid.ToString(),
                        Name = user.username,
                        Reason = user.notes
                    #else
                        Id = user.Id,
                        Name = user.Name,
                        Reason = "Synched ban"
                    #endif
                }
            ).ToList();
        }

        /// <summary>
        /// Create a new "REPLACE INTO" query for bansynch
        /// </summary>
        /// <param name="aBans">The list of bans to insert or replace</param>
        /// <param name="aSqlData">The resulting list of SQL data</param>
        /// <returns>The SQL query string</returns>
        private string CreateReplaceQuery(IEnumerable<BannedPlayer> aBans, out List<object> aSqlData)
        {
            int counter = 0;
            string sqlNewValues = string.Empty;
            aSqlData = new List<object>();

            foreach (BannedPlayer player in aBans) {
                sqlNewValues += $"(@{counter++}, @{counter++}, @{counter++}), ";
                /* 
                 * IMPORTANT:
                 *   Items must be added in the proper order, or they will get missplaced in the query
                 */
                aSqlData.Add(player.Id);
                aSqlData.Add(player.Name);
                aSqlData.Add(player.Reason);
            }

            sqlNewValues = sqlNewValues.Remove(sqlNewValues.Length - 2);
            return $"REPLACE INTO {CBanTableName} (UserId, Name, Reason) VALUES {sqlNewValues};";
        }
        #endregion Utility methods

        #region Constants
        private readonly bool CDebugEnabled = false;
        private const string CBanTableName = "userbans";
        #endregion Constants

        #region Fields
        private ConfigData FConfigData;
        private Connection FSqlCon;
        private List<BannedPlayer> FOldBans;
        private readonly Core.MySql.Libraries.MySql FMySql = Interface.Oxide.GetLibrary<Core.MySql.Libraries.MySql>();
        private readonly Core.SQLite.Libraries.SQLite FSqlite = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();
        #endregion Fields

        #region Database methods
        /// <summary>
        /// Connect to the backend database
        /// </summary>
        /// <returns>Success</returns>
        private bool ConnectToDatabase(out Connection aConn)
        {
            try {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    aConn = FMySql.OpenDb(
                        FConfigData.MySQLHost,
                        FConfigData.MySQLPort,
                        FConfigData.MySQLDb,
                        FConfigData.MySQLUser,
                        FConfigData.MySQLPass,
                        this
                    );
                    LogDebug($"ConnectToDatabase > Is aConn NULL? {(aConn == null ? "yes" : "no")}");

                    if (aConn == null || aConn.Con == null) {
                        LogError($"ConnectToDatabase > Couldn't open the MySQL Database: {aConn.Con.State.ToString()}");
                        aConn = null;
                        return false;
                    }
                } else {
                    aConn = FSqlite.OpenDb(FConfigData.SQLiteDb, this);
                    LogDebug($"ConnectToDatabase > Is aConn NULL? {(aConn == null ? "yes" : "no")}");

                    if (aConn == null) {
                        LogError("ConnectToDatabase > Couldn't open the SQLite Database.");
                        return false;
                    }
                }

                return true;
            } catch (Exception e) {
                LogError($"{e.ToString()}");
                aConn = null;
                return false;
            }
        }

        /// <summary>
        /// Initialize the plugin
        /// </summary>
        private void InitBanSync()
        {
            LogDebug(
                $"InitBanSync > Is FConfigData NULL? {(FConfigData == null ? "yes" : "no")} ; " +
                $"Is FMySql NULL? {(FMySql == null ? "yes" : "no")} ; Is FSqlite NULL? {(FSqlite == null ? "yes" : "no")}"
            );

            if (ConnectToDatabase(out FSqlCon)) {
                FOldBans = GetBannedUserList();

                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.Query(
                        new Sql(
                            "SELECT table_name FROM information_schema.tables WHERE table_schema='{FConfigData.MySQLDb}' AND table_name='{CBanTableName}';"
                        ),
                        FSqlCon,
                        CreateDb
                    );
                } else {
                    FSqlite.Query(
                        new Sql($"SELECT name FROM sqlite_master WHERE type='table' AND name='{CBanTableName}';"),
                        FSqlCon,
                        CreateDb
                    );
                }
            }
        }

        /// <summary>
        /// Create the ban table from scratch
        /// </summary>
        /// <param name="aRows">The list of tables with our table name from the same schema (database)</param>
        private void CreateDb(IEnumerable<Dictionary<string, object>> aRows)
        {
            if (aRows.Count() > 0) {
                PullBans();
            } else {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(
                        new Sql($"CREATE TABLE {CBanTableName} (UserId VARCHAR(1024) PRIMARY KEY, Name TEXT NOT NULL, Reason LONGTEXT NOT NULL);"),
                        FSqlCon
                    );
                } else {
                    FSqlite.ExecuteNonQuery(
                        new Sql($"CREATE TABLE {CBanTableName} (UserId TEXT NOT NULL PRIMARY KEY UNIQUE, Name TEXT NOT NULL, Reason TEXT NOT NULL);"),
                        FSqlCon
                    );
                }

                FillDatabase();
            }
        }

        /// <summary>
        /// Fill the database with the current server bans (Only to be called after creating the table from scratch)
        /// </summary>
        private void FillDatabase()
        {
            if (FOldBans.Count > 0) {
                List<object> sqlNewData;
                string replaceQueryStr = CreateReplaceQuery(FOldBans, out sqlNewData);
                Sql query = new Sql(replaceQueryStr, sqlNewData.ToArray());
                LogDebug($"FillDatabase > SQL = {query.SQL}");

                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(query, FSqlCon, PullBans);
                } else {
                    FSqlite.ExecuteNonQuery(query, FSqlCon, PullBans);
                }
            }
        }

        /// <summary>
        /// Update the currently active bans by adding the new bans and removing unbanned users
        /// </summary>
        /// <param name="aRows">The active ban rows from the database</param>
        private void UpdateBans(IEnumerable<Dictionary<string, object>> aRows)
        {
            BanDiff diff = new BanDiff(
                GetBannedUserList(),
                (
                    from row in aRows
                    select new BannedPlayer {
                        Id = (string)row["UserId"],
                        Name = (string)row["Name"],
                        Reason = (string)row["Reason"]
                    }
                ).ToList()
            );
            LogDebug($"UpdateBans > diff.NewBans.Count = {diff.NewBans.Count} ; diff.RemovedBans.Count = {diff.RemovedBans.Count}");

            if (diff.NotEmpty) {
                foreach (BannedPlayer ban in diff.NewBans) {
                    FOldBans.Add(ban);
                    server.Ban(ban.Id, ban.Reason);
                    IPlayer player = players.FindPlayerById(ban.Id);

                    if (player?.IsConnected ?? false) // Prevent nullref
                        player.Kick(string.Format("Banned: {0}", ban.Reason));
                }

                foreach (BannedPlayer unban in diff.RemovedBans) {
                    FOldBans.RemoveAll(ban => ban.Id == unban.Id);
                    server.Unban(unban.Id);
                }

                FOldBans = GetBannedUserList();
            }

            if (FConfigData.DataStoreType == DataStoreType.MySql) {
                FSqlCon.Con.Close();
            } else {
                FSqlite.CloseDb(FSqlCon);
            }

            FSqlCon = null;
            timer.Once(20, PushBans);
        }
        
        /// <summary>
        /// Pull the currently active bans from the database
        /// </summary>
        /// <param name="aRowsAffected">The count of rows affected by the previous SQL query</param>
        private void PullBans(int aRowsAffected = 0)
        {
            LogDebug($"PullBans > aRowsAffected = {aRowsAffected}");
            Sql query = new Sql($"SELECT UserId, Name, Reason FROM {CBanTableName}");

            if (aRowsAffected > 0)
                FOldBans = GetBannedUserList();

            if (FConfigData.DataStoreType == DataStoreType.MySql) {
                FMySql.Query(query, FSqlCon, UpdateBans);
            } else {
                FSqlite.Query(query, FSqlCon, UpdateBans);
            }
        }

        /// <summary>
        /// Push the currently active bans to the database
        /// </summary>
        private void PushBans()
        {
            if (!ConnectToDatabase(out FSqlCon)) {
                LogError("PushBans > Unable to connect to the database");
                return;
            }

            BanDiff diff = new BanDiff(FOldBans, GetBannedUserList());
            Sql query = new Sql();
            LogDebug($"PushBans > diff.NewBans.Count = {diff.NewBans.Count} ; diff.RemovedBans.Count = {diff.RemovedBans.Count}");

            if (diff.NotEmpty) {
                if (diff.NewBans.Count > 0) {
                    List<object> sqlNewData;
                    string replaceQueryStr = CreateReplaceQuery(FOldBans, out sqlNewData);
                    query.Append(replaceQueryStr, sqlNewData.ToArray());
                }

                if (diff.RemovedBans.Count > 0) {
                    string sqlRemoveValues = string.Empty;

                    for (int i = 0; i < diff.RemovedBans.Count; i++) {
                        int startIndex = 3 * i;
                        BannedPlayer unban = diff.RemovedBans[i];
                        sqlRemoveValues += $"'{unban.Id}', ";
                    }

                    sqlRemoveValues = sqlRemoveValues.Remove(sqlRemoveValues.Length - 2);
                    query.Append($"DELETE FROM {CBanTableName} WHERE UserId IN ({sqlRemoveValues});");
                }

                LogDebug($"PushBans > SQL = {query.SQL}");

                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(query, FSqlCon, PullBans);
                } else {
                    FSqlite.ExecuteNonQuery(query, FSqlCon, PullBans);
                }
            } else {
                PullBans();
            }
        }
        /// <summary>
        /// Add the new ban to the database
        /// </summary>
        /// <param name="aUserId">The player's ID</param>
        /// <param name="aName">The player's name</param>
        /// <param name="aReason">The ban reason</param>
        private void AddPlayerBan(string aUserId, string aName, string aReason)
        {
            Connection localCon;
            List<object> sqlNewData = new List<object> {
                aUserId,
                aName,
                aReason
            };
            Sql query = new Sql($"REPLACE INTO {CBanTableName} (UserId, Name, Reason) VALUES (@0, @1, @2);", sqlNewData.ToArray());
            // Do a replace, in case the ban replaces a previous one
            FOldBans.RemoveAll(ban => ban.Id == aUserId);
            FOldBans.Add(new BannedPlayer { Id = aUserId, Name = aName, Reason = aReason });

            if (ConnectToDatabase(out localCon)) {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(query, localCon);
                    localCon.Con.Close();
                } else {
                    FSqlite.ExecuteNonQuery(query, localCon);
                    FSqlite.CloseDb(localCon);
                }
            }
        }

        /// <summary>
        /// Remove the ban from the database
        /// </summary>
        /// <param name="aUserId">The player's ID</param>
        private void RemovePlayerBan(string aUserId)
        {
            Connection localCon;
            Sql query = new Sql($"DELETE FROM {CBanTableName} WHERE UserId = '{aUserId}';");
            FOldBans.RemoveAll(ban => ban.Id == aUserId);

            if (ConnectToDatabase(out localCon)) {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(query, localCon);
                    localCon.Con.Close();
                } else {
                    FSqlite.ExecuteNonQuery(query, localCon);
                    FSqlite.CloseDb(localCon);
                }
            }
        }
        #endregion Database methods

        #region Hooks
        /// <summary>
        /// HOOK: Perform the actions that should be performed when the server is initialized
        /// </summary>
        void OnServerInitialized()
        {
            LoadConfig();
            InitBanSync();
        }

        /// <summary>
        /// HOOK: Perform the actions that should be taken when the plugin is unloaded
        /// </summary>
        void Unload()
        {
            if (FConfigData.DataStoreType == DataStoreType.MySql) {
                FSqlCon.Con.Close();
            } else {
                FSqlite.CloseDb(FSqlCon);
            }
        }

        /// <summary>
        /// HOOK: Load the stored plugin config
        /// </summary>
        protected override void LoadConfig()
        {
            base.LoadConfig();

            try {
                FConfigData = Config.ReadObject<ConfigData>();

                if (FConfigData == null)
                    LoadDefaultConfig();
            } catch (Exception ex) {
                LogWarning($"LoadConfig > Exception: {ex}");
                LoadDefaultConfig();
            }
        }

        /// <summary>
        /// HOOK: Load the default plugin config
        /// </summary>
        protected override void LoadDefaultConfig()
        {
            FConfigData = new ConfigData();
            LogDebug("LoadDefaultConfig > Default config loaded");
            SaveConfig();
        }

        /// <summary>
        /// HOOK: Save the plugin config
        /// </summary>
        protected override void SaveConfig() => Config.WriteObject(FConfigData);

        /// <summary>
        /// HOOK: Process when a player has been banned
        /// </summary>
        /// <param name="name">The player's name</param>
        /// <param name="id">The player's ID</param>
        /// <param name="address">The player's IP address</param>
        /// <param name="reason">The ban reason</param>
        void OnUserBanned(string name, string id, string address, string reason)
        {
            if (FOldBans.Find(ban => { return ban.Id == id && ban.Name == name; }) == null)
                AddPlayerBan(id, name, reason ?? string.Empty);
        }

        /// <summary>
        /// HOOK: Process when a player has been unbanned
        /// </summary>
        /// <param name="name">The player's name</param>
        /// <param name="id">The player's ID</param>
        /// <param name="address">The player's IP address</param>
        void OnUserUnbanned(string name, string id, string address)
        {
            if (FOldBans.Find(ban => { return ban.Id == id; }) != null)
                RemovePlayerBan(id);
        }
        #endregion Hooks
    }
}
