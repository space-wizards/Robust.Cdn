using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Robust.Cdn.Migrations;

public sealed class Script0003_AddFork : Migrator.IMigrationScript
{
    public string Up(IServiceProvider services, SqliteConnection connection)
    {
        var options = services.GetRequiredService<IOptions<CdnOptions>>().Value;

        connection.Execute("""
            CREATE TABLE Fork(
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE
            );
            """);

        var defaultFork = options.DefaultFork;
        // This default value of "0" is not used unless we insert it down below.
        var defaultForkId = 0;

        if (connection.QuerySingle<int>("SELECT COUNT(*) FROM ContentVersion") > 0)
        {
            if (defaultFork == null)
            {
                throw new InvalidOperationException(
                    "Database has existing versions stored, need to set DefaultFork in CdnOptions to enable migration.");
            }

            defaultForkId = connection.QuerySingle<int>("""
                INSERT INTO Fork (Name)
                VALUES (@Name)
                RETURNING Id
                """, new { Name = defaultFork });
        }

        // Re-create tables to be able to add a "Fork"
        connection.Execute("""
            DROP INDEX ContentManifestEntryContentId;
            ALTER TABLE ContentManifestEntry RENAME TO _ContentManifestEntry;
            ALTER TABLE ContentVersion RENAME TO _ContentVersion;

            CREATE TABLE ContentManifestEntry(
                VersionId INTEGER REFERENCES ContentVersion(Id) ON DELETE CASCADE,
                ManifestIdx INTEGER,

                ContentId REFERENCES Content(Id) ON DELETE RESTRICT,

                PRIMARY KEY (VersionId, ManifestIdx)
            ) WITHOUT ROWID;

            CREATE INDEX ContentManifestEntryContentId ON ContentManifestEntry(ContentId);

            CREATE TABLE ContentVersion(
                Id INTEGER PRIMARY KEY,
                ForkId INTEGER NOT NULL REFERENCES Fork(Id) ON DELETE CASCADE,
                Version TEXT NOT NULL,
                TimeAdded DATETIME NOT NULL,
                ManifestHash BLOB NOT NULL,
                ManifestData BLOB NOT NULL,
                CountDistinctBlobs INTEGER NOT NULL,

                UNIQUE (ForkId, Version)
            );

            -- Transfer data from old tables.
            INSERT INTO ContentVersion
            SELECT Id, @DefaultFork, Version, TimeAdded, ManifestHash, ManifestData, CountDistinctBlobs
            FROM _ContentVersion;

            INSERT INTO ContentManifestEntry
            SELECT VersionId, ManifestIdx, ContentId
            FROM _ContentManifestEntry;

            DROP TABLE _ContentVersion;
            DROP TABLE _ContentManifestEntry;
            """, new {DefaultFork = defaultForkId});

        return "";
    }
}
