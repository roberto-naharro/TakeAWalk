# Take a Walk

A Cities: Skylines mod that gives scenic, quiet, low-pollution pedestrian paths a small park-like leisure value, so neighbourhoods laced with nice footpaths feel a little more livable. Trees, props and nearby landmarks act as the points of interest along the walk; noise and pollution take that value away.

In vanilla CS1 a pedestrian path is purely connective: it adds nothing to an area. This mod treats a pleasant path the way real cities do: a place worth being near.

## How it works

The mod runs in two decoupled phases each simulation tick:

- **Scoring (throttled):** a round-robin slice of the net-segment buffer is evaluated. Every **Beautification** network qualifies (decorative pedestrian paths, park paths, and Plazas & Promenades park paths). Roads are excluded (their service class is `Road`, not `Beautification`). Each qualifying path is cached with the leisure rate to apply at its mid-point.
- **Injection (every tick):** the whole cache is re-applied via `AddResource`. This continuous re-application is essential, because the game's immaterial-resource grid folds and **zeroes** its temporary cells every cycle, so a one-shot injection decays to nothing. Real parks feed the grid every simulation step; so does this mod.

### Length and decoration drive the value

A long or richly decorated scenic path is worth more than a short bare one. For each path, a bounded breadth-first search over connected segments measures the **contiguous length** of the whole path, and the leisure rate combines length and decoration **additively**:

```text
rate = round( SmallParkRate · clamp(lengthFactor + decorationFactor, 0, MaxTotalMultiplier) )

  lengthFactor     = length  / LengthForFullValue       // ~80 m (10 grid units) ≈ a small park
  decorationFactor = quality / DecorationForFullValue   // quality = trees·TreeWeight + props·PropWeight + landmark
```

Because the two factors add, a path earns park-like value two independent ways: a long plain path is a walkable zone, and a short path lined with trees and props is a "park you built along a footpath". A roughly 80 m path reaches small-park value on length alone; the total is capped at `MaxTotalMultiplier` (~3× a small park). A short, bare path comes out near zero and is skipped. Two pollution gates keep grim spots out: ground pollution (`NaturalResourceManager.CheckPollution` > `PollutionThreshold`) and nearby water pollution (`WaterManager.CheckWater` > `WaterPollutionThreshold`, sampled around the point so a path hugging a filthy shore is excluded while dry inland paths are unaffected). Below the gates, noise and dirty water both erode the quality, with noise partly forgiven by a nearby landmark (the *Eiffel-tower effect*, below).

Feature counts come straight from the engine's spatial grids: trees from `TreeManager.m_treeGrid` (540², 32 m cells), props from `PropManager.m_propGrid` (270², 64 m cells), landmarks from `BuildingManager.m_buildingGrid` (270², 64 m cells); noise from `ImmaterialResourceManager.CheckLocalResource(NoisePollution, …)`.

```text
ImmaterialResourceManager.AddResource(Entertainment,  rate,      midpoint, InjectRadius)
ImmaterialResourceManager.AddResource(Sightseeing,    rate / 2,  midpoint, InjectRadius)   // optional
ImmaterialResourceManager.AddResource(TourCoverage,   rate / 2,  midpoint, InjectRadius)   // optional
```

This is the same channel parks and venues feed, so the value flows naturally into land value, wellbeing and tourism, capped so even a long, lavish path stays a few times a small park, never a substitute for real venues.

### The "Eiffel-tower effect"

Noise normally erodes the scenic value, but a nearby landmark forgives part of that penalty (`LandmarkNoiseForgiveness`). Standing by a famous monument, you don't mind the traffic, so a noisy path next to a unique building can still be pleasant, while the same noise on a bare street is not.

## Drawing cims onto the paths

Beyond the leisure value, the mod makes cims actually *walk* scenic paths via native **walking tours** (requires the **Parklife** DLC).

In CS1 a leisure destination is always a **building**: leisure trips are routed by `TransferManager`, whose `StartTransfer` dispatches only to a Vehicle, Citizen, Building, or Park (and the Park branch resolves to a Building), and `HumanAI.StartMoving` only targets a `ushort` building, so there is no API to send a cim to a raw path position. The one engine mechanism that sends cims walking along a route for its own sake is the Parklife walking tour, a `TransportLine` of `TransportType.Pedestrian`. The mod reuses exactly that.

For each qualifying path, a bounded BFS finds paths that join the walkable network at a **single** point (a spur off a sidewalk, not a through-path already used by pedestrians), measures their contiguous length, and samples stops along the loop. If the path is long enough (`TourMinLength`) and has housing within `TourHousingRadius`, a walking-tour line is created with a distinct brown colour, so cims stroll the whole route. A small, local **Attractiveness** boost is injected every tick at each tour's start point (the *Visitor appeal* setting) to draw them in. Path goodness weights a probabilistic chance of creation, the live set is capped (`MaxTours`, sharing the game's 255 transport-line budget), and it re-rolls monthly, or immediately when a path is built or edited. Through-paths and isolated islands get no tour.

Walking-tour lines are kept out of the save with **Harmony**: a patch brackets `TransportManager.Data.Serialize`, zeroing the tour lines' flags for the duration of the save and restoring them immediately after, so the save file never contains them while the live game keeps them. A second patch zeroes their maintenance cost so a walking tour never charges the city. Tours are released on level-unload and mod-disable.

## Save safety

The mod persists **nothing**. Half 1 only feeds the existing immaterial-resource grid (which is recomputed live, never serialized by the mod). Half 2's walking-tour lines are excluded from serialization by the Harmony patch above and released on unload. The mod can be added to or removed from any save cleanly, and the map is never modified.

## Compatibility

- Requires **Harmony** (auto-installed via the CitiesHarmony dependency).
- Walking tours require the **Parklife** DLC; the leisure value works without it.
- Works with custom path and park assets (it filters by service class, not by asset name).
- Safe to add to or remove from any save (see *Save safety* above).

## Settings

All values are exposed in the options panel, fully localized:

| Group | Setting | Meaning |
| --- | --- | --- |
| Eligibility | Max ground pollution | Above this (0-255), a path gives no bonus |
| Eligibility | Max water pollution | Above this (0-255), a path beside dirty water gives no bonus (dry inland paths unaffected) |
| Eligibility | Water pollution penalty | How strongly nearby polluted water (below the limit) erodes the scenic value |
| Eligibility | Noise penalty / Landmark forgiveness | How much noise costs, and how much a landmark forgives |
| Points of interest | Search radius | How far around a path to look for trees/props/landmarks |
| Points of interest | Value per tree / prop / landmark | Weight of each feature in the quality score |
| Leisure value | Small-park value | Base rate a baseline-length path gives (matched to a small park) |
| Leisure value | Length for full value | Path length (m) that earns full small-park value (~80 m) |
| Leisure value | Maximum value | Ceiling as a multiple of a small park (~3×) |
| Leisure value | Decoration for park value | How much nearby decoration alone makes a path worth a small park |
| Leisure value | Spread radius / Add sightseeing | How far value spreads; optional tourism value |
| Leisure value | Health boost | Small local health gain along the path, as a fraction of its leisure value |
| Leisure value | Visitor appeal | Attractiveness added at each walking tour's start, drawing visitors (0-50 strength) |
| Cims walking the paths | Walking tours on scenic paths | Toggle the transient walking-tour system (Half 2, Parklife) |
| Cims walking the paths | Minimum path length | How long a single-entrance path must be to earn a walking tour |
| Cims walking the paths | Maximum walking tours | Cap on live tour lines (shares the game's 255 transport-line budget) |
| Cims walking the paths | Tour frequency | Per-line budget; higher runs more walking groups |
| Performance | Paths scanned per tick | Round-robin throughput vs. CPU cost |
| Performance | Max path length scan | How many connected segments a length measurement follows |

## Languages

English, Spanish, French, German, Portuguese, Italian. Translation files live in `Locale/` as plain `KEY value` text; contributions welcome.

## Building

```bash
# Prerequisites: Mono, xbuild
# Game DLLs are in GameReferences/ (committed). Harmony packages are in packages/.
# Copy .env.example to .env and fill in your machine's values.

xbuild TakeAWalk.csproj /p:Configuration=Release

# Build + deploy to a mounted game folder:
./mount-cities.sh        # one-time per session: mount the game over SMB
./deploy.sh              # Debug build → game Mods folder
./deploy.sh --release    # Release build → dist/ + game Mods folder
```

Continuous delivery: Release Please opens version PRs from Conventional Commits; merging a release tag fires `workshop-deploy.yml`, which builds from source and publishes to the Steam Workshop. The first publish is manual via `./publish.sh` (it creates the Workshop item; set `WORKSHOP_ITEM_ID` afterwards).

## Credits

Created by roberto-naharro. Built on Harmony via the CitiesHarmony API.
