-- Add columns that can represent sources used to build version that should be published
ALTER TABLE PublishInProgress ADD COLUMN SourceUrl TEXT NULL;
ALTER TABLE PublishInProgress ADD COLUMN SourceCommitId TEXT NULL;
ALTER TABLE PublishInProgress ADD COLUMN SourceBranchName TEXT NULL;
ALTER TABLE PublishInProgress ADD COLUMN EngineSourceUrl TEXT NULL;
ALTER TABLE PublishInProgress ADD COLUMN EngineSourceCommitId TEXT NULL;
ALTER TABLE PublishInProgress ADD COLUMN EngineSourceBranchName TEXT NULL;
