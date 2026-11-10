#!/usr/bin/env python3
"""Deterministic synthetic adversaries and an independent arithmetic oracle for the licensed log.
This script never invokes the implementation being tested. Synthetic expectations are hand sums.
"""
from pathlib import Path
import copy
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests/Fixtures"
COMMIT = "b8e7ff3b08dc0d0a1fa40a2409628fc62841d36d"
EVENT, APPLY = "836045448945472", "836045448945477"
ENTER, EXIT, DEATH, REVIVE = "836045448945489", "836045448945490", "836045448945493", "836045448945494"
DAMAGE, HEAL, AREA, DISCIPLINE = "836045448945501", "836045448945500", "836045448953664", "836045448953665"
P, OTHER = "@Élan#101", "@RosterFirst#202"


def line(source, target, effect, tail="", kind=EVENT, ability="", time="20:00:01.000"):
    return f"[{time}] [{source}] [{target}] [{ability}] [Label {{{kind}}}: Label {{{effect}}}] {tail}".rstrip()


def area(id):
    return line(P, "", id, "(test-server) <v7.0.0b>", AREA, time="20:00:00.000")


def npc(template, spawn=1, hp=100):
    return f"Boss {{{template}}}:{spawn}|(0,0,0,0)|({hp}/100)"


def damage(target, value=100, source=P):
    return line(source, target, DAMAGE, f"({value})", APPLY, "Strike {12}")


def death(target):
    return line(OTHER, target, DEATH)


entries = []


def add(id, lines, expected, notes):
    data = ("\r\n".join(lines) + "\r\n").encode("cp1252")
    path = FIXTURES / "logs" / (id + ".txt")
    path.write_bytes(data)
    entries.append(dict(fixtureId=id, path=f"logs/{id}.txt", kind="synthetic", sha256=hashlib.sha256(data).hexdigest(), byteLength=len(data), encodingProfile="windows-1252",
        filenameDateAnchor="2026-11-01", sourceTimezone="UTC", knownOwnerIdentity="101", ownerBasis="AreaEntered", sourceUrl="local synthetic fixture", commit="", license="project test data", verificationNotes=notes, expected=expected))


def expected(damage=0, effectiveDamage=None, healing=0, effectiveHealing=None, bosses=0, flashpoints=0, operations=0, fullRuns=0, rejected=0, kills=None):
    return dict(damage=damage, effectiveDamage=damage if effectiveDamage is None else effectiveDamage, healing=healing, effectiveHealing=healing if effectiveHealing is None else effectiveHealing, bosses=bosses, flashpoints=flashpoints, operations=operations, fullRuns=fullRuns, rejected=rejected, kills=kills)


group = [line(OTHER,"","16141082698337403481",kind=DISCIPLINE), area("137438989504"), damage(npc("999"),1000), damage(npc("999"),9000,OTHER), line(P,"",ENTER), damage(P,500), damage(npc("999"),2000,"@Élan#101/Companion {888}:12"),
         line(P,"=",HEAL,"(4000 ~2000)",APPLY), line(P,npc("999"),DAMAGE,"(5000 ~3500 kinetic {836045448940873} (1500 absorbed {836045448945511})) <99999>",APPLY),
         line(P,npc("999"),DAMAGE,"(41422 ~0 energy {836045448940874})",APPLY), line(P,"=",HEAL,"(500*)",APPLY), damage(npc("999")), damage(npc("999")),
         death(OTHER),death(P),death(P),line(P,P,REVIVE),death(P),line(P,npc("999"),DAMAGE,"(-1)",APPLY),line(P,"",EXIT,time="20:01:01.000")]
add("group-ownership",group,expected(47622,4700,4500,2500,rejected=1),"Hand sum: 1000 + 5000 + 41422 + 100 + 100 = 47622 damage; effective 1000 + 3500 + 0 + 100 + 100 = 4700. Self/group/companion excluded. Healing 4000+500, effective 2000+500. Two personal lives end; one invalid negative record.")

hammer=[area("833571547775682"),line(P,"",ENTER)]
for template in ["3152170987814912","3152166692847616","3152162397880320","3152158102913024","3172554902601728"]:
    hammer += [damage(npc(template)),death(npc(template,hp=0))]
hammer += [death(npc("3172554902601728",hp=0)),damage(npc("3172554902601728",2)),death(npc("3172554902601728",2,0)),line(P,"",EXIT,time="20:05:00.000")]
add("hammer-repeat",hammer,expected(600,bosses=4,flashpoints=2,fullRuns=1),"Six 100-damage events. Three required encounter groups in first run; Vorgan's three roles are one encounter. New terminal spawn is a separate partial run. Duplicate death contributes nothing.")

paired=[area("833571547775669"),line(P,"",ENTER),damage(npc("2760500035190784")),death(npc("2760500035190784",hp=0)),line(P,"",EXIT),line(P,"",ENTER),damage(npc("2760500035190784",2)),damage(npc("2760504330158080",2)),death(P),death(npc("2760504330158080",2,0)),death(npc("2760500035190784",2,0)),line(P,"",EXIT)]
add("paired-wipe-retry",paired,expected(300,bosses=1),"Initial Jarg role death cannot combine with retry. Retry has both roles and local participation before local death. No operation terminal.")
soa=[area("833571547775670"),line(P,"",ENTER),damage(npc("2017170045272064")),death(npc("2017170045272064",hp=0)),line(P,"",EXIT)]
add("soa-terminal",soa,expected(100,bosses=1,operations=1),"One terminal encounter, no EV puzzle/full-run assertion.")
karagga=[area("833571547775669"),line(P,"",ENTER),damage(npc("2761200114860032")),death(npc("2761200114860032",hp=0)),line(P,"",EXIT)]
add("karagga-terminal",karagga,expected(100,bosses=1,operations=1),"Final-boss-only KP join is a terminal clear, not a full run.")
negative=[area("137438993410"),line(P,"",ENTER),damage(npc("3273941900591104")),line(npc("3273941900591104",hp=0),P,"123",kind=APPLY),line(npc("3273941900591104",hp=80),P,"123",kind=APPLY),line(P,"",EXIT)]
add("bestia-reset-negative",negative,expected(100),"Positive HP after zero contradicts the terminal candidate before closing exit; no boss win.")

ordinary=[area("137438989504"),line(P,"",ENTER),
          damage(npc("999",1,49),51),death(npc("999",1,0)),death(npc("999",1,0)),
          damage(npc("999",2,50),50),death(npc("999",2,0)),
          line(P,npc("999",3,0),DAMAGE,"(900 ~49)",APPLY),death(npc("999",3,0)),
          damage(npc("999",4,70),30),damage(npc("999",4,0),70,"@Élan#101/Companion {888}:12"),death(npc("999",4,0)),
          damage(npc("999",5,70),30),damage(npc("999",5,40),30),damage(npc("999",5,0),40,OTHER),death(npc("999",5,0)),
          damage(npc("999",6,0),100),line(P,"",EXIT)]
add("ordinary-kills",ordinary,expected(1191,340,kills=2),"Hand sums: raw 51+50+900+30+30+30+100=1191; effective 51+50+49+30+30+30+100=340. Only spawns 1 and 5 exceed half of max HP and have a death. Exactly half, overkill, companion damage, duplicate death, and zero HP without death do not add kills.")

boarding=[area("833571547775680"),line(P,"",ENTER)]
for template in ["745340034613248","1004464001515520","739816706670592","1005417484255232","957850721452032","957597318381568","739825296605184"]:
    boarding += [damage(npc(template)),death(npc(template,hp=0))]
boarding += [death(npc("739825296605184",hp=0)),line(P,"",EXIT)]
add("boarding-group",boarding,expected(700,bosses=5,flashpoints=1,fullRuns=1,kills=7),"Five encounters, including two minibosses. Storm Squad requires all three deaths and counts once. Other player lands every final blow; local damage proves participation. Duplicate death is ignored.")
partial=[area("833571547775680"),line(P,"",ENTER),damage(npc("957850721452032")),death(npc("957850721452032",hp=0)),damage(npc("957597318381568")),death(npc("957597318381568",hp=0)),line(P,"",EXIT),line(P,"",ENTER),damage(npc("739825296605184",2)),death(npc("739825296605184",2,0)),line(P,"",EXIT)]
add("boarding-wipe",partial,expected(300,kills=3),"A partial Storm Squad kill followed by a new combat cannot combine deaths across attempts.")

# Independent, deliberately simple oracle. Known owner and exact field positions are reviewed,
# not inferred from damage size. This computes amounts only, never encounter expectations.
data=(FIXTURES/"logs/bestia_complete_pull.txt").read_bytes()
totals={"damage":0,"effectiveDamage":0,"healing":0,"effectiveHealing":0}
for record in data.decode("cp1252").splitlines():
    fields=re.findall(r"\[([^\]]*)\]",record)
    if len(fields)!=5 or not fields[1].startswith("@Pilot-1#1100001|"):
        continue
    match=re.match(r"\s*\((\d+)\*?(?:\s+~(\d+))?",record[record.rfind(']')+1:])
    if not match or "{"+APPLY+"}" not in fields[4]:continue
    raw=int(match[1]);effective=int(match[2]) if match[2] is not None else raw
    if "{"+DAMAGE+"}" in fields[4] and fields[2]!="=" and not fields[2].startswith("@Pilot-1#1100001|"):
        totals["damage"]+=raw;totals["effectiveDamage"]+=effective
    if "{"+HEAL+"}" in fields[4]:totals["healing"]+=raw;totals["effectiveHealing"]+=effective
entries.append(dict(fixtureId="bestia-real",path="logs/bestia_complete_pull.txt",kind="upstream_licensed",sha256=hashlib.sha256(data).hexdigest(),byteLength=len(data),encodingProfile="windows-1252",filenameDateAnchor="2026-11-01",sourceTimezone="UTC",knownOwnerIdentity="1100001",ownerBasis="Local EnterCombat; inspected roster order",sourceUrl=f"https://github.com/baras-app/baras/blob/{COMMIT}/test-log-files/fixtures/bestia_complete_pull.txt",commit=COMMIT,license="MIT; BARAS notice in THIRD_PARTY_NOTICES.txt",verificationNotes="Original SHA256 65a7979c71abacb7d2a6e7b40b5b5ae1b2b53e898f9ee6257c73c2a81f0de6ca. Names/player IDs anonymized consistently; bytes/offsets regenerated. Date/UTC are explicit test anchors, not claims about original recording date. Independent Python amount oracle; observed same Bestia spawn alive then zero, no Death-target Bestia, local closing ExitCombat. Scoped inferred boss result; no terminal operation clear.",expected=expected(**totals,bosses=1)))
(FIXTURES/"manifest.json").write_text(json.dumps(dict(version=1,fixtures=entries),indent=2)+"\n")
(FIXTURES/"dashboard-year.json").write_text(json.dumps(dict(kind="synthetic",year=2026,description="Two synthetic sessions per month, two characters; seeded through the real engine."),indent=2)+"\n")

catalog_path=ROOT/"data/catalog/catalog.json"
catalog=json.loads(catalog_path.read_text())
for activity in catalog["activities"]:
    variants=[]
    for encounter in activity["encounters"]:
        if (activity["id"]=="hammer_station" and not encounter["optional"] or
            activity["id"]=="karaggas_palace" and encounter["id"] in ["jarg_sorno","karagga"] or
            activity["id"]=="eternity_vault" and encounter["id"]=="soa"):
            encounter["status"]="logic-tested"
            encounter["positiveFixtures"]=["hammer-repeat" if activity["id"]=="hammer_station" else "soa-terminal" if activity["id"]=="eternity_vault" else "paired-wipe-retry" if encounter["id"]=="jarg_sorno" else "karagga-terminal"]
            encounter["negativeFixtures"]=["paired-wipe-retry"]
        if activity["id"]=="the_dread_palace" and encounter["id"]=="bestia":
            # Retain the untested identifiers as a separate, non-counting candidate.
            variant=copy.deepcopy(encounter)
            variant["roles"]=[dict(role,templateIds=[v for v in role["templateIds"] if v!="3273941900591104"]) for role in variant["roles"] if role["required"]]
            variant["roles"]=[r for r in variant["roles"] if r["templateIds"]]
            if variant["roles"]:
                variant.update(id="bestia_unverified",name="Dread Master Bestia (unverified variants)",rule="unsupported",status="candidate",positiveFixtures=[],negativeFixtures=[])
                variants.append(variant)
            # Only the variant actually present in the licensed fixture is promoted.
            for role in encounter["roles"]:
                if role["required"]:role["templateIds"]=["3273941900591104"]
            encounter.update(rule="hp_zero_exit",status="terminal-tested",positiveFixtures=["bestia-real"],negativeFixtures=["bestia-reset-negative"])
    if activity["id"]=="boarding_party":
        for encounter in activity["encounters"]:
            for role in encounter["roles"]: role["required"]=True
            encounter.update(rule="death_all",status="logic-tested",positiveFixtures=["boarding-group"],negativeFixtures=["boarding-wipe"])
            encounter["optional"]=encounter["id"]=="chief_engineer_kels"
        activity["requiredGroups"]=[[v] for v in ["hxi_54_juggernaut","major_alvena","sakan_donair","lieutenant_menerus","storm_squad"]]
        activity["completeObjectives"]=True
    activity["encounters"].extend(variants)
catalog["version"]="2026.11.09.2"
catalog_path.write_text(json.dumps(catalog,indent=2)+"\n")
print(json.dumps(totals))
