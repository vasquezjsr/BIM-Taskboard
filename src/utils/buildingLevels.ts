/** Default job configuration: UG + 8 numbered floors + Roof. */
export const DEFAULT_BUILDING_LEVEL_COUNT = 10;

/** Generate level names from UG through numbered floors to Roof. */
export function generateBuildingLevels(totalCount: number): string[] {
  const count = Math.max(2, Math.min(30, totalCount));
  if (count === 2) return ['UG', 'Roof'];
  const levels: string[] = ['UG'];
  const numbered = count - 2;
  for (let i = 1; i <= numbered; i++) {
    levels.push(`Level ${i}`);
  }
  levels.push('Roof');
  return levels;
}

/** Human-readable label for the level-count dropdown (e.g. "UG + 8 levels + Roof"). */
export function formatBuildingLevelOptionLabel(totalCount: number): string {
  const count = Math.max(2, Math.min(30, totalCount));
  if (count === 2) return 'UG + Roof (2 total)';
  const numbered = count - 2;
  const floorLabel = numbered === 1 ? '1 level' : `${numbered} levels`;
  return `UG + ${floorLabel} + Roof (${count} total)`;
}

export function defaultBuildingLevels(): string[] {
  return generateBuildingLevels(DEFAULT_BUILDING_LEVEL_COUNT);
}
export function defaultActiveLevels(buildingLevels: string[]): string[] {
  return [...buildingLevels];
}

export function toggleLevelSelection(active: string[], level: string, allLevels: string[]): string[] {
  if (active.includes(level)) {
    const next = active.filter((l) => l !== level);
    return next.length ? next : [level];
  }
  return [...active, level].sort(
    (a, b) => allLevels.indexOf(a) - allLevels.indexOf(b)
  );
}

export function isLevelGroupName(name: string, buildingLevels: string[]): boolean {
  return buildingLevels.includes(name);
}
