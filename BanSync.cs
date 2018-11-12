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
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("BanSync", "ThibmoRozier/sqroot", "2.0.1")]
    [Description("Synchronizes bans across servers.")]
    public class BanSync : RustPlugin
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
            public ulong SteamId { get; set; }
            public string Username { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Banned Player object comparer class
        /// </summary>
        private class CompareBannedPlayers : IEqualityComparer<BannedPlayer>
        {
            /// <summary>
            /// Determine if two objects are equal
            /// </summary>
            /// <param name="a"></param>
            /// <param name="b"></param>
            /// <returns></returns>
            public bool Equals(BannedPlayer a, BannedPlayer b)
            {
                // Check whether the objects are the same object. 
                if (ReferenceEquals(a, b))
                    return true;

                return a != null && b != null && a.SteamId.Equals(b.SteamId) &&
                       a.Username.Equals(b.Username) && a.Notes.Equals(b.Notes);
            }

            /// <summary>
            /// Retrieve the hash code for an object
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            public int GetHashCode(BannedPlayer obj)
            {
                int steamIdHash = obj.SteamId.GetHashCode();
                int usernameHash = obj.Username.GetHashCode();
                int notesHash = obj.Notes.GetHashCode();
                return steamIdHash ^ steamIdHash ^ notesHash;
            }
        }

        /// <summary>
        /// Helper class to determine which users are still banned
        /// </summary>
        private class BanDiff
        {
            public List<BannedPlayer> NewBans = new List<BannedPlayer>();
            public List<BannedPlayer> RemovedBans = new List<BannedPlayer>();

            /// <summary>
            /// Class constructor
            /// </summary>
            /// <param name="aOldList">The old banned user list</param>
            /// <param name="aNewList">The current banned user list</param>
            public BanDiff(List<BannedPlayer> aOldList, List<BannedPlayer> aNewList)
            {
                CompareBannedPlayers comparer = new CompareBannedPlayers();
                NewBans.AddRange(aNewList.Except(aOldList, comparer));
                RemovedBans.AddRange(aOldList.Except(aNewList, comparer));
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
            if (FConfigData.DataStoreType == DataStoreType.MySql) {
                FSqlCon?.Con?.Close();
            } else {
                FSqlite?.CloseDb(FSqlCon);
            }

            LogToFile(string.Empty, $"[{DateTime.Now.ToString("hh:mm:ss")}] ERROR > {aMessage}", this);
            timer.Once(0.01f, () => Interface.GetMod().UnloadPlugin("BanSync"));
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
            List<ServerUsers.User> result = ServerUsers.GetAll(ServerUsers.UserGroup.Banned).ToList();
            LogDebug($"GetBannedUserList > Retrieved the banned user list, result.Count = {result.Count}");
            result.Sort((a, b) => {
                int diff = string.Compare(a.username, b.username);

                if (diff == 0)
                    diff = a.steamid.CompareTo(b.steamid);

                return diff;
            });
            return (
                from user in result
                select new BannedPlayer { SteamId = user.steamid, Username = user.username, Notes = user.notes }
            ).ToList();
        }
        
        /// <summary>
        /// Create a new "REPLACE INTO" query for bansynch
        /// </summary>
        /// <param name="aBans">The list of bans to insert or replace</param>
        /// <param name="aSqlData">The resulting list of SQL data</param>
        /// <returns>The SQL query string</returns>
        private string CreateReplaceQuery(List<BannedPlayer> aBans, out List<object> aSqlData)
        {
            string sqlNewValues = "";
            aSqlData = new List<object>();

            for (int i = 0; i < aBans.Count; i++) {
                BannedPlayer ban = aBans[i];
                int startIndex = 3 * i;
                sqlNewValues += $"(@{startIndex}, @{startIndex + 1}, @{startIndex + 2}), ";

                /* 
                 * IMPORTANT:
                 *   Items must be added in the proper order, or they will get missplaced in the query
                 */
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    aSqlData.Add(ban.SteamId);
                } else {
                    aSqlData.Add(ban.SteamId.ToString());
                }

                aSqlData.Add(ban.Username);
                aSqlData.Add(ban.Notes);
            }

            sqlNewValues = sqlNewValues.Remove(sqlNewValues.Length - 2);
            return $"REPLACE INTO {CBanTableName} (UserId, Username, Notes) VALUES {sqlNewValues};";
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
        private readonly Core.MySql.Libraries.MySql FMySql = Interface.GetMod().GetLibrary<Core.MySql.Libraries.MySql>();
        private readonly Core.SQLite.Libraries.SQLite FSqlite = Interface.GetMod().GetLibrary<Core.SQLite.Libraries.SQLite>();
        #endregion Fields

        #region Database methods
        /// <summary>
        /// Connect to the backend database
        /// </summary>
        /// <returns>Success</returns>
        private bool ConnectToDatabase()
        {
            try {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FSqlCon = FMySql.OpenDb(
                        FConfigData.MySQLHost,
                        FConfigData.MySQLPort,
                        FConfigData.MySQLDb,
                        FConfigData.MySQLUser,
                        FConfigData.MySQLPass,
                        this
                    );
                    LogDebug($"ConnectToDatabase > Is FSqlCon NULL? {(FSqlCon == null ? "yes" : "no")}");

                    if (FSqlCon == null || FSqlCon.Con == null) {
                        LogError($"ConnectToDatabase > Couldn't open the MySQL Database: {FSqlCon.Con.State.ToString()}");
                        return false;
                    }
                } else {
                    FSqlCon = FSqlite.OpenDb(FConfigData.SQLiteDb, this);
                    LogDebug($"ConnectToDatabase > Is FSqlCon NULL? {(FSqlCon == null ? "yes" : "no")}");

                    if (FSqlCon == null) {
                        LogError("ConnectToDatabase > Couldn't open the SQLite Database.");
                        return false;
                    }
                }

                return true;
            } catch (Exception e) {
                LogError($"{e.ToString()}");
                return false;
            }
        }

        /// <summary>
        /// Initialize the plugin
        /// </summary>
        private void InitBanSync()
        {
            FOldBans = GetBannedUserList();
            LogDebug(
                $"InitBanSync > FOldBans.Count = {FOldBans.Count}{Environment.NewLine}" +
                $"InitBanSync > Is FConfigData NULL? {(FConfigData == null ? "yes" : "no")}{Environment.NewLine}" +
                $"InitBanSync > Is FMySql NULL? {(FMySql == null ? "yes" : "no")}{Environment.NewLine}" +
                $"InitBanSync > Is FSqlite NULL? {(FSqlite == null ? "yes" : "no")}"
            );

            if (ConnectToDatabase()) {
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
        private void CreateDb(List<Dictionary<string, object>> aRows)
        {
            if (aRows.Count > 0) {
                PullBans();
            } else {
                if (FConfigData.DataStoreType == DataStoreType.MySql) {
                    FMySql.ExecuteNonQuery(
                        new Sql(
                            $"CREATE TABLE {CBanTableName} (UserId BIGINT UNSIGNED NOT NULL PRIMARY KEY, Username TEXT NOT NULL, Notes LONGTEXT NOT NULL);"
                        ),
                        FSqlCon
                    );
                } else {
                    FSqlite.ExecuteNonQuery(
                        new Sql($"CREATE TABLE {CBanTableName} (UserId TEXT NOT NULL PRIMARY KEY UNIQUE, Username TEXT NOT NULL, Notes TEXT NOT NULL);"),
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
        private void UpdateBans(List<Dictionary<string, object>> aRows)
        {
            BanDiff diff = new BanDiff(
                GetBannedUserList(),
                (
                    from row in aRows
                    select new BannedPlayer {
                        SteamId = FConfigData.DataStoreType == DataStoreType.MySql ? (ulong)row["UserId"] : ulong.Parse((string)row["UserId"]),
                        Username = (string)row["Username"],
                        Notes = (string)row["Notes"]
                    }
                ).ToList()
            );
            LogDebug($"UpdateBans > diff.NewBans.Count = {diff.NewBans.Count} ; diff.RemovedBans.Count = {diff.RemovedBans.Count}");
            diff.NewBans.ForEach(ban => {
                BasePlayer player = BasePlayer.FindByID(ban.SteamId);
                ServerUsers.Set(ban.SteamId, ServerUsers.UserGroup.Banned, ban.Username, ban.Notes);
                FOldBans.Add(ban);
                player?.Kick(string.Format("Banned: {0}", ban.Notes));
            });
            diff.RemovedBans.ForEach(unban => {
                ServerUsers.Remove(unban.SteamId);
                FOldBans.RemoveAll(ban => ban.SteamId == unban.SteamId);
            });

            if (diff.NewBans.Count > 0 || diff.RemovedBans.Count > 0) {
                ServerUsers.Save();
                FOldBans = GetBannedUserList();
                LogDebug($"UpdateBans > FOldBans.Count = {FOldBans.Count}");
            }

            if (FConfigData.DataStoreType == DataStoreType.MySql) {
                FSqlCon.Con.Close();
            } else {
                FSqlite.CloseDb(FSqlCon);
            }

            timer.Once(20, PushBans);
        }
        
        /// <summary>
        /// Pull the currently active bans from the database
        /// </summary>
        /// <param name="aRowsAffected">The count of rows affected by the previous SQL query</param>
        private void PullBans(int aRowsAffected = 0)
        {
            LogDebug($"PullBans > aRowsAffected = {aRowsAffected}");

            if (aRowsAffected > 0) {
                ServerUsers.Save();
                FOldBans = GetBannedUserList();
            }

            Sql query = new Sql($"SELECT UserId, Username, Notes FROM {CBanTableName}");
            LogDebug($"PullBans > SQL = {query.SQL}");

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
            List<BannedPlayer> bans = GetBannedUserList();
            BanDiff diff = new BanDiff(FOldBans, bans);
            Sql query = new Sql();
            LogDebug($"PushBans > bans.Count = {bans.Count}");
            LogDebug($"PushBans > diff.NewBans.Count = {diff.NewBans.Count} ; diff.RemovedBans.Count = {diff.RemovedBans.Count}");

            if (diff.NewBans.Count > 0) {
                List<object> sqlNewData;
                string replaceQueryStr = CreateReplaceQuery(FOldBans, out sqlNewData);
                query.Append(replaceQueryStr, sqlNewData.ToArray());
            }

            if (diff.RemovedBans.Count > 0) {
                string sqlRemoveValues = "";

                for (int i = 0; i < diff.RemovedBans.Count; i++) {
                    int startIndex = 3 * i;
                    BannedPlayer unban = diff.RemovedBans[i];
                    sqlRemoveValues += FConfigData.DataStoreType == DataStoreType.MySql ? $"{unban.SteamId}, " : $"'{unban.SteamId}', ";
                }

                sqlRemoveValues = sqlRemoveValues.Remove(sqlRemoveValues.Length - 2);
                query.Append($"DELETE FROM {CBanTableName} WHERE UserId IN ({sqlRemoveValues});");
            }

            if (diff.NewBans.Count > 0 || diff.RemovedBans.Count > 0) {
                LogDebug($"PushBans > SQL = {query.SQL}");

                if (ConnectToDatabase()) {
                    if (FConfigData.DataStoreType == DataStoreType.MySql) {
                        FMySql.ExecuteNonQuery(query, FSqlCon, PullBans);
                    } else {
                        FSqlite.ExecuteNonQuery(query, FSqlCon, PullBans);
                    }
                }
            } else {
                PullBans();
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
            } catch {
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
        #endregion Hooks
    }
}
