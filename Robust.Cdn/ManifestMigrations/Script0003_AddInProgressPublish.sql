-- A publish that is currently in-progress.
-- The publishing job should currently be in the process of uploading the individual files.
CREATE TABLE PublishInProgress(
    Id INTEGER PRIMARY KEY,

    -- Version name that is being published.
    Version TEXT NOT NULL,

    -- The fork being published to.
    ForkId INTEGER NOT NULL REFERENCES Fork(Id) ON DELETE CASCADE,

    -- When the publish was started.
    StartTime DATETIME NOT NULL,

    -- The engine version being published.
    EngineVersion TEXT NULL,

    UNIQUE (ForkId, Version)
);
