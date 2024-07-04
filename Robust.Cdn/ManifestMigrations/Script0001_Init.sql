-- Stores a single fork managed by the CDN.
CREATE TABLE Fork(
    Id INTEGER PRIMARY KEY,
    -- Name as used in build and configuration files.
    Name TEXT NOT NULL UNIQUE,

    -- A cache of the manifest.json content used by the watchdog.
    -- Just contains raw JSON encoded data.
    ServerManifestCache BLOB NULL
);

-- A single stored version of a fork.
CREATE TABLE ForkVersion(
    Id INTEGER PRIMARY KEY,

    -- The name of the version itself.
    Name TEXT NOT NULL,

    -- The ID of the fork this version is on.
    ForkId INTEGER NOT NULL REFERENCES Fork(Id) ON DELETE CASCADE,

    -- The time when this version was published.
    PublishedTime DATETIME NOT NULL,

    -- The file name of the fork's client zip file in the version files.
    ClientFileName TEXT NOT NULL,
    -- SHA256 hash of the above file.
    ClientSha256 BLOB NOT NULL,

    -- Not strictly necessary, but I'll save it here anyways.
    EngineVersion TEXT NOT NULL,

    -- Whether this version is available for servers to download.
    -- This is updated after CDN content ingestion finishes.
    Available BOOLEAN DEFAULT(FALSE),

    -- Make sure version names are unique.
    UNIQUE (ForkId, Name)
);

-- A single stored server build for a fork version.
CREATE TABLE ForkVersionServerBuild(
    Id INTEGER PRIMARY KEY,

    -- Version that this build is for.
    ForkVersionId INTEGER NOT NULL REFERENCES ForkVersion(Id) ON DELETE CASCADE,

    -- The platform (.NET RID) for this server build.
    Platform TEXT NOT NULL,

    -- The file name of the server build.
    FileName TEXT NOT NULL,
    -- SHA256 hash of the above file.
    Sha256 BLOB NOT NULL,

    -- Can't have multiple builds on the same platform per version.
    UNIQUE (ForkVersionId, Platform),

    -- Can't have multiple builds with the same file name per version.
    UNIQUE (ForkVersionId, FileName)
);
