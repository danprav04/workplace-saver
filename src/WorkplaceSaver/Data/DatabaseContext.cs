using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WorkplaceSaver.Data
{
    public class DatabaseContext
    {
        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkplaceSaver");

        public static string DatabasePath =>
            Path.Combine(AppDataFolder, "workplace_saver.db");

        public static string ThumbnailsFolder =>
            Path.Combine(AppDataFolder, "Thumbnails");

        public static string ConnectionString =>
            $"Data Source={DatabasePath};";

        public static void Initialize()
        {
            Directory.CreateDirectory(AppDataFolder);
            Directory.CreateDirectory(ThumbnailsFolder);

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS Workspaces (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    CreatedAt TEXT NOT NULL,
                    ThumbnailPath TEXT,
                    Tags TEXT,
                    WindowCount INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS WindowSnapshots (
                    Id TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    ProcessName TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL,
                    CommandLine TEXT,
                    WindowTitle TEXT,
                    ClassName TEXT,
                    ShowCmd INTEGER NOT NULL,
                    Flags INTEGER NOT NULL,
                    NormalLeft INTEGER NOT NULL,
                    NormalTop INTEGER NOT NULL,
                    NormalRight INTEGER NOT NULL,
                    NormalBottom INTEGER NOT NULL,
                    ZOrder INTEGER NOT NULL,
                    AppIconBase64 TEXT,
                    FOREIGN KEY(WorkspaceId) REFERENCES Workspaces(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_WindowSnapshots_WorkspaceId ON WindowSnapshots(WorkspaceId);
            ";
            cmd.ExecuteNonQuery();
        }
    }
}
