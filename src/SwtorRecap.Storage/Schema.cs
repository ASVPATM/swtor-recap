namespace SwtorRecap.Storage;

internal static class Schema
{
    public const string Sql = """
CREATE TABLE app_state(id INTEGER PRIMARY KEY CHECK(id=1), schema_version INTEGER NOT NULL, history_epoch TEXT NOT NULL,
 data_revision INTEGER NOT NULL CHECK(data_revision>=0), settings_revision INTEGER NOT NULL, reporting_zone TEXT NOT NULL,
 settings_json TEXT NOT NULL) STRICT;
CREATE TABLE schema_migrations(id INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_utc TEXT NOT NULL) STRICT;
CREATE TABLE containers(id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('session','import')), state TEXT NOT NULL,
 started_utc TEXT, ended_utc TEXT, closure_reason TEXT, process_key TEXT UNIQUE, last_observed_utc TEXT, timing_quality TEXT NOT NULL) STRICT;
CREATE TABLE source_generations(id TEXT PRIMARY KEY, container_id TEXT NOT NULL REFERENCES containers(id), physical_identity TEXT,
 original_path TEXT NOT NULL, encoding TEXT NOT NULL, date_anchor TEXT, source_zone TEXT NOT NULL,
 next_offset INTEGER NOT NULL DEFAULT 0 CHECK(next_offset>=0), checkpoint_revision INTEGER NOT NULL DEFAULT 0,
 last_length INTEGER NOT NULL, prefix_length INTEGER NOT NULL, prefix_hash TEXT NOT NULL, boundary_start INTEGER NOT NULL DEFAULT 0,
 boundary_hash TEXT NOT NULL DEFAULT '', digest TEXT, parser_version TEXT NOT NULL, integrity TEXT NOT NULL,
 active_projection TEXT NOT NULL, visible INTEGER NOT NULL DEFAULT 1 CHECK(visible IN(0,1)), sealed INTEGER NOT NULL CHECK(sealed IN(0,1)),
 accepted INTEGER NOT NULL DEFAULT 0, rejected INTEGER NOT NULL DEFAULT 0, unknown_count INTEGER NOT NULL DEFAULT 0, context_id TEXT NOT NULL,
 FOREIGN KEY(id,active_projection) REFERENCES projection_sets(source_id,id) DEFERRABLE INITIALLY DEFERRED) STRICT;
CREATE TABLE source_dependencies(prior_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 next_id TEXT NOT NULL UNIQUE REFERENCES source_generations(id) ON DELETE CASCADE, proof TEXT NOT NULL, PRIMARY KEY(prior_id,next_id), CHECK(prior_id<>next_id)) STRICT;
CREATE TABLE source_aliases(source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE, path TEXT NOT NULL, PRIMARY KEY(source_id,path)) STRICT;
CREATE TABLE owner_selections(source_id TEXT PRIMARY KEY REFERENCES source_generations(id) ON DELETE CASCADE, player_id TEXT NOT NULL) STRICT;
CREATE TABLE source_exclusions(source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 start_byte INTEGER NOT NULL CHECK(start_byte>=0), end_byte INTEGER NOT NULL CHECK(end_byte>=start_byte), PRIMARY KEY(source_id,start_byte,end_byte)) STRICT;
CREATE TABLE projection_sets(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 parser_version TEXT NOT NULL, metric_version TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('building','active','retired')), UNIQUE(source_id,id)) STRICT;
CREATE UNIQUE INDEX one_active_projection ON projection_sets(source_id) WHERE state='active';
CREATE TABLE characters(id TEXT PRIMARY KEY, player_key TEXT NOT NULL, server TEXT, name TEXT NOT NULL, quality TEXT NOT NULL) STRICT;
CREATE TABLE character_aliases(character_id TEXT NOT NULL REFERENCES characters(id), name TEXT NOT NULL, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE, PRIMARY KEY(character_id,name,source_id)) STRICT;
CREATE TABLE source_segments(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 start_byte INTEGER NOT NULL CHECK(start_byte>=0), end_byte INTEGER CHECK(end_byte>=start_byte), owner TEXT,
 character_id TEXT REFERENCES characters(id), name TEXT, attribution TEXT NOT NULL, server TEXT, UNIQUE(source_id,start_byte)) STRICT;
CREATE TABLE engine_checkpoint(source_id TEXT PRIMARY KEY REFERENCES source_generations(id) ON DELETE CASCADE,
 revision INTEGER NOT NULL, state_version INTEGER NOT NULL, state_json TEXT NOT NULL) STRICT;
CREATE TABLE metric_buckets(id TEXT PRIMARY KEY, projection_id TEXT NOT NULL REFERENCES projection_sets(id) ON DELETE CASCADE,
 segment_id TEXT NOT NULL REFERENCES source_segments(id) ON DELETE CASCADE, actor_key TEXT NOT NULL, context TEXT NOT NULL,
 local_minute TEXT NOT NULL, utc_minute INTEGER, reporting_date TEXT, quality TEXT NOT NULL, clock_epoch INTEGER NOT NULL,
 damage INTEGER NOT NULL CHECK(damage>=0), effective_damage INTEGER NOT NULL CHECK(effective_damage>=0), damage_unknown INTEGER NOT NULL CHECK(damage_unknown>=0),
 healing INTEGER NOT NULL CHECK(healing>=0), effective_healing INTEGER NOT NULL CHECK(effective_healing>=0), healing_unknown INTEGER NOT NULL CHECK(healing_unknown>=0),
 events INTEGER NOT NULL CHECK(events>=0)) STRICT;
CREATE INDEX bucket_period ON metric_buckets(reporting_date,segment_id,projection_id);
CREATE TRIGGER bucket_source_insert BEFORE INSERT ON metric_buckets WHEN
 (SELECT source_id FROM projection_sets WHERE id=NEW.projection_id) <> (SELECT source_id FROM source_segments WHERE id=NEW.segment_id)
 BEGIN SELECT RAISE(ABORT,'Bucket projection and segment belong to different sources'); END;
CREATE TRIGGER bucket_source_update BEFORE UPDATE OF projection_id,segment_id ON metric_buckets WHEN
 (SELECT source_id FROM projection_sets WHERE id=NEW.projection_id) <> (SELECT source_id FROM source_segments WHERE id=NEW.segment_id)
 BEGIN SELECT RAISE(ABORT,'Bucket projection and segment belong to different sources'); END;
CREATE TABLE encounter_evidence(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL CHECK(end_byte>start_byte), kind TEXT NOT NULL, entity TEXT NOT NULL,
 segment_id TEXT REFERENCES source_segments(id), reporting_date TEXT, utc_time TEXT, quality TEXT NOT NULL, payload TEXT NOT NULL,
 UNIQUE(source_id,start_byte,kind,entity)) STRICT;
CREATE TABLE encounter_attempts(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 segment_id TEXT NOT NULL REFERENCES source_segments(id) ON DELETE CASCADE, run_id TEXT NOT NULL, activity_id TEXT NOT NULL,
 encounter_id TEXT NOT NULL, result TEXT NOT NULL, basis TEXT NOT NULL, qualified INTEGER NOT NULL, reporting_date TEXT, payload TEXT NOT NULL) STRICT;
CREATE TABLE activity_runs(id TEXT PRIMARY KEY, segment_id TEXT NOT NULL REFERENCES source_segments(id) ON DELETE CASCADE,
 activity_id TEXT NOT NULL, terminal INTEGER NOT NULL, full INTEGER NOT NULL, reporting_date TEXT, payload TEXT NOT NULL) STRICT;
CREATE TABLE result_evidence_links(attempt_id TEXT NOT NULL REFERENCES encounter_attempts(id) ON DELETE CASCADE,
 evidence_id TEXT NOT NULL REFERENCES encounter_evidence(id) ON DELETE CASCADE, PRIMARY KEY(attempt_id,evidence_id)) STRICT;
CREATE TABLE coverage_gaps(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_generations(id) ON DELETE CASCADE,
 start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL, dimension TEXT NOT NULL, reason TEXT NOT NULL, recoverable INTEGER NOT NULL) STRICT;
CREATE TABLE session_intervals(id TEXT PRIMARY KEY, container_id TEXT NOT NULL REFERENCES containers(id) ON DELETE CASCADE,
 segment_id TEXT REFERENCES source_segments(id) ON DELETE CASCADE, actor_key TEXT NOT NULL, kind TEXT NOT NULL,
 start_utc TEXT, end_utc TEXT, milliseconds INTEGER NOT NULL CHECK(milliseconds>=0), reporting_date TEXT, quality TEXT NOT NULL) STRICT;
CREATE INDEX interval_period ON session_intervals(reporting_date,kind);
CREATE TABLE jobs(id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL, processed_bytes INTEGER NOT NULL,
 total_bytes INTEGER NOT NULL, message TEXT, request_json TEXT NOT NULL, expected_epoch TEXT NOT NULL, expected_revision INTEGER NOT NULL) STRICT;
CREATE TABLE mutation_receipts(id TEXT PRIMARY KEY, epoch TEXT NOT NULL, result_json TEXT NOT NULL) STRICT;
CREATE VIEW personal_buckets AS SELECT b.*, s.character_id, g.container_id FROM metric_buckets b
 JOIN source_segments s ON s.id=b.segment_id JOIN source_generations g ON g.id=s.source_id
 WHERE b.projection_id=g.active_projection AND b.actor_key=s.owner AND g.visible=1;
""";
}
