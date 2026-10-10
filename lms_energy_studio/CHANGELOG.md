# Changelog

## 2026.10.10.18.49

- A battery can be an array of batteries that share one state of charge and one import/export power.

## 2026.10.10.17.51

- The entity picker can hide unavailable sensors, and does so unless that filter is turned off.

## 2026.10.10.17.42

- Open the entity picker as a full-width dialog so sensor names and entity ids have room to read.

## 2026.10.10.16.51

- Sort the mapping device list by area, then by device name.

## 2026.10.10.14.23

- Edit mode puts a mapping control on each dashboard part, so solar, inverters, batteries, loads and the grid are mapped on their own.
- Numbered battery cell sensors can be applied in order from the first cell to the pack's cell count.

## 2026.10.02.18.44

- Republish Energy Studio so Home Assistant can install this version from the App store.

## 2026.10.02.17.34

- Publish Energy Studio in the Home Assistant repository with versioned amd64/aarch64 GHCR images.
- Add persistent HA entity mappings, live quality-aware power topology, BMS and V2G monitoring.
- Add Recorder/statistics histories, reviewed tariffs, cheap-rate sessions and separate EV cap estimates.
- Preserve the supplied dashboard/assets and add ingress, revision guards and server-side credentials.
