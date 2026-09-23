namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// A single ordered schema migration. Versions are applied in ascending order and the
/// highest known version is the schema version this plugin expects.
/// </summary>
/// <param name="Version">Monotonic schema version.</param>
/// <param name="Description">Short human readable description.</param>
/// <param name="Sql">One or more DDL/DML statements applied inside a transaction.</param>
public sealed record SchemaMigration(int Version, string Description, string Sql, bool RebuildsReferencedTables = false);
