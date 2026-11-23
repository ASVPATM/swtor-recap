"""Reviewed corrections retained when the pinned upstream catalog is regenerated."""

VERSION = "2026.11.23.1"

# Reviewed ordinary death encounters. Do not infer victory support from BARAS's
# is_boss flag: phase actors, surrender bosses and event objectives use it too.
DEATH_ENCOUNTERS = {
    "assault_on_tython": "imperial_commander",
    "battle_of_ilum": "crystal_droids commander_folex",
    "colicoid_war_game": "annihilator_6k_a2",
    "directive_seven": "assembler replicator",
    "false_emperor": "tregg_the_destroyer prototypes_a14_b16 chondrus_berani",
    "foundry": "n4_10_exterminator",
    "korriban_incursion": "korriban_imperial_commander korriban_republic_commander",
    "kuat_drive_yards": "lieutenant_krupp major_benes lord_modo master_khoris station_guardian_one",
    "lost_island": "doctor_lorrick",
    "maelstrom_prison": "lord_kancras lord_vanithrast",
    "mandalorian_raiders": "imperial_boarding_party republic_boarding_party gil",
    "nathema": "hands_zildrog",
    "objective_meridian": "vulture_squad feng_yun r10-x6 jakir_and_seldin",
    "ruins_of_nul": "warden_units",
    "secrets_of_the_enclave": "graul rep_imperial_squad rep_colonel_barden_golah imp_republic_squad imp_leeha_narezz monstrous_terentatek",
    "shrine_of_silence": "corrupted_vorantikus voss_mystics temu_tfb kirba",
    "spirit_of_vengeance": "gorga_brak bask_sunn troya_ajak goldie",
    "taral_v": "captain_shivanek pd_44 lord_hasper handler_gattan_vine_cats doctor_zharen",
}

# Main boss rosters, not quest turn-ins. '/' means one of the mutually exclusive
# routes suffices; skippable main bosses still belong to a full boss run.
# Reviewed against the named activity guides at https://swtorista.com/flashpoints
# and https://www.merlynswtor.com/operations plus the pinned encounter definitions.
ROSTERS = {
    "a_traitor_among_the_chiss": "guardian_droid syndic_zenta valss",
    "assault_on_tython": "major_travik/major_imos lieutenant_kreshin/master_liam_dentiri lord_goh/master_oric_traless",
    "athiss": "leyarsha vodal_beast vodal_prophet",
    "battle_of_ilum": "gark_the_indomitable drinda_velasu crystal_droids krel_thak commander_folex darth_serevin",
    "battle_of_rishi": "rarrook_marko obai_vodd shield_walker",
    "black_talon": "gxr_5_sabotage_droid/sergeant_boran commander_ghulil yadira_ban",
    "blood_hunt": "kyramla valk_jos shae",
    "boarding_party": "hxi_54_juggernaut major_alvena sakan_donair lieutenant_menerus storm_squad",
    "cademimu": "officer_xander_en4c captain_grimyk general_ortol",
    "colicoid_war_game": "annihilator_6k_a2",
    "crisis_on_umbara": "technician_canni_elli_vaa vixian_mauler umbaran_spider_tank",
    "czerka_core": "vrblther duneclaw vigilant",
    "czerka_labs": "eradicator_droid zokar rasmus",
    "directive_seven": "detector assassin_droids interrogator bulwark assembler mentor",
    "esseles": "lieutenant_isric ironfist iss_7_guardian_battledroid vokk",
    "false_emperor": "tregg_the_destroyer jindo_krey prototypes_a14_b16 hk_47 chondrus_berani darth_malgus",
    "foundry": "foundry_guardian hk_47 n4_10_exterminator mysterious_jedi",
    "hammer_station": "dn_314_tunneler vorgan_the_volcano battlelord_kreshan",
    "kaon_under_siege": "rakghoul_behemoth rakghoul_super_soldiers",
    "korriban_incursion": "lord_renning/master_riilna i5_t1/r_9xr darth_soverus/commander_jensyn",
    "kuat_drive_yards": "lieutenant_krupp/major_benes/lord_modo/master_khoris/station_guardian_one",
    "legacy_rakata": "arena rand darok_arkous",
    "lost_island": "putrid_shaclaw lr_5_sentinel_droid project_sav_rak doctor_lorrick",
    "maelstrom_prison": "x37_oppressor colonel_daksh lord_kancras lord_vanithrast grand_moff_kilran",
    "manaan": "sairisi ortuno stivastin",
    "mandalorian_raiders": "braxx_the_bloodhound imperial_boarding_party/republic_boarding_party mavrix_varad",
    "nathema": "voreclaw guardian_droid gemini vin_zil",
    "objective_meridian": "vulture_squad/r10-x6 commander_aster/jakir_and_seldin darth_malgus/tau_idair",
    "red_reaper": "lord_kherus sv_3_eradicator darth_ikoral",
    "ruins_of_nul": "apex_ranphyx regnant nul_darth_malgus",
    "secrets_of_the_enclave": "graul rep_imperial_squad/imp_republic_squad rep_colonel_barden_golah/imp_leeha_narezz",
    "shrine_of_silence": "corrupted_vorantikus voss_mystics temu_tfb",
    "spirit_of_vengeance": "gorga_brak bask_sunn troya_ajak heta_kol",
    "taral_v": "handler_gattan_vine_cats captain_shivanek doctor_zharen pd_44 general_edikar",
    "children_of_nul": "nul_acolyte force_beast last_daughter",
    "dxun": "red holding_pens breach trandos huntmaster apex_vanguard",
    "eternity_vault": "xrr3 gharj pylons infernal_council soa",
    "explosive_conflict": "zorn_toth firebrand_stormcaller colonel_vorgath warlord_kephess",
    "gods_from_the_machine": "TYTT_K a&e_k nahut_k SCYVA_K IZAX_K",
    "hive_of_the_mountain_queen": "mutated_geonosian_queen",
    "karaggas_palace": "bonethrasher jarg_sorno foreman_crusher g4b3 karagga",
    "monolith": "monolith",
    "propagator_core": "propagator_core_xr53",
    "r4_anomaly": "ip_cpt watchdog kanoth dominique",
    "scum_and_villainy": "dashroode titan6 thrasher operations_chief olok cartel_warlords Styrak_k",
    "temple_of_sacrifice": "malaphar sword_squadron underlurker revanite_commanders revan",
    "terror_from_beyond": "writhing_horror dread_guard op_ix kephess_the_undying the_terror_from_beyond",
    "the_dread_fortress": "nefra draxus grobthok corruptor_zero brontes",
    "the_dread_palace": "bestia/bestia_unverified tyrans calphayus raptus dread_council",
    "the_eyeless": "eyeless",
    "the_ravagers": "sparky bulo torque master_blaster coratanni",
    "toborros_courtyard": "golden_fury",
    "xenoanalyst": "xenoanalyst",
}

# These require objective/identity evidence not present in this catalog. Keeping
# their rosters explicit must not turn an incomplete capture into a full run.
INCOMPLETE_ROSTERS = {"colicoid_war_game", "kuat_drive_yards", "eternity_vault", "children_of_nul",
    "kaon_under_siege", "crisis_on_umbara", "directive_seven"}


def apply(catalog):
    activities = {a["id"]: a for a in catalog["activities"]}
    def encounter(activity_id, encounter_id):
        return next(e for e in activities[activity_id]["encounters"] if e["id"] == encounter_id)

    def role(activity_id, encounter_id, role_id):
        return next(r for r in encounter(activity_id, encounter_id)["roles"] if r["id"] == role_id)

    # Missing first Maelstrom boss. Both difficulty IDs are present in the
    # extracted game data: https://swtor.jedipedia.net/en/npc/x-37-oppressor-droid
    # and https://swtor.jedipedia.net/en/npc/x-37-oppressor-droid-2
    if not any(e["id"] == "x37_oppressor" for e in activities["maelstrom_prison"]["encounters"]):
        activities["maelstrom_prison"]["encounters"].insert(0, dict(id="x37_oppressor", name="X-37 Oppressor Droid",
            roles=[dict(id="droid", name="X-37 Oppressor Droid", templateIds=["1006405326733312", "2513685444558848"], required=True)],
            rule="death_all", victoryId=None, final=False, optional=False, status="logic-tested", positiveFixtures=[], negativeFixtures=[]))

    for activity_id, names in DEATH_ENCOUNTERS.items():
        for name in names.split():
            e = encounter(activity_id, name)
            e.update(rule="death_all", status="logic-tested")
            for r in e["roles"]: r["required"] = True

    for activity_id, roster in ROSTERS.items():
        a = activities[activity_id]
        a["requiredGroups"] = [group.split("/") for group in roster.split()]
        a["completeObjectives"] = activity_id not in INCOMPLETE_ROSTERS
        primary = {name for group in a["requiredGroups"] for name in group}
        for e in a["encounters"]:
            e["optional"] = e["id"] not in primary

    # Every Kuat final boss is an alternative ending; the two random scenarios
    # still prevent claiming complete objective coverage.
    for e in activities["kuat_drive_yards"]["encounters"]: e["final"] = True
    encounter("monolith", "monolith")["final"] = True

    role("taral_v", "handler_gattan_vine_cats", "role-1")["count"] = 2
    # Lorrick's human actor is a phase, not the final kill. The template IDs are
    # split by form in Jedipedia's extracted NPC records (doctor-lorrick-2/-6/-7).
    encounter("lost_island", "doctor_lorrick")["roles"] = [
        dict(id="role-0", name="Doctor Lorrick (human)", templateIds=["2794163988856832", "2838582540632064"], required=False),
        dict(id="rakghoul", name="Doctor Lorrick (rakghoul)", templateIds=["2809320928444416", "2819959562436608"], required=True)]
    # These two repeated roles are two boss NPCs, not two separate pulls.
    for r in encounter("nathema", "hands_zildrog")["roles"]: r["required"] = True

    # Council identities: all eight Story NPCs are independently present in the
    # supplied log. Other 8/16-player variants match StarParse's published NPC IDs:
    # https://github.com/Ixale/StarParse/blob/master/starparse-client/src/main/java/com/ixale/starparse/domain/ops/EternityVault.java
    council = encounter("eternity_vault", "infernal_council")
    council["roles"][0].update(required=True, count=16, countsByGroupSize={"8": 8, "16": 16}, templateIds=[
        "1977669231050752", "2098319157362688", "2098323452329984", "1977660641116160", "2098306272460800", "1977664936083456", "2098327747297280", "2098332042264576",
        "2017230174814208", "2289642770530304", "2289647065497600", "2017238764748800", "2289681425235968", "2017243059716096", "2289690015170560", "2289694310137856",
        "2512152141234176", "2512165026136064", "2512169321103360", "2512190795939840", "2512195090907136", "2512238040580096", "2512242335547392", "2512246630514688",
        "2289840339025920", "2289844633993216", "2289848928960512", "2289853223927808", "2289861813862400", "2289878993731584", "2289887583666176", "2289891878633472",
        "2290106626998272", "2290110921965568", "2290123806867456", "2290132396802048", "2290136691769344", "2290140986736640", "2290145281703936", "2290149576671232",
        "2512263810383872", "2512268105351168", "2512272400318464", "2512276695285760", "2512280990253056", "2512285285220352", "2512298170122240", "2512306760056832"])
    council.update(rule="death_all", status="logic-tested")

    def threshold(activity_id, encounter_id, role_id, percent, abilities=None, immune=False):
        e = encounter(activity_id, encounter_id)
        e.update(rule="hp_threshold_exit", status="logic-tested", ending=dict(roleId=role_id, healthPercent=percent,
            abilityIds=abilities or [], requireImmune=immune, windowSeconds=60))
        role(activity_id, encounter_id, role_id)["required"] = True

    threshold("foundry", "mysterious_jedi", "role-0", 5, ["2270006180052992"])
    threshold("objective_meridian", "darth_malgus", "role-0", 2, immune=True)
    threshold("objective_meridian", "tau_idair", "role-0", 2, immune=True)
    threshold("ruins_of_nul", "nul_darth_malgus", "role-0", 2, immune=True)
    # Ruugar also has a non-death ending (self-destruct); requiring his Death
    # would reproduce the missed clear. Pearl's death remains required, and the
    # explicit phase transition preserves it only until Ruugar's phase begins.
    threshold("the_ravagers", "coratanni", "role-1", 10, ["3502391211065344"])
    role("the_ravagers", "coratanni", "role-0")["required"] = False
    role("the_ravagers", "coratanni", "role-2")["required"] = True
    encounter("the_ravagers", "coratanni")["continueAfterRoles"] = ["role-2"]

    # Explicit upstream victory signals, not timer triggers. These are kept
    # separate from ordinary death rules and require participation plus an exit.
    def signals(activity_id, encounter_id, values):
        encounter(activity_id, encounter_id).update(rule="signal_exit", status="logic-tested", victorySignals=values)
    signals("dxun", "holding_pens", [{"kind": "damage", "id": "4258554563264512"}, {"kind": "loot", "id": "4340614208421888"}])
    signals("dxun", "trandos", [{"kind": "remove", "id": "4381085685252434", "roleId": "role-4"},
        {"kind": "loot", "id": "4330074358677504"}, {"kind": "loot", "id": "4408929958232064"}, {"kind": "loot", "id": "4333471677808640"}])
    threshold("terror_from_beyond", "the_terror_from_beyond", "role-1", 4, ["3019211685232640"])
    huntmaster = encounter("dxun", "huntmaster")
    huntmaster.update(rule="death_all", status="logic-tested")
    for r in huntmaster["roles"]: r["required"] = r["id"] == "role-1"
    # According To Plan has multiple waves; one Enforcer death is insufficient.
    activities["dxun"]["completeObjectives"] = False

    # Veteran Kaon logs contain this third final-boss template; upstream repeated its other variant.
    chak = role("kaon_under_siege", "rakghoul_super_soldiers", "role-1")
    chak["templateIds"] = list(dict.fromkeys(chak["templateIds"] + ["2762140712697856"]))
    # Both hounds share a template. The boss and both distinct hounds must die in the same attempt.
    role("mandalorian_raiders", "braxx_the_bloodhound", "role-1")["count"] = 2
    # Veteran ends on Xander's death; Master requires the droid too. Unknown modes stay strict.
    # Observed in the supplied Veteran/Master logs; mechanics also documented by the guide author:
    # https://swtorista.com/articles/swtor-cademimu-flashpoint-guide/#Officer_Xander_and_EN-4C
    role("cademimu", "officer_xander_en4c", "role-1")["notRequiredDifficulties"] = ["836045448953657"]
    catalog["version"] = VERSION
    return catalog
