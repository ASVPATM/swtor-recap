# SWTOR Recap guide

## Install on Windows

Download either file from [Releases](https://github.com/ASVPATM/swtor-recap/releases):

- **Installer:** run `SwtorRecap-0.3.0-Setup.exe`. It installs for your Windows account and adds **SWTOR Recap** to the Start menu. A desktop shortcut is optional. Uninstall through Windows Settings → Apps.
- **Portable ZIP:** extract the complete `SwtorRecap-win-x64.zip` into a permanent folder, then open `SwtorRecap.exe`. Keep all files and the `agent` folder together. To remove it, disable startup, quit the tracker, and delete its application folder.

Both options contain the same application and .NET runtime. No administrator access, Java, Python, Git, or separate .NET installation is needed to run them. A loading window appears while the tracker opens your history, then the dashboard opens. No command file is needed.

This test build is unsigned. Windows may show a publisher/reputation warning or block it. An installer does not provide publisher verification. If blocked, save the exact message; do not disable Windows security. Compare the download with `SHA256SUMS.txt` using `Get-FileHash` in PowerShell. A matching checksum verifies the file, not the publisher's identity. [Microsoft's signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

## Enable logging

1. In SWTOR, enable **Preferences → Combat Logging → Enable combat logging to file**, then Apply.
2. Open Recap before the next game session. Steam needs no launch options or special configuration; Recap watches `swtor.exe`.
3. The default input folder is `Documents\Star Wars - The Old Republic\CombatLogs`. Windows Documents redirection is supported. If necessary, choose the folder in Recap Settings and save. Do not select the Steam installation folder.
4. Launch SWTOR and fight an enemy. Your character, damage, and combat time should appear in Session after the log updates. Combat time closes when the game logs the end of combat.
5. Leave recording enabled during an activity. Closing the dashboard leaves the tray tracker running. Reopen it from the Start menu, `SwtorRecap.exe`, or the tray.
6. After quitting SWTOR, allow a few seconds for final saving. Session returns to the ready screen; the recording remains in History.

Several activities in one continuous game launch belong to one session. Restarting SWTOR creates another session. **Start with Windows** runs the tray tracker at sign-in. **Exit tracker after the next game session** stops it after that session ends.

## Replace an earlier version

1. Close SWTOR. In the old Recap, export a history backup from Settings.
2. Disable **Start with Windows** if enabled. Choose **Quit tracker** from the tray, then close the dashboard.
3. Run the new installer, or extract the new ZIP into a separate folder. Keep the old portable folder until the replacement works. Do not mix DLLs from different versions.
4. Open **SWTOR Recap** from the Start menu if installed, or the new `SwtorRecap.exe`. Both use your existing `%LOCALAPPDATA%\SwtorRecap` history. Do not delete that data folder.
5. With SWTOR closed and the original combat logs available, use **Settings → Reprocess saved history → Reprocess saved logs**. This applies the current kill and encounter rules to older recordings. Check the job result before playing.
6. Re-enable **Start with Windows** if wanted, then launch SWTOR normally. Remove old application shortcuts to avoid opening the previous version.

New recordings count kills immediately. A `+` beside older kill totals means those recordings still need reprocessing. Reprocessing replaces results; it does not add another copy. Paused ranges stay excluded. If original logs are missing, retain the saved history and import/reprocess only when the files are available.

## What is counted

- **Damage dealt:** your character's raw logged damage, excluding self-damage, companion damage, and party damage.
- **Healing done:** your character's raw logged healing, including self-heals, periodic/passive effects attributed to you, and overhealing. Companion and incoming party healing are excluded. Unlogged regeneration and shielding are not estimated.
- **Effective amounts:** the effective damage/healing values supplied by supported log records. These are separate from raw totals in recording details. Unknown effective values remain unknown.
- **NPC kills:** an NPC's recorded death after your character deals **strictly more than half of its logged maximum health** in effective damage. For a 100,000-health enemy, 50,000 does not qualify; 50,001 does. The final hit can come from a companion or another player, including after you die. Boss targets count individually under the same rule. Overkill does not increase effective damage. Damage across known resets or recording interruptions is not combined. If the enemy heals, the threshold still uses its maximum health, not the total damage dealt by the group.
- **Kill exclusions:** player/companion targets, known training dummies, unknown or changing maximum health, and deaths not observed in the log. Zero HP alone is insufficient. Individual boss kills are separate from encounter wins. This is Recap's contribution rule, not the game's quest or official kill-credit system.
- **Player deaths:** logged deaths of the identified local character.
- **Combat time:** the union of your supported combat intervals. Missing boundaries leave partial coverage.
- **Game-open time:** observed awake time while SWTOR runs, including AFK time. Sleep and recording pauses are excluded. Imports cannot reconstruct past game-open time.
- **Boss wins:** participation in a supported encounter followed by supported victory evidence. Being alive at the end or landing the final blow is not required. Healing a participant during the fight can establish participation.
- **Activity clears:** a supported final-encounter win. Conversations, holoterminal objectives, rewards, and quest turn-ins are separate.
- **Full runs:** all required supported encounters/objectives observed without a recording gap.

These are totals, not DPS/HPS. General combat metrics and NPC kills work independently of the boss catalog. Missing log events can leave kills or encounters uncounted.

## Updates

1. Close SWTOR and finish or cancel imports.
2. Open **Settings → Check for updates**.
3. If a newer stable version is available, choose **Download, install and reopen**. Downloading can be cancelled.
4. Recap verifies the file, saves a verified history backup, closes the tracker, runs the installer, and reopens. Windows may show its normal security prompt.
5. If older counts need updating, use **Reprocess saved history** with the original logs available.

The updater uses only `ASVPATM/swtor-recap` on GitHub. It checks release versions, refuses drafts/prereleases/downgrades, verifies the asset size and GitHub's SHA-256 digest, restricts download redirects to GitHub asset hosts, and verifies the file again before installation. Logs, history and credentials are never uploaded. Network requests occur only when you check or download.

The current build is unsigned. A GitHub checksum checks download integrity; it is not publisher verification. The updater keeps Windows security checks enabled. Installer failures leave history and the recovery backup in place; reinstall the latest version if application files need repair. There is no automatic database downgrade or destructive restore.

For a portable copy, the updater installs to your Windows user account and adds a Start menu shortcut. The old portable folder stays in place; use the new Start menu shortcut. An existing managed installation keeps its installation directory. A matching startup entry is moved to the installed tracker.

While the repository is private, anonymous update checks report that no public stable release is available. Install the supplied preview manually. No GitHub token is required or stored. After public publication, this same updater uses the stable releases without a configuration change.

## Character switching

Logging into another character does not start a new session while the same SWTOR process is running. Area-entry records identify the character; group roster order and damage rankings do not select the owner. Returning to a previous character adds to that character's totals. Characters use player ID and server identity, so a name alone does not merge them.

Session totals combine your characters. The current-character label follows login/area evidence. History, recording details, and monthly/yearly Recap offer **All characters** or a specific character. Bosses, clears, kills, deaths, damage, healing, and logged combat time follow that filter. Game-open time remains a session measurement because character-selection and loading time cannot be reliably assigned to one character.

## Encounter coverage

The catalog currently contains 175 encounter definitions with explicit death-target rules. These use the same tested counting logic: match the area and NPC IDs, establish local participation, and observe the required targets die in the same attempt. Another player can land every final blow. A recorded death after your own death still counts. Knowing an encounter's name alone does not prove its ending.

Multi-target encounters count once after their required targets are defeated. Individual kills are separate: dealing over 50% of two targets' maximum health in a three-target boss can produce **2 NPC kills and 1 boss encounter win**. Exactly 50% does not qualify. Participation below 50% can still earn the encounter win and clear.

Hammer Station requires its three main encounters for a full run. Boarding Party groups Massey, Alon and Jorland as one final encounter; its full-run roster also includes HXI-54, Major Alvena, Sakan Do'nair and Lieutenant Menerus. A final clear can count even when an earlier boss is skipped or unobserved. The optional Chief Engineer Kels does not block that full-run roster.

One Bestia variant has a fixture-backed HP-zero plus combat-exit rule. Other scripted endings without a verified rule remain **unconfirmed**. Combat exit, disappearance, a timer, or a missing health bar alone is not victory evidence. Unknown NPCs can count as individual kills but cannot establish a named boss encounter or activity clear. Eternity Vault full-run claims remain disabled because puzzle evidence is incomplete.

Shared-rule tests exercise every death-based catalog definition, including missing final targets and lack of local participation. These tests are not a claim that every difficulty has been tested in the live game. See `data/catalog/coverage.json` or **Settings → Catalog support and diagnostics** for rule coverage.

## Files and backups

SWTOR creates the raw `combat_*.txt` files. Recap reads them with shared access and never changes them. StarParse can read the same files. Recap's SQLite history, backups, and imported-log copies live in `%LOCALAPPDATA%\SwtorRecap`; Settings shows both paths.

Use **Export history backup** to save a consistent backup. Do not copy only `recap.db` while the tracker runs. Restore creates a recovery copy first. Keep original combat logs for later parser corrections. Neither portable removal nor the installer uninstaller deletes your history or SWTOR logs.

Recap images hide character names by default. Combat logs, diagnostics, screenshots, and database backups can contain names and paths. Keep those out of public issues; share only the files needed to diagnose a problem.

## Troubleshooting

| Problem | Check |
| --- | --- |
| Installer says Recap is running | Quit tracker from the tray and close the dashboard. |
| Dashboard and tracker versions differ | Close SWTOR, then choose **Restart matching tracker** in the loading window. The app refuses to display counters from an older tracker. |
| Loading fails | Check complete extraction. Use **Try again**. Save the exact error if it repeats. |
| Ready screen while playing | Confirm `swtor.exe` is running under the same Windows account. |
| Game detected, no log | Apply SWTOR's logging setting and check the input folder for a recent `combat_*.txt`. Restart SWTOR if needed. |
| Recording without personal totals | Open recording details. Inspect source counts/gaps; select your character and reprocess if owner selection is offered. |
| NPC kill stays at zero | Check that you exceeded half the enemy's maximum health yourself and its death was recorded. Companion damage does not count. |
| Older kill totals show `+` | Reprocess saved history with the original logs available. |
| Startup points to an old copy | Disable it in the old copy before replacing. Save the preference again in the new copy. |
| Multiple SWTOR instances | Close the extra instance; simultaneous game instances are unsupported. |

For a test report, record the application version, Windows version, activity and difficulty, approximate times, completions and wipes. Include the relevant original combat log and screenshots of Session or recording details. No Steam account files or whole AppData folder are needed.

## Build from source

Install the .NET SDK pinned in `global.json` and Python 3. Dependency lockfiles are included.

```sh
python3 scripts/check.py          # Release build, tests, replays, catalog validation
python3 scripts/check.py --quick  # Build and Core/Integration tests
python3 scripts/publish.py        # Self-contained Windows x64 ZIP
```

On Windows use `py` in place of `python3`. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run `py scripts/publish.py --installer` to produce both downloads. `scripts/verify_package.py` checks archive hashes, executable headers and icons. The Windows package smoke test runs only on disposable CI runners; it checks install, launch, Start menu shortcut, upgrade, checksum rejection, preserved history and uninstall.

Build runs and logs are kept in a separate private repository. The workflow template is `scripts/ci/verify.yml`. Release downloads include SHA-256 checksums, file manifests and dependency notices. Local checks can be run without GitHub.

## Replay and preview

Use an empty, isolated data directory for each replay:

```sh
scripts/dotnet.sh run --project src/SwtorRecap.Tools -c Release -- \
  replay --manifest tests/Fixtures/manifest.json \
  --data-dir /tmp/swtor-replay-01 --seed 7741 --verify

scripts/dotnet.sh run --project src/SwtorRecap.Desktop -c Release -- \
  --preview --fixture tests/Fixtures/dashboard-year.json --data-dir /tmp/swtor-preview-01
```

Replay uses randomized chunk boundaries and duplicate notifications. `--speed 1` follows recorded forward timestamps. Preview uses synthetic history through the real engine and database. It cannot enable startup or access production history. `PROJECT_FLOW_DATA` takes precedence over `--data-dir`.

For Flow, run `localdev-mlx flow doctor --project .`, then `localdev-mlx flow --project .` and **Open test app**. The native adapter builds copied source; evidence is manual. No model is required. Set `SWTOR_NUGET_PACKAGES` to an existing cache if needed. Desktop tests save headless renders under `.artifacts/ui-qa`.

For performance measurements, use `scripts/measure-windows.ps1` with a tracker process ID and an output path. Replay timings do not measure live-game FPS or idle-agent overhead.
