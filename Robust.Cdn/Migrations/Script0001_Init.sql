-- Stores the actual content of game files.
CREATE TABLE Content(
    Id INTEGER PRIMARY KEY,
    -- BLAKE2B hash of the (uncompressed) data stored in this file.
    -- Unique constraint to not allow duplicate blobs in the database.
    -- Also should be backed by an index allowing us to efficiently look up existing blobs when writing.
    Hash BLOB NOT NULL UNIQUE,
    -- Uncompressed size of the data stored in this file.
    Size INTEGER NOT NULL,
    -- Compression scheme used to store this file.
    -- See ContentCompressionScheme enum for values.
    Compression INTEGER NOT NULL,
    -- Actual data for the file. May be compressed based on "Compression".
    Data BLOB NOT NULL,
    -- Simple check: if a file is uncompressed, "Size" MUST match "Data" length.
    CONSTRAINT UncompressedSameSize CHECK(Compression != 0 OR length(Data) = Size)
);

-- Unlike the launcher, we don't care to keep track of file paths.
-- We only need this table to be able to:
-- * Fetch by manifest index to respond to downloads
-- * Keep track of FK to Content easily.
CREATE TABLE ContentManifestEntry(
    VersionId INTEGER REFERENCES ContentVersion(Id) ON DELETE CASCADE,
    ManifestIdx INTEGER,

    ContentId REFERENCES Content(Id) ON DELETE RESTRICT,

    PRIMARY KEY (VersionId, ManifestIdx)
) WITHOUT ROWID;

-- Used to aid in FK deletion of Content.
CREATE INDEX ContentManifestEntryContentId ON ContentManifestEntry(ContentId);

CREATE TABLE ContentVersion(
    Id INTEGER PRIMARY KEY,
    Version TEXT NOT NULL UNIQUE,
    TimeAdded DATETIME NOT NULL,
    ManifestHash BLOB NOT NULL,
    ManifestData BLOB NOT NULL,
    -- Getting this count is somewhat slow so we cache it.
    CountDistinctBlobs INTEGER NOT NULL
);


-- Table for logging of requests
-- These request blobs can get relatively large so I hope that storing them by hash will save a decent amount of bytes.
CREATE TABLE RequestLogBlob(
    Id INTEGER PRIMARY KEY,
    Hash BLOB NOT NULL UNIQUE,
    Data BLOB NOT NULL
);

CREATE TABLE RequestLog(
    Id INTEGER PRIMARY KEY,
    Time DATETIME NOT NULL,
    Compression INTEGER NOT NULL,
    Protocol INTEGER NOT NULL,
    BytesSent INTEGER NOT NULL,
    VersionId INTEGER NOT NULL REFERENCES ContentVersion(Id) ON DELETE RESTRICT,
    BlobId INTEGER NOT NULL REFERENCES RequestLogBlob(Id) ON DELETE RESTRICT
);
