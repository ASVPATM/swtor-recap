#!/usr/bin/env python3
"""Normalize a reviewed BARAS snapshot. Timer conditions are deliberately not victory rules."""
import argparse
import json
from pathlib import Path
import tomllib

COMMIT = "b8e7ff3b08dc0d0a1fa40a2409628fc62841d36d"
LAIRS = {"hive_of_the_mountain_queen", "monolith", "propagator_core", "the_eyeless", "toborros_courtyard", "xenoanalyst"}


def build(snapshot):
    activities = []
    for path in sorted((snapshot / "core/definitions/encounters").rglob("*.toml")):
        source = tomllib.loads(path.read_text())
        area = source.get("area", {})
        area_id = area.get("area_id", area.get("id"))
        if area_id is None:
            continue
        activity_id = path.stem
        encounters = []
        for boss in source.get("boss", []):
            roles = []
            for index, entity in enumerate(boss.get("entities", [])):
                if not entity.get("is_boss") and not entity.get("is_kill_target"):
                    continue
                roles.append(dict(id=f"role-{index}", name=entity["name"], templateIds=[str(i) for i in entity["ids"]], required=entity.get("is_kill_target", False)))
            # Broad identifiers remain candidates; missing optional flags never become required.
            optional = bool(boss.get("is_optional", False)) or activity_id == "hammer_station" and boss["id"] == "asteroid_beast"
            rule = "death_all" if any(r["required"] for r in roles) else "unsupported"
            encounters.append(dict(id=boss["id"], name=boss["name"], roles=roles, rule=rule, victoryId=None,
                final=bool(boss.get("is_final_boss", False)), optional=optional, status="candidate", positiveFixtures=[], negativeFixtures=[]))
        # These rosters are independently curated; EV puzzles deliberately remain incomplete.
        rosters = {"hammer_station": ["dn_314_tunneler", "vorgan_the_volcano", "battlelord_kreshan"],
                   "karaggas_palace": ["bonethrasher", "jarg_sorno", "foreman_crusher", "g4b3", "karagga"]}
        kind = "lair" if activity_id in LAIRS else "flashpoint" if path.parent.name == "flashpoints" else "operation" if path.parent.name == "operations" else "other"
        activities.append(dict(id=activity_id, name=area.get("name", activity_id), type=kind, areaIds=[str(area_id)], encounters=encounters,
            requiredGroups=[[v] for v in rosters.get(activity_id, [])], completeObjectives=activity_id in rosters,
            provenance=f"https://github.com/baras-app/baras/blob/{COMMIT}/{path.relative_to(snapshot)}", commit=COMMIT, license="MIT"))
    # Stable dummy ID from the same licensed snapshot; practice deaths are not kills.
    activities.append({'id': 'training_dummy', 'name': 'Training dummy', 'type': 'training', 'areaIds': [], 'encounters': [{'id': 'training_dummy', 'name': 'Training dummy', 'roles': [{'id': 'dummy', 'name': 'Training dummy', 'templateIds': ['2857785339412480'], 'required': False}], 'rule': 'unsupported', 'victoryId': None, 'final': False, 'optional': True, 'status': 'candidate', 'positiveFixtures': [], 'negativeFixtures': []}], 'requiredGroups': [], 'completeObjectives': False, 'provenance': 'https://github.com/baras-app/baras/blob/b8e7ff3b08dc0d0a1fa40a2409628fc62841d36d/core/src/game_data/world_bosses.rs', 'commit': COMMIT, 'license': 'MIT'})
    return dict(version="2026.11.09.1", activities=activities)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--output", type=Path, default=Path("data/catalog/catalog.json"))
    args = parser.parse_args()
    args.output.write_text(json.dumps(build(args.snapshot), indent=2) + "\n")
