import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import analyze_logs as review


OWNER = "@PrivatePlayer#101"
OTHER = "@AnotherPrivatePlayer#202"
GUARD = "Guard {999}:1"


def event(time, source, target, semantic, tail="", ability=""):
    return f"[{time}] [{source}] [{target}] [{ability}] [{semantic}] {tail}".rstrip()


def area(owner=OWNER, area_id="888"):
    return event("12:00:00.000", owner, "", f"AreaEntered {{{review.AREA}}}: Test Area {{{area_id}}}", "(server1) <v7.0.0b>")


def hit(value, hp=40, owner=OWNER, target=GUARD, stamp="12:00:01.000"):
    return event(stamp, owner, target+f"|({hp}/100)", f"ApplyEffect {{{review.APPLY}}}: Damage {{{review.DAMAGE}}}", f"({value})", "Strike {11}")


def death(target=GUARD, stamp="12:00:02.000"):
    return event(stamp, OTHER, target+"|(0/100)", f"Event {{{review.EVENT}}}: Death {{{review.DEATH}}}")


def boundary(effect, stamp, owner=OWNER):
    return event(stamp, owner, "", f"Event {{{review.EVENT}}}: Boundary {{{effect}}}")


class AnalysisTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.path = self.root / "combat_2026-11-07_12_00_00_123456.txt"
        self.catalog = {"version": "test", "activities": []}

    def tearDown(self):
        self.temp.cleanup()

    def scan(self, lines, catalog=None):
        self.path.write_text("\r\n".join(lines), encoding="cp1252")
        return review.scan(self.path, "F001", catalog or self.catalog)

    def test_personal_amounts_exclude_party_companion_and_self_damage(self):
        healing = event("12:00:02.000", OWNER, "=", f"ApplyEffect {{{review.APPLY}}}: Heal {{{review.HEAL}}}", "(10 ~0)")
        r, _ = self.scan([area(), hit("100* ~50"), hit(500, owner=OTHER), hit(200, owner=OWNER+"/Medic {55}:1"), hit(30, target=OWNER), healing])
        self.assertEqual((100, 50, 10, 0), tuple(r["independentTotals"][k] for k in ("damage", "effectiveDamage", "healing", "effectiveHealing")))
        self.assertNotIn("PrivatePlayer", json.dumps(r))
        self.assertEqual(1, len(r["characters"]))

    def test_strict_majority_other_final_blow_and_duplicate_death(self):
        r, e = self.scan([area(), hit(50), death(), hit(51, target="Guard {999}:2"), death(target="Guard {999}:2"), death(target="Guard {999}:2")])
        self.assertEqual(1, r["npcContributionCandidates"])
        self.assertEqual("2", e["killCandidates"][0]["spawnId"])

    def test_heal_preceded_by_full_health_record_keeps_candidate_evidence(self):
        activate = event("12:00:02.000", GUARD+"|(100/100)", "=", f"Event {{{review.EVENT}}}: Activate {{836045448945479}}")
        healing = event("12:00:02.001", GUARD+"|(100/100)", "=", f"ApplyEffect {{{review.APPLY}}}: Heal {{{review.HEAL}}}", "(30)")
        r, e = self.scan([area(), hit(30, hp=70), activate, healing, hit(30, hp=70, stamp="12:00:03.000"), death(stamp="12:00:04.000")])
        self.assertEqual(60, e["killCandidates"][0]["effectiveDamage"])
        self.assertIn("contribution_spans_healing", [x["code"] for x in r["findings"]])

    def test_new_combat_does_not_combine_damage_from_a_wipe(self):
        r, _ = self.scan([area(), boundary(review.ENTER, "12:00:00.100"), hit(30), boundary(review.EXIT, "12:00:02.000"), boundary(review.ENTER, "12:00:03.000"), hit(30, stamp="12:00:04.000"), death(stamp="12:00:05.000")])
        self.assertEqual(0, r["npcContributionCandidates"])

    def test_player_death_does_not_erase_group_finish_evidence(self):
        own_death = event("12:00:01.500", OWNER, "=", f"Event {{{review.EVENT}}}: Death {{{review.DEATH}}}")
        r, _ = self.scan([area(), hit(60), own_death, death()])
        self.assertEqual(1, r["independentTotals"]["deaths"])
        self.assertEqual(1, r["npcContributionCandidates"])

    def test_owner_switch_changes_attribution_and_death_state(self):
        own_death = event("12:00:01.500", OWNER, "=", f"Event {{{review.EVENT}}}: Death {{{review.DEATH}}}")
        next_area = area(OTHER).replace("12:00:00.000", "12:00:03.000")
        other_death = own_death.replace(OWNER, OTHER).replace("12:00:01.500", "12:00:05.000")
        r, _ = self.scan([area(), hit(60), own_death, next_area, hit(70, owner=OTHER, stamp="12:00:04.000"), other_death])
        self.assertEqual([60, 70], sorted(c["totals"]["damage"] for c in r["characters"]))
        self.assertEqual(2, r["independentTotals"]["deaths"])

    def test_multiple_live_instances_and_missing_catalog_id_are_flagged(self):
        enc = {"id": "pack", "name": "Pack", "rule": "death_all", "final": True, "roles": [
            {"name": "Guard", "templateIds": ["999"], "required": True},
            {"name": "Captain", "templateIds": ["123"], "required": True}]}
        catalog = {"activities": [{"id": "test", "type": "flashpoint", "areaIds": ["888"], "encounters": [enc]}]}
        r, _ = self.scan([area(), hit(60), hit(60, target="Guard {999}:2"), death(), death(target="Guard {999}:2"), hit(60, target="Captain {456}:3")], catalog)
        codes = {x["code"] for x in r["findings"]}
        self.assertTrue({"catalog_id_candidate", "multiple_role_instances", "encounter_evidence_incomplete"} <= codes)
        match = next(x for x in r["findings"] if x["code"] == "catalog_id_candidate")
        self.assertEqual(["456"], match["ids"])

    def test_midnight_and_backwards_clock_are_distinguished(self):
        r, _ = self.scan([area(), boundary(review.ENTER, "23:59:30.000"), boundary(review.EXIT, "00:00:30.000"), boundary(review.ENTER, "00:00:40.000"), boundary(review.EXIT, "00:00:35.000")])
        self.assertEqual(60000, r["independentTotals"]["combatMilliseconds"])
        self.assertIn("clock_discontinuity", [x["code"] for x in r["findings"]])

    def test_review_respects_catalog_multiplicity_and_known_difficulty(self):
        enc = {"id": "pack", "name": "Pack", "rule": "death_all", "final": True, "roles": [
            {"name": "Guard", "templateIds": ["999"], "required": True, "count": 2},
            {"name": "Droid", "templateIds": ["123"], "required": True, "notRequiredDifficulties": ["777"]}]}
        catalog = {"activities": [{"id": "test", "type": "flashpoint", "areaIds": ["888"], "encounters": [enc]}]}
        veteran = area().replace("Test Area {888}", "Test Area {888} Mode {777}")
        lines = [veteran, hit(60), hit(60, target="Guard {999}:2"), death(), death()]
        r, _ = self.scan(lines, catalog)
        roles = r["visits"][0]["encounters"][0]["roles"]
        self.assertEqual((2, 1, False), (roles[0]["expectedCount"], roles[0]["observedDeaths"], roles[1]["required"]))
        self.assertIn("Guard", r["visits"][0]["encounters"][0]["missingDeathRoles"])
        self.assertNotIn("multiple_role_instances", [f["code"] for f in r["findings"]])
        r, _ = self.scan(lines + [death(target="Guard {999}:2")], catalog)
        self.assertNotIn("encounter_evidence_incomplete", [f["code"] for f in r["findings"]])
        r, _ = self.scan([area()] + lines[1:] + [death(target="Guard {999}:2")], catalog)
        self.assertEqual(["Droid"], r["visits"][0]["encounters"][0]["missingDeathRoles"])

    def test_utf8_bom_bad_encoding_oversized_line_and_sealed_tail(self):
        self.path.write_bytes(b"\xef\xbb\xbf"+area().encode()+b"\r\n\xff\n"+b"x"*70000+b"\n"+hit(60).encode())
        r, _ = review.scan(self.path, "F001", self.catalog)
        self.assertEqual((4, 2, 60), (r["lines"], r["rejected"], r["independentTotals"]["damage"]))
        lines = list(review.records(self.path))
        self.assertEqual(3+len(area().encode())+2, lines[1][1])

    def test_group_size_and_non_death_endings_are_reported_without_false_missing_death_errors(self):
        enc = {"id": "council", "name": "Council", "rule": "death_all", "final": False, "roles": [
            {"name": "Guard", "templateIds": ["999"], "required": True, "count": 16, "countsByGroupSize": {"8": 8, "16": 16}}]}
        ending = {"roleId": "boss", "healthPercent": 5, "abilityIds": ["123"]}
        scripted = {"id": "boss", "name": "Boss", "rule": "hp_threshold_exit", "final": True, "ending": ending,
            "roles": [{"id": "boss", "name": "Boss", "templateIds": ["8888"], "required": True}]}
        catalog = {"activities": [{"id": "test", "type": "operation", "areaIds": ["888"], "encounters": [enc, scripted]}]}
        lines = [area().replace("Test Area {888}", "Test Area {888} 8 Player Story {777}")]
        lines += [line for i in range(8) for line in (hit(60, target=f"Guard {{999}}:{i}"), death(target=f"Guard {{999}}:{i}"))]
        lines.append(hit(60, target="Boss {8888}:1"))
        r, _ = self.scan(lines, catalog)
        self.assertEqual(8, r["visits"][0]["encounters"][0]["roles"][0]["expectedCount"])
        self.assertEqual(ending, r["visits"][0]["encounters"][1]["ending"])
        self.assertIn("scripted_ending_review", [f["code"] for f in r["findings"]])
        self.assertNotIn("encounter_evidence_incomplete", [f["code"] for f in r["findings"]])
        r, _ = self.scan([area()] + lines[1:], catalog)
        self.assertEqual(16, r["visits"][0]["encounters"][0]["roles"][0]["expectedCount"])
        self.assertIn("encounter_evidence_incomplete", [f["code"] for f in r["findings"]])

    def test_unsupported_profile_is_not_presented_as_understood(self):
        r, _ = self.scan([area().replace("v7.0.0b", "v99"), hit(60)])
        self.assertEqual(2, r["rejected"])
        self.assertNotIn("damage", r["independentTotals"])

    def test_brief_is_bounded_but_does_not_claim_exhaustiveness(self):
        r, _ = self.scan([area(), hit(60)])
        r["findings"] = [{"code": "long_code_"+str(i), "note": "n"*2000} for i in range(500)]
        text = review.brief([r], {"catalog": "test", "appStatus": "not run", "duplicates": 0}, 2000)
        self.assertLessEqual(len(text), 2000)
        self.assertIn("truncated", text)

    def test_batch_dedup_preserves_distinct_dates_and_inputs(self):
        self.path.write_text(area()+"\n"+hit(60), encoding="cp1252")
        data = self.path.read_bytes(); before = self.path.stat().st_mtime_ns
        copy = self.root/"copy.txt"; copy.write_bytes(data)
        later = self.root/"combat_2026-11-08_12_00_00_123456.txt"; later.write_bytes(data)
        with contextlib.redirect_stdout(io.StringIO()):
            rc = review.main([str(self.path), str(copy), "--output", str(self.root/"one")])
            review.main([str(self.path), str(later), "--output", str(self.root/"two")])
        self.assertEqual(0, rc)
        first = json.loads(next((self.root/"one").glob("*/index.json")).read_text())
        second = json.loads(next((self.root/"two").glob("*/index.json")).read_text())
        self.assertEqual((1, 1, 2), (len(first["files"]), first["duplicates"], len(second["files"])))
        self.assertEqual(data, self.path.read_bytes()); self.assertEqual(before, self.path.stat().st_mtime_ns)

    def test_excerpt_is_bounded_redacted_and_checks_original_hash(self):
        self.path.write_text(area()+"\n"+hit(60), encoding="cp1252")
        with contextlib.redirect_stdout(io.StringIO()): review.main([str(self.path), "--output", str(self.root/"out")])
        run = next((self.root/"out").glob("run-*")); output = io.StringIO()
        with contextlib.redirect_stdout(output): review.inspect_command(["excerpt", str(run), "F001", "1:2"])
        self.assertNotIn("PrivatePlayer", output.getvalue()); self.assertNotIn("#101", output.getvalue())
        self.assertIn("|(40/100)", output.getvalue())
        self.path.write_text("changed", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "changed"): review.inspect_command(["excerpt", str(run), "F001", "1:2"])
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit): review.inspect_command(["excerpt", str(run), "F001", "1:101"])

    def test_comparison_surfaces_missing_contribution_without_claiming_victory(self):
        r, e = self.scan([area(), hit(60), death()])
        totals = {"amounts": {k: r["independentTotals"].get(k, 0) for k in ("damage", "effectiveDamage", "healing", "effectiveHealing", "events")}, "deaths": 0, "combatMilliseconds": 0}
        app = {"totals": totals, "accepted": 3, "rejected": 0, "unknown": 0, "integrity": "ok", "outcomes": [], "kills": [], "gaps": []}
        review.compare_app(r, e, app)
        self.assertTrue(all(r["appComparison"].values()))
        f = next(x for x in r["findings"] if x["code"] == "contribution_not_counted")
        self.assertEqual(3, f["line"]); self.assertIn("true reset", f["note"])

    def test_failed_app_comparison_retains_evidence_and_returns_failure(self):
        self.path.write_text(area()+"\n"+hit(60), encoding="cp1252")
        with patch.object(review, "run_app", side_effect=OSError("SDK unavailable")), contextlib.redirect_stdout(io.StringIO()):
            rc = review.main([str(self.path), "--compare-app", "--output", str(self.root/"out")])
        run = next((self.root/"out").glob("run-*"))
        self.assertEqual(1, rc); self.assertTrue((run/"F001.json").exists())
        self.assertIn("FAILED", (run/"MODEL_BRIEF.md").read_text())

    def test_empty_input_and_unknown_owner_are_explicit(self):
        r, _ = self.scan([])
        self.assertIn("empty_input", [f["code"] for f in r["findings"]])
        r, _ = self.scan([hit(60)])
        self.assertIn("unresolved_owner", [f["code"] for f in r["findings"]])
        self.assertEqual(0, r["independentTotals"].get("damage", 0))

    def test_unmapped_semantics_are_preserved_and_flagged(self):
        r, e = self.scan([area(), event("12:00:01.000", OWNER, "=", "FutureType {12345}: FutureEvent {45678}")])
        self.assertIn("unknown_semantics", [f["code"] for f in r["findings"]])
        self.assertTrue(any(s["ids"] == "12345:45678" and s["count"] == 1 for s in e["semantics"]))


if __name__ == "__main__": unittest.main()
