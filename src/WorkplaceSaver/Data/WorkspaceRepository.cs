using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using WorkplaceSaver.Models;

namespace WorkplaceSaver.Data
{
    public class WorkspaceRepository
    {
        public async Task<List<Workspace>> GetAllWorkspacesAsync(string? search = null, string sortBy = "date_desc")
        {
            using var connection = new SqliteConnection(DatabaseContext.ConnectionString);
            await connection.OpenAsync();

            string orderByClause = sortBy switch
            {
                "date_asc" => "ORDER BY CreatedAt ASC",
                "name_asc" => "ORDER BY Name COLLATE NOCASE ASC",
                "name_desc" => "ORDER BY Name COLLATE NOCASE DESC",
                "apps_desc" => "ORDER BY WindowCount DESC",
                "apps_asc" => "ORDER BY WindowCount ASC",
                _ => "ORDER BY CreatedAt DESC"
            };

            string query;
            object? param;

            if (string.IsNullOrWhiteSpace(search))
            {
                query = $"SELECT * FROM Workspaces {orderByClause}";
                param = null;
            }
            else
            {
                query = $@"
                    SELECT DISTINCT w.* 
                    FROM Workspaces w
                    LEFT JOIN WindowSnapshots s ON w.Id = s.WorkspaceId
                    WHERE w.Name LIKE @search 
                       OR w.Tags LIKE @search 
                       OR s.WindowTitle LIKE @search 
                       OR s.ProcessName LIKE @search
                    {orderByClause}";
                param = new { search = $"%{search.Trim()}%" };
            }

            var workspaces = (await connection.QueryAsync<Workspace>(query, param)).ToList();

            if (workspaces.Count > 0)
            {
                var workspaceIds = workspaces.Select(w => w.Id).ToList();
                var windows = (await connection.QueryAsync<WindowSnapshot>(
                    "SELECT * FROM WindowSnapshots WHERE WorkspaceId IN @Ids ORDER BY ZOrder ASC",
                    new { Ids = workspaceIds })).ToList();

                var grouped = windows.GroupBy(w => w.WorkspaceId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var ws in workspaces)
                {
                    if (grouped.TryGetValue(ws.Id, out var wsWindows))
                    {
                        ws.Windows = wsWindows;
                        ws.WindowCount = wsWindows.Count;
                    }
                }
            }

            return workspaces;
        }

        public async Task<Workspace?> GetWorkspaceByIdAsync(string id)
        {
            using var connection = new SqliteConnection(DatabaseContext.ConnectionString);
            await connection.OpenAsync();

            var workspace = await connection.QuerySingleOrDefaultAsync<Workspace>(
                "SELECT * FROM Workspaces WHERE Id = @Id", new { Id = id });

            if (workspace != null)
            {
                var windows = await connection.QueryAsync<WindowSnapshot>(
                    "SELECT * FROM WindowSnapshots WHERE WorkspaceId = @Id ORDER BY ZOrder ASC",
                    new { Id = id });
                workspace.Windows = windows.ToList();
                workspace.WindowCount = workspace.Windows.Count;
            }

            return workspace;
        }

        public async Task SaveWorkspaceAsync(Workspace workspace)
        {
            using var connection = new SqliteConnection(DatabaseContext.ConnectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            workspace.WindowCount = workspace.Windows.Count;

            const string insertWorkspaceSql = @"
                INSERT INTO Workspaces (Id, Name, Description, CreatedAt, ThumbnailPath, Tags, WindowCount)
                VALUES (@Id, @Name, @Description, @CreatedAt, @ThumbnailPath, @Tags, @WindowCount)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Description = excluded.Description,
                    ThumbnailPath = excluded.ThumbnailPath,
                    Tags = excluded.Tags,
                    WindowCount = excluded.WindowCount;
            ";

            await connection.ExecuteAsync(insertWorkspaceSql, new
            {
                workspace.Id,
                workspace.Name,
                workspace.Description,
                CreatedAt = workspace.CreatedAt.ToString("o"),
                workspace.ThumbnailPath,
                workspace.Tags,
                workspace.WindowCount
            }, transaction);

            // Replace existing window snapshots
            await connection.ExecuteAsync(
                "DELETE FROM WindowSnapshots WHERE WorkspaceId = @WorkspaceId",
                new { WorkspaceId = workspace.Id }, transaction);

            const string insertWindowSql = @"
                INSERT INTO WindowSnapshots (
                    Id, WorkspaceId, ProcessName, ExecutablePath, CommandLine,
                    WindowTitle, ClassName, ShowCmd, Flags,
                    NormalLeft, NormalTop, NormalRight, NormalBottom,
                    ZOrder, AppIconBase64
                ) VALUES (
                    @Id, @WorkspaceId, @ProcessName, @ExecutablePath, @CommandLine,
                    @WindowTitle, @ClassName, @ShowCmd, @Flags,
                    @NormalLeft, @NormalTop, @NormalRight, @NormalBottom,
                    @ZOrder, @AppIconBase64
                );
            ";

            if (workspace.Windows.Count > 0)
            {
                await connection.ExecuteAsync(insertWindowSql, workspace.Windows, transaction);
            }

            transaction.Commit();
        }

        public async Task UpdateWorkspaceNameAndTagsAsync(string id, string name, string? tags)
        {
            using var connection = new SqliteConnection(DatabaseContext.ConnectionString);
            await connection.OpenAsync();

            await connection.ExecuteAsync(
                "UPDATE Workspaces SET Name = @Name, Tags = @Tags WHERE Id = @Id",
                new { Id = id, Name = name, Tags = tags });
        }

        public async Task DeleteWorkspaceAsync(string id)
        {
            using var connection = new SqliteConnection(DatabaseContext.ConnectionString);
            await connection.OpenAsync();

            var thumbnailPath = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT ThumbnailPath FROM Workspaces WHERE Id = @Id", new { Id = id });

            if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
            {
                try { File.Delete(thumbnailPath); } catch { /* Ignore file lock */ }
            }

            await connection.ExecuteAsync("DELETE FROM Workspaces WHERE Id = @Id", new { Id = id });
        }
    }
}
