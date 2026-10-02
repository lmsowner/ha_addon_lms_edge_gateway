# LMS Energy Studio

## Daily energy and tariff analysis (v9)

Click **Today's energy** or **Explore energy & tariffs** to open a daily stacked consumption chart. Every configured household load, Other home use, each EV and Battery charging has a separate colour and kWh total. Solar generation is a separate line, not part of consumption. Click/tap a time or use the interval slider to inspect its per-load kW readings, solar, grid import and tariff at the interval's start. The lower chart shows import price; shaded time slices distinguish scheduled off-peak, peak, extra whole-home sessions, dynamic prices and unknown prices.

**Tariff & sessions** edits the tariff name, time zone, currency, peak/off-peak prices, daily windows and dated whole-home sessions. The demo defaults to 23:30–05:30; sample prices are illustrative, not a supplier quote. Windows and sessions can cross midnight. Last-listed overlapping sessions win and override either fixed or dynamic prices. Only enter confirmed whole-home sessions: an EV charging event alone does not establish tariff eligibility. A demo lunchtime session and editable 48-slot dynamic-pricing example are available. Negative prices work. Dynamic mode never silently replaces missing prices with the peak rate. Settings persist with the exported browser configuration.

The total cost uses **grid-import kWh**, not total household consumption. Pricing is integrated minute by minute across tariff boundaries even when consumption intervals are longer. Missing prices produce a partial cost and priced-energy coverage; standing charges and export credits are excluded. Demo profiles are marked illustrative. Instantaneous live telemetry without historical readings displays a missing-history state instead of inventing measurements.

The production adapter can supply dated measured history:

```js
window.LMSEnergy.updateEnergyHistory({
  date: '2026-10-02', // local date in configured home time zone
  slots: [{
    start: 0, end: 30, // ordered, non-overlapping local clock minutes
    durationMinutes: 30, // optional actual elapsed duration for energy integration
    loads: {'heat-pump': 0.8, underfloor: 0.3, pool: 0},
    other: 0.4, ev: [3.4, 1.2], batteryCharge: 1.8,
    solar: 0, gridImport: 7.9, gridExport: 0
  }]
});
```

All power readings are average kW for the interval and non-negative; `ev` contains charging only, one entry per configured car. Empty historical load readings are zero. Grid import/export are separately metered flows; they are not inferred from load readings. Histories remain in memory and should be restored from LMS/Home Assistant on page load. Maximum 1,500 intervals per day. The timeline is a 24-hour local-clock view; upstream adapters must map daylight-saving repetitions appropriately and supply actual elapsed durations. This is an analysis display, not a supplier billing engine or charging controller.

`getConfig().tariff` contains `mode` (`fixed`/`dynamic`), `timezone`, `currency`, `peak`, `offPeak`, `windows`, `sessions` and `dynamic`. All prices are in minor currency units per kWh (pence for GBP, cents for EUR/USD). Sessions and dynamic entries use `{date,start,end,price}`; sessions also support `name`. Dynamic price slots do not overlap or cross midnight; a final `end: '24:00'` covers the last minute of the day. Tariff editing preserves current live energy telemetry.

Balancing cells have a gentle pulsing border and activity indicator: green for receiving, red for giving/bleeding, amber when direction is unavailable. Explicit `balancing: false` remains still; otherwise a reported balancing state or non-zero balancing current activates the effect. Pause freezes it and reduced-motion uses a static glow.

## Clickable cell lines and cylindrical reserve (v7)

Click near a cell voltage line to select the nearest trace. The selected line becomes thicker and the other traces dim while keeping the same scale for comparison. The clickable legend and cell cards share the selection; clicking again or **Show all cells** clears it. A readings strip by the graph shows the selected cell's latest voltage, balancing current/direction and resistance, with **View cell details** to jump to its card. Hover near a line identifies that cell in the history tooltip. Missing-history gaps cannot be selected as connecting lines.

The pack SoC instrument now has shaded cylindrical sides, elliptical caps and a graduated colour based on the reported reserve: red at 0%, amber in the middle and green at 100%. Unknown SoC remains unfilled. This is a functional gauge, not an image of the battery's physical construction.

## Battery analysis (v6)

Click or keyboard-activate any battery in the energy diagram to open pack and cell analysis. Each battery's Customise setting selects an illustrative BMS profile: no detail, pack only, active cell balancing or passive balancing. Every demo reading and graph is clearly marked synthetic and does not claim support for the photographed hardware.

Pack voltage, pack SoC, nominal voltage, rated Ah, current and temperature are optional. Cell voltage, signed balancing current (positive receiving, negative giving/bleeding), balancing status and balance resistance in ohms are independently optional. Missing data says **Not reported**; cell voltage does not create an inferred cell SoC. High/low markers and mV spread are comparisons, not fault diagnoses. Only explicitly reported active transfers animate between cells. Passive balancing animates to a resistor; it never invents a transfer to another cell.

Overlaid cell history supports isolating a cell, restoring all cells, 1/6/24-hour windows and pointer inspection. Pack charge/discharge power and SoC have separate history graphs. Samples with missing readings leave gaps. Ranges end at the latest supplied timestamp, and all batteries have independent history. Reduced-motion and Pause apply to balancing animation.

Add optional `batteryAnalysis` to the existing balanced `updateTelemetry` payload. Keys are configured storage IDs. Live telemetry never falls back to illustrative BMS data. Pack SoC here is the detailed BMS value; also supply matching SOC in `batteryUnits` for the main energy diagram. Nominal voltage and Ah must come from device metadata; they are not inferred for real batteries.

```js
// Merge this property into the normal balanced telemetry payload:
const batteryAnalysis = {
  'dc-bank': {
    voltage: 52.8, nominalVoltage: 51.2, ampHours: 280,
    soc: 78, current: -12.5, temperature: 26,
    balanceType: 'active',
    cells: [
      {id: 'c1', name: 'Cell 01', voltage: 3.32, balancing: true, balanceCurrent: -0.8},
      {id: 'c2', name: 'Cell 02', voltage: 3.29, balancing: true, balanceCurrent: 0.72}
    ],
    transfers: [{from: 'c1', to: 'c2', current: 0.72}],
    history: [
      {time: '2026-10-02T06:00:00Z', soc: 79, power: 0.65,
       cellVoltages: {c1: 3.325, c2: 3.287}},
      {time: '2026-10-02T06:10:00Z', soc: 78, power: 0.66,
       cellVoltages: {c1: 3.320, c2: 3.290}}
    ]
  }
};
```

`cells` can contain the available monitored cells, not necessarily the full pack. In `history`, power follows the dashboard convention: positive discharging, negative charging. Pack `current` is positive charging, negative discharging. `balanceCurrent` describes balancing only, not total cell current. Passive cells may supply `balanceResistance`; active balancers usually leave it absent unless measured. Use `balanceType: 'unknown'` when unavailable and omit `transfers` unless actual routes are known. History timestamps must be increasing, at most 2,000 samples and 256 cells per battery. Arrays of historical readings should be downsampled upstream for long retention; this client does not connect to a BMS or store measured history across reloads.

## Routed generation and V2G (v5)

Each wind, hydro, generator or plug-in solar source has a `connection` (`ac` or `inverter`) and selected `inverter` ID. Wind/hydro represent conditioned output after a compatible controller or converter; a routed generator uses a compatible inverter's AC generator input. These are logical energy routes, not electrical wiring instructions. Routed inputs share the selected inverter's rating with PV and attached DC storage. The simulation clips generation proportionally at that rating and counts every source once. Direct AC inputs bypass these inverters. The mixed demo now includes routed wind/hydro, an idle generator on the garage inverter, and direct plug-in solar.

Vehicles independently configure `v2g`, `energyMode` (`auto`, `charge`, `export`, `idle`), `exportPower` in kW, and `exportReserve` in percent. Chargers independently configure `chargerV2G`. Export requires both capability flags, an assigned charger, and SOC above the minimum reserve. Selecting a Tesla image never enables V2G automatically. Automatic mode charges in sunshine and exports during evening/peak scenarios when supported. One active vehicle uses each charger at a time. Green export animations reverse from vehicle through charger to the AC bus; surplus can reach the grid.

Telemetry EV values are signed: positive charging, negative export. Negative readings require both capability flags; measured values are displayed rather than limited by demo settings. The balance is `solar + other sources + battery discharge + grid import = household demand + signed EV power`. This remains a monitoring demo: no device commands or Home Assistant connection are issued.

Example configuration:

```js
window.LMSEnergy.configure({
  chargerV2G: [true, false],
  vehicles: [{name: 'My bidirectional EV', model: 'other', charger: 0,
    soc: 80, v2g: true, energyMode: 'export', exportPower: 3, exportReserve: 40}]
});
```

## Independent array sizes and graduated storage displays (v4)

Each `arrayRoutes` entry now has its own `panels` count (0–120) and `rating` in watts (100–1000). Total panel count and installed kWp are derived from those entries. Existing configurations migrate from the former equal split without losing names or inverter connections. The mixed demo uses 18 × 440 W panels on the roof and 10 × 420 W on the garage. Edit each array under **Configure connections & sources**. The top-level panel count is now read-only; the panel-rating field is a default for new arrays.

Every AC, DC and plug-in battery independently stores `capacity` (kWh), `soc` and `maxPower` (kW). Cards show **stored / total kWh**, percentage, flow power, a ten-step red/amber/mint/green gauge, and a charging shimmer. Generic batteries also have a graduated vertical charge illustration; Powerwall cards preserve the real product image. Gauge colours indicate reserve, while flow-line colours indicate energy direction. Motion respects Pause and the system's reduced-motion setting. Fleet reserve is weighted by capacity, not averaged by battery count.

For example, a 10 kWh battery at 25% holds 2.5 kWh, while a 40 kWh battery at 75% holds 30 kWh; together they hold 32.5 / 50 kWh, or 65%.

Legacy `configure({panels: 30})` evenly redistributes that total for compatibility. For independent sizes, provide `arrayRoutes` with explicit counts and ratings. Aggregate telemetry without per-array readings is estimated by installed wattage, not panel count alone.

## Connection-aware topology (v3)

Use **Configure connections & sources**, or Customise, to add up to four named hybrid inverters, assign solar arrays to the same or different inverter, and configure each battery as AC/grid-connected, DC/inverter-connected, or plug-in AC storage. Each battery has its own name, capacity, reserve and power limit. Up to eight named wind, micro-hydro, generator and plug-in-solar inputs can connect through a selected compatible inverter or directly to the home AC bus. Wind and hydro readings represent conditioned output; plug-in solar defaults to direct AC via its own micro-inverter. Grid remains a distinct bidirectional input.

**Load mixed-energy demo** replaces only solar/inverter/storage/source settings with a representative system. Your house, vehicles, chargers and household loads are retained. Existing browser preferences migrate without losing the previous layout or device counts.

The diagram explicitly routes roof PV through its chosen inverter, DC storage through that inverter, and AC storage and other generation to the home AC bus. Inverter readings are signed net AC output: accepted PV and routed generation plus attached battery discharge (minus charging). Solar and batteries are counted once only. Simulation is lossless, clips PV at the inverter's AC rating conservatively, and limits DC discharge to available inverter headroom; it is a visual demo, not a wiring design, electrical controller or detailed conversion-loss simulation. PV that could instead be diverted to DC storage above the AC rating is conservatively curtailed in this simplified model.

### Per-device Home Assistant readings

`getConfig()` exposes stable IDs and connections. `getState()` exposes the balanced per-device demo/telemetry state. `updateTelemetry` now accepts source readings, individually measured array powers, and signed battery-unit powers plus SOC, in addition to its existing aggregate fields:

```js
window.LMSEnergy.updateTelemetry({
  solar: 8, home: 2, grid: -3.5, battery: -0.5, batterySoc: 75,
  ev: [3.4, 1.2],
  arrays: { 'array-1': 5, 'array-2': 3 },
  sources: { wind: 1, hydro: 0.4, 'balcony-solar': 1.2 },
  batteryUnits: {
    powerwall: { power: -0.2, soc: 80 },
    'dc-bank': { power: -0.2, soc: 70 },
    'plug-battery': { power: -0.1, soc: 65 }
  },
  loads: { 'heat-pump': 0.8 }
});
```

These IDs correspond to the mixed-energy preset. Device totals must match supplied aggregate values. Input/output readings must balance within 50 W; invalid or inconsistent readings are rejected before rendering. Missing source readings show zero. Without array/battery details, aggregate solar is allocated by installed wattage and aggregate battery power equally across units (an estimate, not independent measurement); supply per-device fields for accurate topology flows. The aggregate battery reserve becomes capacity-weighted when per-unit SOC is supplied. Do not send measured inverter output as another source: it is derived from PV and attached storage, to avoid double-counting. Production adapters may add a losses/unmetered term if actual meters do not balance within this demo's tolerance.

`dist/topology.js` is now a required locally bundled script, loaded before `app.js`.

Photo-led, framework-free HTML/CSS/JavaScript dashboard. Serve `dist` with any static web server, embed it in Blazor or an iframe. Optional Google Fonts have local system fallbacks. All product imagery is bundled locally; credits are in `dist/credits.html`.

## Customisation

- Home name, two real house photographs, or upload your own house photo.
- 0–120 individually shown solar panels across 1–4 arrays; configurable panel wattage.
- 0–6 individually shown Tesla Powerwalls, with aggregate reserve and power.
- 0–4 named Tesla Wall Connectors and 0–4 vehicles; assign each vehicle to a charger.
- Tesla Model 3/S/X/Y product renders; upload your own car photograph for other EVs.
- User-defined household loads with stable IDs, name, category and demo demand. Add/remove as many loads as needed.
- Other home use is a separate baseline. The home total includes household loads plus other use; EV power is separate.
- Settings and uploaded images persist locally when localStorage is available. Export JSON includes configuration and uploaded photo data. Reset restores defaults.

Sunny/evening/peak scenarios balance power: solar + battery discharge + grid import = home + EV demand. Negative grid means export; negative battery means charging. Green is generation/export/charging storage; red is consumption/import/discharge. Particle direction follows the physical flow. Reduced-motion preference disables motion. In the simulator, one EV at a time draws from each assigned charger; other cars on the same charger show idle. Arrays share aggregate solar power by installed wattage. Provide per-device telemetry for independently measured generation and storage.

## LMS / Home Assistant adapter

The demo does not connect to Home Assistant or control devices. Use LMS's server-side connection to Home Assistant, convert W to kW and normalise signed conventions, then call:

```js
window.LMSEnergy.updateTelemetry({
  solar: 8, home: 1.8, grid: -0.6,
  battery: -1, batterySoc: 78,
  ev: [3.4, 1.2], // one entry per configured car
  loads: { 'heat-pump': 0.8, underfloor: 0.3, pool: 0.2 }
});
```

`loads` is an object keyed by configured load IDs. Unprovided load readings show zero; the remaining home demand appears as Other home use. Negative readings or load sums greater than home demand are rejected. `updateTelemetry` suspends simulation. `resumeDemo()` restores it. `configure({...})` updates configuration; `getConfig()` returns a copy. Inspect exported configuration to retrieve each household load's stable ID for entity mapping.

EV SOC is configuration data in this prototype. Per-battery SOC/power and independent array readings are supported through the per-device telemetry schema above. Charger displayed power is the sum of assigned EV readings, not an independent charger meter. The daily chart is explicitly illustrative, not measured history. Demo charging toggles never issue real control commands.

## Files

`dist/index.html`, `dist/style.css`, `dist/app.js`, `dist/assets/*.webp`, and `dist/credits.html`. No package dependencies or build step. The mobile diagram scrolls horizontally to keep photographs and labels readable.
