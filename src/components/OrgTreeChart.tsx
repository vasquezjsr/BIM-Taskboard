import { useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useDraggable, useDroppable } from '@dnd-kit/core';
import { employeeAssigneeStyle, employeeInitials } from '../data/employees';
import type { EmployeeAssigneeStylesMap } from '../data/assigneeColors';
import type { Employee } from '../types';
import {
  ORG_CARD_WIDTH,
  buildOrgChartLevels,
  employeeNameById,
  employeeOrgLabel,
  cardLeftPx,
  computeOrgChartGridLayout,
  getEmployeeManagers,
  getManagerEdges,
  orgSlotDropId,
  reportsUnderDropId,
  resolveOrgChartCardPositions,
  type EmployeeReportsToMap,
  type OrgChartLevel,
  type OrgChartLevelSlotsMap,
} from '../utils/orgChart';
import { useStore } from '../store/useStore';
import { useOrgChartViewportTransform } from './OrgChartViewport';
import treeStyles from './OrgTreeChart.module.css';

interface OrgTreeChartProps {
  memberIds: string[];
  employees: Employee[];
  employeeReportsTo: EmployeeReportsToMap;
  orgChartLevelSlots: OrgChartLevelSlotsMap;
  canManage: boolean;
  activeDragId: string | null;
}

interface ConnectorPath {
  d: string;
  stroke: string;
}

function managerConnectorColor(managerId: string, styleMap: EmployeeAssigneeStylesMap): string {
  return employeeAssigneeStyle(managerId, styleMap).border;
}

function scalePx(value: number, zoom: number): number {
  return value * zoom;
}

function toLocalY(containerRect: DOMRect, clientY: number): number {
  return clientY - containerRect.top;
}

function getNodeCenterX(node: HTMLElement, containerRect: DOMRect): number {
  const rect = node.getBoundingClientRect();
  return rect.left + rect.width / 2 - containerRect.left;
}

function buildLevelConnectorPaths(
  edges: { managerId: string; employeeId: string }[],
  levels: OrgChartLevel[],
  container: HTMLElement,
  containerRect: DOMRect,
  assigneeStyles: EmployeeAssigneeStylesMap
): ConnectorPath[] {
  const paths: ConnectorPath[] = [];
  const depthOf = new Map<string, number>();

  for (const level of levels) {
    for (const employeeId of level.employeeIds) {
      depthOf.set(employeeId, level.depth);
    }
  }

  for (let index = 0; index < levels.length - 1; index += 1) {
    const parentLevel = levels[index]!;
    const childLevel = levels[index + 1]!;
    const parentRow = container.querySelector<HTMLElement>(
      `[data-level-depth="${parentLevel.depth}"]`
    );
    const childRow = container.querySelector<HTMLElement>(
      `[data-level-depth="${childLevel.depth}"]`
    );
    if (!parentRow || !childRow) continue;

    const parentRowRect = parentRow.getBoundingClientRect();
    const childRowRect = childRow.getBoundingClientRect();
    const busY =
      toLocalY(containerRect, parentRowRect.bottom) +
      (toLocalY(containerRect, childRowRect.top) - toLocalY(containerRect, parentRowRect.bottom)) / 2;

    const levelEdges = edges.filter(
      (edge) =>
        depthOf.get(edge.managerId) === parentLevel.depth &&
        depthOf.get(edge.employeeId) === childLevel.depth
    );
    if (levelEdges.length === 0) continue;

    const edgesByManager = new Map<string, string[]>();
    for (const edge of levelEdges) {
      const children = edgesByManager.get(edge.managerId) ?? [];
      children.push(edge.employeeId);
      edgesByManager.set(edge.managerId, children);
    }

    for (const [managerId, employeeIds] of edgesByManager) {
      const managerNode = container.querySelector<HTMLElement>(`[data-org-node="${managerId}"]`);
      if (!managerNode) continue;

      const managerX = getNodeCenterX(managerNode, containerRect);
      const managerBottom = toLocalY(containerRect, managerNode.getBoundingClientRect().bottom);
      const stroke = managerConnectorColor(managerId, assigneeStyles);

      const childCenters: { x: number; top: number }[] = [];
      for (const employeeId of employeeIds) {
        const childNode = container.querySelector<HTMLElement>(`[data-org-node="${employeeId}"]`);
        if (!childNode) continue;
        childCenters.push({
          x: getNodeCenterX(childNode, containerRect),
          top: toLocalY(containerRect, childNode.getBoundingClientRect().top),
        });
      }
      if (childCenters.length === 0) continue;

      const childXs = childCenters.map((child) => child.x);
      const minChildX = Math.min(...childXs);
      const maxChildX = Math.max(...childXs);
      const busLeft = Math.min(managerX, minChildX);
      const busRight = Math.max(managerX, maxChildX);

      paths.push({
        d: `M ${managerX} ${managerBottom} L ${managerX} ${busY}`,
        stroke,
      });

      if (busLeft < busRight) {
        paths.push({
          d: `M ${busLeft} ${busY} L ${busRight} ${busY}`,
          stroke,
        });
      }

      for (const child of childCenters) {
        paths.push({
          d: `M ${child.x} ${busY} L ${child.x} ${child.top}`,
          stroke,
        });
      }
    }
  }

  return paths;
}

function PhantomSlot({
  depth,
  halfSlotIndex,
  positionLeft,
  slotWidth,
  slotMinHeight,
}: {
  depth: number;
  halfSlotIndex: number;
  positionLeft: number;
  slotWidth: number;
  slotMinHeight: number;
}) {
  const { setNodeRef, isOver } = useDroppable({
    id: orgSlotDropId(depth, halfSlotIndex),
  });

  return (
    <div
      ref={setNodeRef}
      className={`${treeStyles.phantomSlot} ${isOver ? treeStyles.phantomSlotDropTarget : ''}`}
      style={{ left: positionLeft, width: slotWidth, minHeight: slotMinHeight }}
      aria-hidden
    />
  );
}

function employeeAvatarStyle(
  employeeId: string,
  styleMap: EmployeeAssigneeStylesMap
): CSSProperties {
  const badgeStyle = employeeAssigneeStyle(employeeId, styleMap);
  return {
    borderColor: badgeStyle.border,
    background: badgeStyle.background,
    color: badgeStyle.text,
  };
}

function OrgDragHandle({
  employeeName,
  listeners,
  attributes,
  setActivatorNodeRef,
  disabled,
}: {
  employeeName: string;
  listeners: ReturnType<typeof useDraggable>['listeners'];
  attributes: ReturnType<typeof useDraggable>['attributes'];
  setActivatorNodeRef: ReturnType<typeof useDraggable>['setActivatorNodeRef'];
  disabled?: boolean;
}) {
  if (disabled) {
    return (
      <span className={treeStyles.nodeDragHandleDisabled} aria-hidden>
        ⠿
      </span>
    );
  }

  return (
    <button
      ref={setActivatorNodeRef}
      type="button"
      className={treeStyles.nodeDragHandle}
      aria-label={`Drag ${employeeName}`}
      title="Drag to reposition or drop on a manager"
      {...listeners}
      {...attributes}
    >
      ⠿
    </button>
  );
}

function OrgTreeNode({
  employee,
  managerIds,
  employees,
  assigneeStyles,
  canManage,
  activeDragId,
  positionLeft,
  stackOrder,
  cardWidth,
}: {
  employee: Employee;
  managerIds: string[];
  employees: Employee[];
  assigneeStyles: EmployeeAssigneeStylesMap;
  canManage: boolean;
  activeDragId: string | null;
  positionLeft?: number;
  stackOrder?: number;
  cardWidth: number;
}) {
  const roleLabel = employeeOrgLabel(employee);
  const managerLabel =
    managerIds.length === 0
      ? 'Top level'
      : managerIds.map((id) => employeeNameById(employees, id)).join(', ');

  const { attributes, listeners, setNodeRef, setActivatorNodeRef, isDragging } = useDraggable({
    id: employee.id,
    disabled: !canManage,
  });

  const { setNodeRef: setDropRef, isOver } = useDroppable({
    id: reportsUnderDropId(employee.id),
    disabled: !canManage || isDragging,
  });

  const isActiveDrag = activeDragId === employee.id;

  const style: CSSProperties = {
    ...(positionLeft !== undefined ? { left: positionLeft, zIndex: (stackOrder ?? 0) + 2 } : {}),
    width: cardWidth,
  };

  return (
    <div
      ref={(node) => {
        setNodeRef(node);
        setDropRef(node);
      }}
      style={style}
      data-org-node={employee.id}
      className={`${treeStyles.nodeCard} ${positionLeft !== undefined ? treeStyles.nodeCardPositioned : ''} ${
        isOver ? treeStyles.nodeCardDropTarget : ''
      } ${isDragging || isActiveDrag ? treeStyles.nodeCardDragging : ''}`}
      title={canManage ? `Drop here to add ${employee.name} as a boss` : undefined}
    >
      <div className={treeStyles.nodeHeader}>
        <div className={treeStyles.nodeHeaderTop}>
          <OrgDragHandle
            employeeName={employee.name}
            listeners={listeners}
            attributes={attributes}
            setActivatorNodeRef={setActivatorNodeRef}
            disabled={!canManage}
          />
          <div>
            <div className={treeStyles.nodeName}>{employee.name}</div>
            <div className={treeStyles.nodeTitle}>{roleLabel}</div>
          </div>
        </div>
      </div>
      <div className={treeStyles.nodeBody}>
        <div className={treeStyles.avatar} style={employeeAvatarStyle(employee.id, assigneeStyles)}>
          {employeeInitials(employee.name)}
        </div>
        <div className={treeStyles.nodeDetails}>
          <div className={treeStyles.nodeMeta}>
            <span className={treeStyles.managerSummaryLabel}>Reports to</span>
            <span className={treeStyles.managerSummaryValue}>{managerLabel}</span>
          </div>
        </div>
      </div>
    </div>
  );
}

export function OrgTreeChart({
  memberIds,
  employees,
  employeeReportsTo,
  orgChartLevelSlots,
  canManage,
  activeDragId,
}: OrgTreeChartProps) {
  const assigneeStyles = useStore((state) => state.employeeAssigneeStyles);
  const viewportTransform = useOrgChartViewportTransform();
  const visualScale = viewportTransform.scale;
  const containerRef = useRef<HTMLDivElement>(null);
  const [connectorPaths, setConnectorPaths] = useState<ConnectorPath[]>([]);
  const [chartSize, setChartSize] = useState({ width: 0, height: 0 });

  const baseLevels = useMemo(
    () => buildOrgChartLevels(memberIds, employeeReportsTo),
    [memberIds, employeeReportsTo]
  );

  const levels = baseLevels;

  const isDragging = canManage && activeDragId !== null;

  const edges = useMemo(
    () => getManagerEdges(memberIds, employeeReportsTo),
    [memberIds, employeeReportsTo]
  );

  const gridLayout = useMemo(
    () => computeOrgChartGridLayout(levels, orgChartLevelSlots, activeDragId, isDragging),
    [levels, orgChartLevelSlots, activeDragId, isDragging]
  );

  useLayoutEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const updatePaths = () => {
      const containerRect = container.getBoundingClientRect();
      setChartSize({
        width: container.offsetWidth,
        height: container.offsetHeight,
      });
      setConnectorPaths(
        buildLevelConnectorPaths(edges, levels, container, containerRect, assigneeStyles)
      );
    };

    updatePaths();
    const raf = requestAnimationFrame(updatePaths);
    const observer = new ResizeObserver(updatePaths);
    observer.observe(container);

    return () => {
      cancelAnimationFrame(raf);
      observer.disconnect();
    };
  }, [
    edges,
    levels,
    gridLayout,
    memberIds,
    employeeReportsTo,
    activeDragId,
    orgChartLevelSlots,
    assigneeStyles,
    viewportTransform.pan.x,
    viewportTransform.pan.y,
    viewportTransform.scale,
  ]);

  const cardWidth = scalePx(ORG_CARD_WIDTH, visualScale);
  const rowGap = scalePx(48, visualScale);
  const rowMinHeight = scalePx(180, visualScale);
  const phantomMinHeight = scalePx(140, visualScale);

  return (
    <section className={treeStyles.orgTreeSection}>
      <div className={treeStyles.treeScroll}>
        {memberIds.length === 0 ? (
          <div className={treeStyles.emptyTree}>No employees yet.</div>
        ) : (
          <div
            ref={containerRef}
            className={treeStyles.levelChart}
            style={{
              gap: rowGap,
              ['--org-zoom' as string]: visualScale,
            }}
          >
            {levels.map((level) => {
              if (level.employeeIds.length === 0) return null;

              const cardPositions =
                gridLayout.positionsByDepth.get(level.depth) ??
                resolveOrgChartCardPositions(
                  level.employeeIds,
                  orgChartLevelSlots[String(level.depth)]
                );
              const showPhantoms = isDragging;
              const phantomStarts = showPhantoms
                ? (gridLayout.phantomStartsByDepth.get(level.depth) ?? [])
                : [];
              const cardPlacements = Object.entries(cardPositions)
                .filter(([employeeId]) => level.employeeIds.includes(employeeId))
                .map(([employeeId, start]) => ({ employeeId, start }))
                .sort((a, b) => a.start - b.start);

              return (
                <div
                  key={level.depth}
                  className={`${treeStyles.levelRow} ${treeStyles.levelRowHalfGrid} ${
                    showPhantoms ? treeStyles.levelRowDragging : ''
                  }`}
                  data-level-depth={level.depth}
                  style={{
                    width: gridLayout.width > 0 ? scalePx(gridLayout.width, visualScale) : undefined,
                    minHeight: rowMinHeight,
                  }}
                >
                  {cardPlacements.map(({ employeeId, start }) => {
                    const employee = employees.find((entry) => entry.id === employeeId);
                    if (!employee) return null;

                    const managerIds = getEmployeeManagers(employeeId, memberIds, employeeReportsTo);

                    return (
                      <OrgTreeNode
                        key={employeeId}
                        employee={employee}
                        managerIds={managerIds}
                        employees={employees}
                        assigneeStyles={assigneeStyles}
                        canManage={canManage}
                        activeDragId={activeDragId}
                        positionLeft={scalePx(cardLeftPx(start, gridLayout.paddingLeft), visualScale)}
                        stackOrder={start}
                        cardWidth={cardWidth}
                      />
                    );
                  })}
                  {phantomStarts.map((start) => (
                    <PhantomSlot
                      key={`phantom-${level.depth}-${start}`}
                      depth={level.depth}
                      halfSlotIndex={start}
                      positionLeft={scalePx(cardLeftPx(start, gridLayout.paddingLeft), visualScale)}
                      slotWidth={cardWidth}
                      slotMinHeight={phantomMinHeight}
                    />
                  ))}
                </div>
              );
            })}

            <svg
              className={treeStyles.connectorLayer}
              viewBox={`0 0 ${Math.max(chartSize.width, 1)} ${Math.max(chartSize.height, 1)}`}
              aria-hidden
            >
              {connectorPaths.map((path, index) => (
                <path
                  key={index}
                  d={path.d}
                  className={treeStyles.connectorPath}
                  stroke={path.stroke}
                />
              ))}
            </svg>
          </div>
        )}
      </div>
    </section>
  );
}

export function OrgTreeCardPreview({
  employee,
  managerIds,
  employees,
}: {
  employee: Employee;
  managerIds?: string[];
  employees?: Employee[];
}) {
  const assigneeStyles = useStore((state) => state.employeeAssigneeStyles);
  const roleLabel = employeeOrgLabel(employee);
  const managerLabel =
    managerIds && employees
      ? managerIds.length === 0
        ? 'Top level'
        : managerIds.map((id) => employeeNameById(employees, id)).join(', ')
      : null;

  return (
    <div className={`${treeStyles.nodeCard} ${treeStyles.nodeCardDragOverlay}`}>
      <div className={treeStyles.nodeHeader}>
        <div className={treeStyles.nodeHeaderTop}>
          <span className={treeStyles.nodeDragHandleDisabled} aria-hidden>
            ⠿
          </span>
          <div>
            <div className={treeStyles.nodeName}>{employee.name}</div>
            <div className={treeStyles.nodeTitle}>{roleLabel}</div>
          </div>
        </div>
      </div>
      <div className={treeStyles.nodeBody}>
        <div className={treeStyles.avatar} style={employeeAvatarStyle(employee.id, assigneeStyles)}>
          {employeeInitials(employee.name)}
        </div>
        <div className={treeStyles.nodeDetails}>
          {managerLabel !== null ? (
            <div className={treeStyles.nodeMeta}>
              <span className={treeStyles.managerSummaryLabel}>Reports to</span>
              <span className={treeStyles.managerSummaryValue}>{managerLabel}</span>
            </div>
          ) : (
            <div className={treeStyles.nodeMeta}>Not assigned to a team</div>
          )}
        </div>
      </div>
    </div>
  );
}
