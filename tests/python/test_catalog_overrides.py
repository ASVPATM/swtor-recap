import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import catalog_overrides


class CatalogOverridesTests(unittest.TestCase):
    def test_corrections_survive_regeneration_and_repeated_application(self):
        catalog = json.loads((Path(__file__).resolve().parents[2] / "data/catalog/catalog.json").read_text())
        roles = {(a["id"], e["id"], r["id"]): r for a in catalog["activities"] for e in a["encounters"] for r in e["roles"]}
        chak = roles["kaon_under_siege", "rakghoul_super_soldiers", "role-1"]
        hound = roles["mandalorian_raiders", "braxx_the_bloodhound", "role-1"]
        droid = roles["cademimu", "officer_xander_en4c", "role-1"]
        chak["templateIds"] = ["2762269561716736"] * 2
        hound.pop("count", None)
        droid.pop("notRequiredDifficulties", None)
        catalog_overrides.apply(catalog)
        self.assertEqual(["2762269561716736", "2762140712697856"], chak["templateIds"])
        self.assertEqual(2, hound["count"])
        self.assertEqual(["836045448953657"], droid["notRequiredDifficulties"])
        expected = copy.deepcopy(catalog)
        self.assertEqual(expected, catalog_overrides.apply(catalog))
        self.assertEqual(catalog_overrides.VERSION, catalog["version"])

    def test_expanded_rules_rosters_and_phases_survive_an_upstream_style_import(self):
        catalog = json.loads((Path(__file__).resolve().parents[2] / "data/catalog/catalog.json").read_text())
        activities = {a["id"]: a for a in catalog["activities"]}
        for a in activities.values():
            a.update(requiredGroups=[], completeObjectives=False)
            a["encounters"] = [e for e in a["encounters"] if e["id"] != "x37_oppressor"]
            for e in a["encounters"]:
                for field in ("ending", "victorySignals", "continueAfterRoles"): e.pop(field, None)
                if e["rule"] in ("hp_threshold_exit", "signal_exit"): e["rule"] = "unsupported"
                for r in e["roles"]: r.pop("countsByGroupSize", None)
        for aid, names in catalog_overrides.DEATH_ENCOUNTERS.items():
            for e in activities[aid]["encounters"]:
                if e["id"] in names.split():
                    e["rule"] = "unsupported"
                    for r in e["roles"]: r["required"] = False
        catalog_overrides.apply(catalog)
        def encounter(aid, eid): return next(e for e in activities[aid]["encounters"] if e["id"] == eid)
        self.assertEqual("hp_threshold_exit", encounter("foundry", "mysterious_jedi")["rule"])
        self.assertEqual("signal_exit", encounter("dxun", "holding_pens")["rule"])
        self.assertEqual(8, encounter("eternity_vault", "infernal_council")["roles"][0]["countsByGroupSize"]["8"])
        self.assertFalse(encounter("lost_island", "doctor_lorrick")["roles"][0]["required"])
        self.assertEqual("x37_oppressor", activities["maelstrom_prison"]["requiredGroups"][0][0])
        self.assertTrue(activities["maelstrom_prison"]["completeObjectives"])
        for a in activities.values():
            for group in a["requiredGroups"]:
                for eid in group: self.assertFalse(encounter(a["id"], eid)["optional"])
        expected = copy.deepcopy(catalog)
        self.assertEqual(expected, catalog_overrides.apply(catalog))
