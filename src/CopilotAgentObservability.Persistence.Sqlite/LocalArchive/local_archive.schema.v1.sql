CREATE TABLE local_archive_current(
  target_kind TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_kind)='text' AND target_kind IN ('session','repository')),
  target_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_id)='text'
      AND length(target_id)=36
      AND target_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND target_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(target_id,'-',''))=32),
  state TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(state)='text' AND state IN ('active','archived')),
  revision INTEGER NOT NULL
    CHECK(typeof(revision)='integer'
      AND revision BETWEEN 1 AND 9223372036854775807),
  archived_at TEXT COLLATE BINARY NULL
    CHECK(archived_at IS NULL OR (
      typeof(archived_at)='text'
      AND length(archived_at)=33
      AND archived_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(archived_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(archived_at,6,2) BETWEEN '01' AND '12'
      AND substr(archived_at,12,2) BETWEEN '00' AND '23'
      AND substr(archived_at,15,2) BETWEEN '00' AND '59'
      AND substr(archived_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(archived_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(archived_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(archived_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(archived_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(archived_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END)),
  updated_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(updated_at)='text'
      AND length(updated_at)=33
      AND updated_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(updated_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(updated_at,6,2) BETWEEN '01' AND '12'
      AND substr(updated_at,12,2) BETWEEN '00' AND '23'
      AND substr(updated_at,15,2) BETWEEN '00' AND '59'
      AND substr(updated_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(updated_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(updated_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(updated_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(updated_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(updated_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END),
  PRIMARY KEY(target_kind,target_id),
  CHECK((state='active' AND revision%2=0 AND archived_at IS NULL)
     OR (state='archived' AND revision%2=1 AND archived_at IS NOT NULL))
);

CREATE TABLE local_archive_events(
  event_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY
    CHECK(typeof(event_id)='text'
      AND length(event_id)=36
      AND event_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND event_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(event_id,'-',''))=32),
  target_kind TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_kind)='text' AND target_kind IN ('session','repository')),
  target_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_id)='text'
      AND length(target_id)=36
      AND target_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND target_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(target_id,'-',''))=32),
  action TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(action)='text' AND action IN ('archive','restore')),
  previous_revision INTEGER NOT NULL
    CHECK(typeof(previous_revision)='integer'
      AND previous_revision BETWEEN 0 AND 9223372036854775806),
  new_revision INTEGER NOT NULL
    CHECK(typeof(new_revision)='integer'
      AND new_revision BETWEEN 1 AND 9223372036854775807),
  occurred_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(occurred_at)='text'
      AND length(occurred_at)=33
      AND occurred_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(occurred_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(occurred_at,6,2) BETWEEN '01' AND '12'
      AND substr(occurred_at,12,2) BETWEEN '00' AND '23'
      AND substr(occurred_at,15,2) BETWEEN '00' AND '59'
      AND substr(occurred_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(occurred_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(occurred_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(occurred_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(occurred_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(occurred_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END),
  CHECK(new_revision=previous_revision+1),
  CHECK((action='archive' AND new_revision%2=1)
     OR (action='restore' AND new_revision%2=0)),
  FOREIGN KEY(target_kind,target_id)
    REFERENCES local_archive_current(target_kind,target_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE INDEX IX_local_archive_current_archived_page
  ON local_archive_current(target_kind,state,archived_at DESC,target_id DESC);

CREATE UNIQUE INDEX IX_local_archive_events_target_revision
  ON local_archive_events(target_kind,target_id,new_revision);

CREATE TRIGGER local_archive_current_identity_update_rejected
BEFORE UPDATE OF target_kind,target_id ON local_archive_current
WHEN NEW.target_kind IS NOT OLD.target_kind
  OR NEW.target_id IS NOT OLD.target_id
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_identity_immutable');
END;

CREATE TRIGGER local_archive_current_delete_rejected
BEFORE DELETE ON local_archive_current
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_delete_rejected');
END;

CREATE TRIGGER local_archive_current_insert_replacement_rejected
BEFORE INSERT ON local_archive_current
WHEN EXISTS(
  SELECT 1 FROM local_archive_current
  WHERE target_kind=NEW.target_kind AND target_id=NEW.target_id)
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_replacement_rejected');
END;

CREATE TRIGGER local_archive_events_update_rejected
BEFORE UPDATE ON local_archive_events
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;

CREATE TRIGGER local_archive_events_delete_rejected
BEFORE DELETE ON local_archive_events
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;

CREATE TRIGGER local_archive_events_insert_replacement_rejected
BEFORE INSERT ON local_archive_events
WHEN EXISTS(
  SELECT 1 FROM local_archive_events
  WHERE event_id=NEW.event_id
     OR (target_kind=NEW.target_kind
         AND target_id=NEW.target_id
         AND new_revision=NEW.new_revision))
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;
