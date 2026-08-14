CREATE TABLE skill_invocation_snapshots (
    snapshot_id TEXT PRIMARY KEY
        CHECK(length(snapshot_id)=36 AND lower(snapshot_id)=snapshot_id
          AND substr(snapshot_id,9,1)='-' AND substr(snapshot_id,14,1)='-'
          AND substr(snapshot_id,19,1)='-' AND substr(snapshot_id,24,1)='-'
          AND substr(snapshot_id,15,1)='7'
          AND substr(snapshot_id,20,1) IN ('8','9','a','b')
          AND snapshot_id NOT GLOB '*[^0-9a-f-]*'),
    session_id TEXT NOT NULL,
    native_session_id TEXT NOT NULL
        CHECK(typeof(native_session_id)='text'
          AND length(native_session_id) BETWEEN 1 AND 256
          AND instr(native_session_id,char(0))=0
          AND length(CAST(native_session_id AS BLOB)) BETWEEN 1 AND 1024),
    event_id TEXT NOT NULL,
    claim_id TEXT NULL,
    run_id TEXT NULL,
    trace_id TEXT NULL
        CHECK(trace_id IS NULL OR
          (length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*')),
    span_id TEXT NULL
        CHECK(span_id IS NULL OR
          (length(span_id)=16 AND span_id NOT GLOB '*[^0-9a-f]*')),
    name TEXT NULL,
    source TEXT NULL,
    trigger TEXT NULL,
    state TEXT NOT NULL
        CHECK(state IN ('available','malformed','missing','binary','oversized')),
    reason TEXT NOT NULL
        CHECK(reason IN ('none','name_missing','body_missing',
          'definition_path_missing','duplicate_property','unknown_property',
          'invalid_field_type','name_invalid','body_unicode_invalid',
          'path_unicode_invalid','body_oversized','path_oversized','path_invalid')),
    content_item_id TEXT NOT NULL UNIQUE
        CHECK(length(content_item_id)=32
          AND content_item_id NOT GLOB '*[^0-9a-f]*'),
    payload_sha256 TEXT NOT NULL
        CHECK(length(payload_sha256)=64
          AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
    payload_bytes INTEGER NOT NULL
        CHECK(typeof(payload_bytes)='integer'
          AND payload_bytes BETWEEN 2 AND 8388608),
    content_document_sha256 TEXT NOT NULL
        CHECK(length(content_document_sha256)=64
          AND content_document_sha256 NOT GLOB '*[^0-9a-f]*'),
    body_sha256 TEXT NULL
        CHECK(body_sha256 IS NULL OR
          (length(body_sha256)=64 AND body_sha256 NOT GLOB '*[^0-9a-f]*')),
    body_utf8_bytes INTEGER NULL
        CHECK(body_utf8_bytes IS NULL OR
          (typeof(body_utf8_bytes)='integer'
           AND body_utf8_bytes BETWEEN 0 AND 1048576)),
    definition_path_sha256 TEXT NULL
        CHECK(definition_path_sha256 IS NULL OR
          (length(definition_path_sha256)=64
           AND definition_path_sha256 NOT GLOB '*[^0-9a-f]*')),
    definition_path_utf8_bytes INTEGER NULL
        CHECK(definition_path_utf8_bytes IS NULL OR
          (typeof(definition_path_utf8_bytes)='integer'
           AND definition_path_utf8_bytes BETWEEN 1 AND 4096)),
    source_parent_event_id TEXT NULL
        CHECK(source_parent_event_id IS NULL OR
          (length(source_parent_event_id)=36
           AND lower(source_parent_event_id)=source_parent_event_id
           AND substr(source_parent_event_id,9,1)='-'
           AND substr(source_parent_event_id,14,1)='-'
           AND substr(source_parent_event_id,19,1)='-'
           AND substr(source_parent_event_id,24,1)='-'
           AND substr(source_parent_event_id,15,1)='4'
           AND substr(source_parent_event_id,20,1) IN ('8','9','a','b')
           AND source_parent_event_id NOT GLOB '*[^0-9a-f-]*')),
    source_ephemeral INTEGER NOT NULL
        CHECK(typeof(source_ephemeral)='integer' AND source_ephemeral IN (0,1)),
    source_application_version TEXT NOT NULL CHECK(length(source_application_version)>0),
    adapter_version TEXT NOT NULL CHECK(length(adapter_version)>0),
    normalization_version TEXT NOT NULL CHECK(length(normalization_version)>0),
    payload_schema TEXT NOT NULL
        CHECK(payload_schema='github-copilot-sdk.skill-invoked.v1'),
    schema_fingerprint TEXT NOT NULL
        CHECK(length(schema_fingerprint)=64
          AND schema_fingerprint NOT GLOB '*[^0-9a-f]*'),
    captured_at TEXT NOT NULL CHECK(length(captured_at)=33),
    created_at TEXT NOT NULL CHECK(length(created_at)=33),
    UNIQUE(session_id,event_id),
    UNIQUE(claim_id),
    CHECK((trace_id IS NULL)=(span_id IS NULL)),
    CHECK(
      (state='available' AND reason='none'
       AND claim_id IS NOT NULL AND name IS NOT NULL
       AND body_sha256 IS NOT NULL AND body_utf8_bytes IS NOT NULL
       AND definition_path_sha256 IS NOT NULL
       AND definition_path_utf8_bytes IS NOT NULL)
      OR
      (state<>'available' AND reason<>'none'
       AND claim_id IS NULL AND run_id IS NULL
       AND trace_id IS NULL AND span_id IS NULL
       AND name IS NULL AND source IS NULL AND trigger IS NULL
       AND body_sha256 IS NULL AND body_utf8_bytes IS NULL
       AND definition_path_sha256 IS NULL
       AND definition_path_utf8_bytes IS NULL)),
    CHECK((state='malformed' AND reason IN
            ('duplicate_property','unknown_property','invalid_field_type',
             'name_invalid','path_invalid'))
       OR (state='missing' AND reason IN
            ('name_missing','body_missing','definition_path_missing'))
       OR (state='binary' AND reason IN
            ('body_unicode_invalid','path_unicode_invalid'))
       OR (state='oversized' AND reason IN
            ('body_oversized','path_oversized'))
       OR (state='available' AND reason='none')),
    FOREIGN KEY(session_id) REFERENCES sessions(session_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY(session_id,event_id) REFERENCES session_events(session_id,event_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY(session_id,run_id) REFERENCES session_runs(session_id,run_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY(claim_id) REFERENCES skill_projection_sdk_claims(claim_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY(content_item_id) REFERENCES retention_items(item_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE TABLE skill_invocation_snapshot_receipts (
    source_adapter TEXT NOT NULL CHECK(source_adapter='copilot-sdk-stream'),
    source_event_id TEXT NOT NULL
        CHECK(length(source_event_id)=36 AND lower(source_event_id)=source_event_id
          AND substr(source_event_id,9,1)='-'
          AND substr(source_event_id,14,1)='-'
          AND substr(source_event_id,19,1)='-'
          AND substr(source_event_id,24,1)='-'
          AND substr(source_event_id,15,1)='4'
          AND substr(source_event_id,20,1) IN ('8','9','a','b')
          AND source_event_id NOT GLOB '*[^0-9a-f-]*'),
    snapshot_id TEXT NOT NULL UNIQUE,
    request_fingerprint_sha256 TEXT NOT NULL
        CHECK(length(request_fingerprint_sha256)=64
          AND request_fingerprint_sha256 NOT GLOB '*[^0-9a-f]*'),
    created_at TEXT NOT NULL CHECK(length(created_at)=33),
    PRIMARY KEY(source_adapter,source_event_id),
    FOREIGN KEY(snapshot_id) REFERENCES skill_invocation_snapshots(snapshot_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
);
CREATE TRIGGER skill_invocation_snapshot_rows_update_rejected
BEFORE UPDATE ON skill_invocation_snapshots
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_rows_delete_rejected
BEFORE DELETE ON skill_invocation_snapshots
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_rows_replacement_rejected
BEFORE INSERT ON skill_invocation_snapshots
WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshots s
 WHERE s.snapshot_id=NEW.snapshot_id
    OR (s.session_id=NEW.session_id AND s.event_id=NEW.event_id)
    OR (NEW.claim_id IS NOT NULL AND s.claim_id=NEW.claim_id)
    OR s.content_item_id=NEW.content_item_id)
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_receipts_update_rejected
BEFORE UPDATE ON skill_invocation_snapshot_receipts
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_receipt_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_receipts_delete_rejected
BEFORE DELETE ON skill_invocation_snapshot_receipts
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_receipt_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_receipts_replacement_rejected
BEFORE INSERT ON skill_invocation_snapshot_receipts
WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshot_receipts r
 WHERE (r.source_adapter=NEW.source_adapter AND r.source_event_id=NEW.source_event_id)
    OR r.snapshot_id=NEW.snapshot_id)
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_receipt_append_only'); END;

CREATE TRIGGER skill_invocation_snapshot_session_event_update_rejected
BEFORE UPDATE ON session_events
WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshots s
            WHERE s.event_id=OLD.event_id)
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_event_immutable'); END;

CREATE TRIGGER skill_invocation_snapshot_session_event_delete_rejected
BEFORE DELETE ON session_events
WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshots s
            WHERE s.event_id=OLD.event_id)
BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_event_immutable'); END;
