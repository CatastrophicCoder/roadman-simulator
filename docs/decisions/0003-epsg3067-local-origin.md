# 0003: EPSG:3067 with a local origin per map

Status: Accepted

## Context

The source data comes in different coordinate systems: OSM uses WGS84 and MML uses ETRS-TM35FIN. The game needs metric coordinates, and the values must stay small so 32-bit floats keep enough precision.

## Decision

The pipeline reprojects all data to ETRS-TM35FIN (EPSG:3067), which is metric, and subtracts a local origin defined per map in `map.yaml`. The origin is placed near the map centre, so game coordinates stay within about ±1.5 km for Somero.

## Consequences

- Game units are metres, and float precision is fine at this map size.
- Each map pack records its CRS and origin, so positions can be converted back to real-world coordinates.
- EPSG:3067 is designed for Finland. A map outside Finland sets its own CRS in `map.yaml`. TODO: confirm that the CRS is fully configurable per map once a non-Finnish map is added.
