<p align="center"><img src="assets/logo.svg" width="96" alt="Crossed lightsabers — SWTOR Recap"></p>

# SWTOR Recap

Windows app for SWTOR combat stats, session history, and monthly or yearly recaps. Reads combat logs and stores history on your PC. Works with Steam and alongside StarParse.

## Install

[Downloads](https://github.com/ASVPATM/swtor-recap/releases)

| Download | Setup |
| --- | --- |
| `SwtorRecap-0.2.0-Setup.exe` | Run the installer, then open **SWTOR Recap** from the Start menu. |
| `SwtorRecap-win-x64.zip` | Extract the entire ZIP and open **SwtorRecap.exe**. Keep the files together. |

Windows x64. The .NET runtime is included. No administrator access is needed. The test build is unsigned, so Windows may show a publisher or reputation warning.

1. In SWTOR, enable **Preferences → Combat Logging → Enable combat logging to file**.
2. Open Recap and launch SWTOR normally. The combat-log folder is detected automatically; change it in Settings if needed.
3. Play. Closing the dashboard leaves the tray tracker running. Finished sessions appear in History.

[Replace an older version](docs/GUIDE.md#replace-an-earlier-version) · [Setup and troubleshooting](docs/GUIDE.md)

## Counts

| Stat | What counts |
| --- | --- |
| Damage and healing | Your character's raw logged output. Healing includes self-heals, periodic effects, and overhealing. Effective amounts appear in recording details. |
| NPC kills | A recorded death after you dealt **more than 50% of the target's maximum health** in effective damage. The final blow can come from someone else. |
| Player deaths | Your character's logged deaths. |
| Combat time | Your logged combat intervals. |
| Game-open time | Observed time while SWTOR is running, including AFK time; excludes sleep and recording pauses. |
| Boss wins | Your participation in a supported encounter with victory evidence. |
| Activity clears | A supported final-boss defeat. |
| Full runs | All required supported encounters observed without a recording gap. |

Companion and party damage/healing are excluded from your totals. NPC kills exclude players, companions, catalogued bosses and training dummies; missing health or death evidence does not establish a kill. Bosses absent from the catalog can appear as NPC kills. Quest conversations, rewards, XP, loot, and official game kill credit are not tracked.

Boss recognition is limited. Hammer Station has rules for its three required encounters and completion. Most other encounter IDs do not yet establish wins or clears. [Supported rules and limits](docs/GUIDE.md#encounter-coverage)

## Data

SWTOR writes `combat_*.txt`; Recap only reads them. Recap history, backups, and imported-log copies live in `%LOCALAPPDATA%\SwtorRecap`. There is no account, upload service, overlay, or game-memory access. Recap images hide character names by default.

## Build

Install the .NET SDK specified in `global.json` and Python 3, then run `python3 scripts/check.py` (`py scripts/check.py` on Windows). [Build and package commands](docs/GUIDE.md#build-from-source)

## License

[MIT](LICENSE). [Dependency notices](THIRD_PARTY_NOTICES.txt). Original project logo. Independent fan project; not affiliated with Lucasfilm, Electronic Arts, or Broadsword.
