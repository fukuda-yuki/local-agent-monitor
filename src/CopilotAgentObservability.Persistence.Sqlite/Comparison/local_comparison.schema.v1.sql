CREATE TABLE local_comparison_snapshots(
  comparison_id TEXT COLLATE BINARY PRIMARY KEY
    CHECK(typeof(comparison_id)='text'
      AND length(comparison_id)=36
      AND comparison_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND comparison_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(comparison_id,'-',''))=32),
  repository_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(repository_id)='text'
      AND length(repository_id)=36
      AND repository_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND repository_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(repository_id,'-',''))=32),
  created_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(created_at)='text'
      AND length(created_at)=33
      AND created_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'),
  expires_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(expires_at)='text'
      AND length(expires_at)=33
      AND expires_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND expires_at>created_at),
  selection_frame BLOB NOT NULL
    CHECK(typeof(selection_frame)='blob' AND length(selection_frame) BETWEEN 1 AND 16384),
  selection_sha256 TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(selection_sha256)='text' AND length(selection_sha256)=64
      AND selection_sha256 NOT GLOB '*[^0-9a-f]*'),
  scope_condition_sha256 BLOB NOT NULL
    CHECK(typeof(scope_condition_sha256)='blob' AND length(scope_condition_sha256)=32),
  FOREIGN KEY(repository_id) REFERENCES local_repositories(repository_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE TABLE local_comparison_cohort_memberships(
  comparison_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(comparison_id)='text'
      AND length(comparison_id)=36
      AND comparison_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND comparison_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(comparison_id,'-',''))=32),
  cohort TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(cohort)='text' AND cohort IN ('a','b')),
  ordinal INTEGER NOT NULL
    CHECK(typeof(ordinal)='integer' AND ordinal BETWEEN 0 AND 199),
  session_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(session_id)='text'
      AND length(session_id)=36
      AND session_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND session_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(session_id,'-',''))=32),
  workspace_revision TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(workspace_revision)='text' AND length(workspace_revision)=64
      AND workspace_revision NOT GLOB '*[^0-9a-f]*'),
  fact_frame BLOB NOT NULL
    CHECK(typeof(fact_frame)='blob' AND length(fact_frame) BETWEEN 1 AND 1048576),
  fact_sha256 TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(fact_sha256)='text' AND length(fact_sha256)=64
      AND fact_sha256 NOT GLOB '*[^0-9a-f]*'),
  PRIMARY KEY(comparison_id,cohort,ordinal),
  FOREIGN KEY(comparison_id) REFERENCES local_comparison_snapshots(comparison_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE TABLE local_comparison_results(
  comparison_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(comparison_id)='text'
      AND length(comparison_id)=36
      AND comparison_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND comparison_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(comparison_id,'-',''))=32),
  result_ordinal INTEGER NOT NULL
    CHECK(typeof(result_ordinal)='integer' AND result_ordinal BETWEEN 0 AND 1000000),
  section_ordinal INTEGER NOT NULL
    CHECK(typeof(section_ordinal)='integer' AND section_ordinal BETWEEN 0 AND 9),
  row_kind TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(row_kind)='text'
      AND row_kind IN ('receipt','scalar','skill','tool','subagent','condition')),
  row_key TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(row_key)='text' AND length(CAST(row_key AS BLOB)) BETWEEN 1 AND 256),
  payload BLOB NOT NULL
    CHECK(typeof(payload)='blob' AND length(payload) BETWEEN 1 AND 1048576),
  payload_sha256 TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(payload_sha256)='text' AND length(payload_sha256)=64
      AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
  PRIMARY KEY(comparison_id,result_ordinal),
  UNIQUE(comparison_id,section_ordinal,row_kind,row_key),
  CHECK((result_ordinal=0 AND section_ordinal=0 AND row_kind='receipt'
      AND row_key='comparison_receipt')
    OR (result_ordinal>0 AND section_ordinal BETWEEN 1 AND 9
      AND row_kind<>'receipt')),
  FOREIGN KEY(comparison_id) REFERENCES local_comparison_snapshots(comparison_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE TABLE local_comparison_evidence(
  comparison_id TEXT COLLATE BINARY NOT NULL,
  result_ordinal INTEGER NOT NULL
    CHECK(typeof(result_ordinal)='integer' AND result_ordinal BETWEEN 1 AND 1000000),
  evidence_ordinal INTEGER NOT NULL
    CHECK(typeof(evidence_ordinal)='integer' AND evidence_ordinal BETWEEN 0 AND 1000000),
  field_key TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(field_key)='text' AND length(CAST(field_key AS BLOB)) BETWEEN 1 AND 128
      AND field_key NOT GLOB '*[^a-z0-9_.:-]*'),
  cohort TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(cohort)='text' AND cohort IN ('a','b')),
  session_id TEXT COLLATE BINARY NOT NULL,
  availability_state TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(availability_state)='text' AND availability_state IN (
      'recorded','explicit_zero','not_observed','source_unsupported','capture_gap',
      'certification_pending','not_captured','expired','deleted','read_denied',
      'inconsistent','projection_invalid','too_large')),
  consumed_value TEXT COLLATE BINARY NULL
    CHECK(consumed_value IS NULL OR (typeof(consumed_value)='text'
      AND length(consumed_value) BETWEEN 1 AND 200)),
  source_kind TEXT COLLATE BINARY NULL
    CHECK(source_kind IS NULL OR (typeof(source_kind)='text'
      AND source_kind IN ('workspace_session','workspace_node','session_run','session_event','otel_span','skill_claim'))),
  source_identity TEXT COLLATE BINARY NULL
    CHECK(source_identity IS NULL OR (typeof(source_identity)='text'
      AND length(CAST(source_identity AS BLOB)) BETWEEN 1 AND 128
      AND source_identity NOT GLOB '*[^a-z0-9:.-]*')),
  trace_id TEXT COLLATE BINARY NULL
    CHECK(trace_id IS NULL OR (typeof(trace_id)='text' AND length(trace_id)=32
      AND trace_id NOT GLOB '*[^0-9a-f]*')),
  span_id TEXT COLLATE BINARY NULL
    CHECK(span_id IS NULL OR (typeof(span_id)='text' AND length(span_id)=16
      AND span_id NOT GLOB '*[^0-9a-f]*')),
  event_id TEXT COLLATE BINARY NULL
    CHECK(event_id IS NULL OR (typeof(event_id)='text'
      AND length(event_id)=36
      AND event_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND event_id NOT GLOB '*[^0-9a-f-]*')),
  revision_sha256 TEXT COLLATE BINARY NULL
    CHECK(revision_sha256 IS NULL OR (typeof(revision_sha256)='text'
      AND length(revision_sha256)=64 AND revision_sha256 NOT GLOB '*[^0-9a-f]*')),
  PRIMARY KEY(comparison_id,result_ordinal,evidence_ordinal),
  CHECK((trace_id IS NULL)=(span_id IS NULL)),
  CHECK((source_kind IS NULL AND source_identity IS NULL AND trace_id IS NULL
      AND span_id IS NULL AND event_id IS NULL AND revision_sha256 IS NULL)
    OR (source_kind IS NOT NULL AND revision_sha256 IS NOT NULL
      AND (source_identity IS NOT NULL OR trace_id IS NOT NULL OR event_id IS NOT NULL))),
  FOREIGN KEY(comparison_id,result_ordinal)
    REFERENCES local_comparison_results(comparison_id,result_ordinal)
    ON UPDATE RESTRICT ON DELETE RESTRICT,
  FOREIGN KEY(comparison_id,session_id)
    REFERENCES local_comparison_cohort_memberships(comparison_id,session_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE TABLE local_comparison_expiry_tombstones(
  comparison_id TEXT COLLATE BINARY PRIMARY KEY
    CHECK(typeof(comparison_id)='text'
      AND length(comparison_id)=36
      AND comparison_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND comparison_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(comparison_id,'-',''))=32),
  repository_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(repository_id)='text'
      AND length(repository_id)=36
      AND repository_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND repository_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(repository_id,'-',''))=32),
  expired_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(expired_at)='text'
      AND length(expired_at)=33
      AND expired_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00')
);

CREATE UNIQUE INDEX UX_local_comparison_membership_session
  ON local_comparison_cohort_memberships(comparison_id,session_id);

CREATE INDEX IX_local_comparison_snapshots_expiry
  ON local_comparison_snapshots(expires_at,comparison_id);

CREATE INDEX IX_local_comparison_evidence_session
  ON local_comparison_evidence(comparison_id,cohort,session_id,result_ordinal,evidence_ordinal);

CREATE TRIGGER local_comparison_snapshots_insert_replacement_rejected
BEFORE INSERT ON local_comparison_snapshots
WHEN EXISTS(SELECT 1 FROM local_comparison_snapshots WHERE comparison_id=NEW.comparison_id)
  OR EXISTS(SELECT 1 FROM local_comparison_expiry_tombstones WHERE comparison_id=NEW.comparison_id)
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_snapshots_update_rejected
BEFORE UPDATE ON local_comparison_snapshots
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_snapshots_delete_rejected
BEFORE DELETE ON local_comparison_snapshots
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_cohort_memberships_insert_replacement_rejected
BEFORE INSERT ON local_comparison_cohort_memberships
WHEN EXISTS(SELECT 1 FROM local_comparison_snapshots WHERE comparison_id=NEW.comparison_id)
  OR EXISTS(
  SELECT 1 FROM local_comparison_cohort_memberships
  WHERE comparison_id=NEW.comparison_id
    AND (cohort=NEW.cohort AND ordinal=NEW.ordinal OR session_id=NEW.session_id))
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_cohort_memberships_update_rejected
BEFORE UPDATE ON local_comparison_cohort_memberships
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_cohort_memberships_delete_rejected
BEFORE DELETE ON local_comparison_cohort_memberships
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_results_insert_replacement_rejected
BEFORE INSERT ON local_comparison_results
WHEN EXISTS(SELECT 1 FROM local_comparison_snapshots WHERE comparison_id=NEW.comparison_id)
  OR EXISTS(
  SELECT 1 FROM local_comparison_results
  WHERE comparison_id=NEW.comparison_id
    AND (result_ordinal=NEW.result_ordinal
      OR section_ordinal=NEW.section_ordinal AND row_kind=NEW.row_kind AND row_key=NEW.row_key))
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_results_update_rejected
BEFORE UPDATE ON local_comparison_results
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_results_delete_rejected
BEFORE DELETE ON local_comparison_results
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_evidence_insert_replacement_rejected
BEFORE INSERT ON local_comparison_evidence
WHEN EXISTS(SELECT 1 FROM local_comparison_snapshots WHERE comparison_id=NEW.comparison_id)
  OR EXISTS(
  SELECT 1 FROM local_comparison_evidence
  WHERE comparison_id=NEW.comparison_id
    AND result_ordinal=NEW.result_ordinal AND evidence_ordinal=NEW.evidence_ordinal)
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_evidence_update_rejected
BEFORE UPDATE ON local_comparison_evidence
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_evidence_delete_rejected
BEFORE DELETE ON local_comparison_evidence
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_expiry_tombstones_insert_replacement_rejected
BEFORE INSERT ON local_comparison_expiry_tombstones
WHEN EXISTS(SELECT 1 FROM local_comparison_expiry_tombstones WHERE comparison_id=NEW.comparison_id)
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_expiry_tombstones_update_rejected
BEFORE UPDATE ON local_comparison_expiry_tombstones
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;

CREATE TRIGGER local_comparison_expiry_tombstones_delete_rejected
BEFORE DELETE ON local_comparison_expiry_tombstones
BEGIN SELECT RAISE(ABORT,'local_comparison_immutable'); END;
