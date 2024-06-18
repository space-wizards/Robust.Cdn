﻿using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Robust.Cdn;

/// <summary>
/// Utility class to do SQLite database migrations.
/// </summary>
public sealed class Migrator
{
    internal static bool Migrate(IServiceProvider services, ILogger logger, SqliteConnection connection, string prefix)
    {
        logger.LogDebug("Migrating with prefix {Prefix}", prefix);

        using var transaction = connection.BeginTransaction(deferred: true);

        connection.Execute(@"
        CREATE TABLE IF NOT EXISTS SchemaVersions(
            SchemaVersionID INTEGER PRIMARY KEY,
            ScriptName TEXT NOT NULL,
            Applied DATETIME NOT NULL
        );");

        var appliedScripts = connection.Query<string>("SELECT ScriptName FROM main.SchemaVersions");

        // ReSharper disable once InvokeAsExtensionMethod
        var scriptsToApply = Enumerable.Concat(
            MigrationFileScriptList(prefix),
            MigrationCodeScriptList(prefix)
        ).ExceptBy(appliedScripts, s => s.name).OrderBy(x => x.name);

        var success = true;
        foreach (var (name, script) in scriptsToApply)
        {
            logger.LogInformation("Applying migration {Transaction}!", name);
            transaction.Save(name);

            try
            {
                var code = script.Up(services, connection);

                if (!string.IsNullOrWhiteSpace(code))
                    connection.Execute(code);

                connection.Execute(
                    "INSERT INTO SchemaVersions(ScriptName, Applied) VALUES (@Script, datetime('now'))",
                    new { Script = name });

                transaction.Release(name);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Exception during migration {Transaction}, rolling back...!", name);
                transaction.Rollback(name);
                success = false;
                break;
            }
        }

        logger.LogInformation("Committing migrations");
        transaction.Commit();
        return success;
    }

    private static IEnumerable<(string name, IMigrationScript)> MigrationCodeScriptList(string prefix)
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.Namespace != prefix || !type.Name.StartsWith("Script"))
                continue;

            if (!type.IsAssignableTo(typeof(IMigrationScript)))
                continue;

            var name = type.Name["Script".Length..];
            yield return (name, (IMigrationScript)Activator.CreateInstance(type)!);
        }
    }

    private static IEnumerable<(string name, IMigrationScript)> MigrationFileScriptList(string prefix)
    {
        var assembly = typeof(Migrator).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".sql") || !resourceName.StartsWith(prefix))
                continue;

            var index = resourceName.LastIndexOf('.', resourceName.Length - 5, resourceName.Length - 4);
            index += 1;

            var name = resourceName[(index + "Script".Length)..^4];

            using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
            var scriptContents = reader.ReadToEnd();
            yield return (name, new FileMigrationScript(scriptContents));
        }
    }

    public interface IMigrationScript
    {
        string Up(IServiceProvider services, SqliteConnection connection);
    }

    private sealed class FileMigrationScript : IMigrationScript
    {
        private readonly string _code;

        public FileMigrationScript(string code) => _code = code;

        public string Up(IServiceProvider services, SqliteConnection connection) => _code;
    }
}
