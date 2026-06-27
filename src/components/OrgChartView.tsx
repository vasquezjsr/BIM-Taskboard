import { useMemo, useState } from 'react';
import {
  DndContext,
  DragOverlay,
  MeasuringStrategy,
  MouseSensor,
  closestCenter,
  pointerWithin,
  useSensor,
  useSensors,
  type CollisionDetection,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { useStore } from '../store/useStore';
import {
  ORG_SLOT_PREFIX,
  REPORTS_UNDER_PREFIX,
  findReportsUnderDropFromCardOverlap,
  getEmployeeManagers,
  parseOrgSlotDropId,
  parseReportsUnderDropId,
} from '../utils/orgChart';
import { snapGrabPointToCursor } from '../utils/orgChartDragModifiers';
import { canManageOrg } from '../utils/permissions';
import { OrgTreeCardPreview, OrgTreeChart } from './OrgTreeChart';
import { OrgChartViewport } from './OrgChartViewport';
import styles from './OrgChartView.module.css';

export function OrgChartView() {
  const employees = useStore((s) => s.employees);
  const employeePermissions = useStore((s) => s.employeePermissions);
  const employeeReportsTo = useStore((s) => s.employeeReportsTo);
  const orgChartLevelSlots = useStore((s) => s.orgChartLevelSlots);
  const currentUserId = useStore((s) => s.currentUserId);
  const viewAsOriginalUserId = useStore((s) => s.viewAsOriginalUserId);
  const addEmployeeManager = useStore((s) => s.addEmployeeManager);
  const placeEmployeeOnOrgChartSlot = useStore((s) => s.placeEmployeeOnOrgChartSlot);

  const [activeDragId, setActiveDragId] = useState<string | null>(null);

  const sensors = useSensors(useSensor(MouseSensor, { activationConstraint: { distance: 4 } }));

  const memberIds = useMemo(() => employees.map((employee) => employee.id), [employees]);

  const collisionDetection = useMemo<CollisionDetection>(() => {
    return (args) => {
      const activeId = args.active ? String(args.active.id) : null;
      const droppableEntries = args.droppableContainers.map((container) => ({
        id: String(container.id),
        rect: args.droppableRects.get(container.id) ?? null,
      }));

      const pointerHits = pointerWithin(args);
      const slotHit = pointerHits.find((collision) =>
        String(collision.id).startsWith(ORG_SLOT_PREFIX)
      );
      if (slotHit) return [slotHit];

      const overlapReportDrop =
        activeId != null
          ? findReportsUnderDropFromCardOverlap(activeId, args.collisionRect, droppableEntries)
          : null;
      if (overlapReportDrop) return [{ id: overlapReportDrop }];

      const reportsUnderHit = pointerHits.find((collision) =>
        String(collision.id).startsWith(REPORTS_UNDER_PREFIX)
      );
      if (reportsUnderHit) return [reportsUnderHit];

      const centerHits = closestCenter(args);
      const centerSlot = centerHits.find((collision) =>
        String(collision.id).startsWith(ORG_SLOT_PREFIX)
      );
      if (centerSlot) return [centerSlot];

      const centerReportsUnder = centerHits.find((collision) =>
        String(collision.id).startsWith(REPORTS_UNDER_PREFIX)
      );
      if (centerReportsUnder) return [centerReportsUnder];

      return centerHits;
    };
  }, []);

  const editorUserId = viewAsOriginalUserId ?? currentUserId;
  const canManage = canManageOrg(editorUserId, employees, employeePermissions);
  const isViewingAs = viewAsOriginalUserId !== null;
  const activeEmployee = employees.find((employee) => employee.id === activeDragId);
  const activeManagerIds =
    activeEmployee && activeDragId
      ? getEmployeeManagers(activeDragId, memberIds, employeeReportsTo)
      : [];

  const handleDragStart = (event: DragStartEvent) => {
    setActiveDragId(String(event.active.id));
  };

  const handleDragEnd = (event: DragEndEvent) => {
    setActiveDragId(null);
    if (!canManage || !event.over) return;

    const activeId = String(event.active.id);
    const overId = String(event.over.id);

    const slotDrop = parseOrgSlotDropId(overId);
    if (slotDrop) {
      placeEmployeeOnOrgChartSlot(slotDrop.depth, activeId, slotDrop.halfSlotIndex);
      return;
    }

    const managerId = parseReportsUnderDropId(overId);
    if (managerId && managerId !== activeId) {
      addEmployeeManager(activeId, managerId);
    }
  };

  const handleDragCancel = () => {
    setActiveDragId(null);
  };

  const dragModifiers = useMemo(() => [snapGrabPointToCursor], []);

  return (
    <div className={styles.wrapper}>
      <div className={styles.header}>
        <div>
          <h2 className={styles.title}>Organizational Chart</h2>
          <p className={styles.subtitle}>
            Company reporting structure from ownership through BIM, operations, and support.
          </p>
        </div>
      </div>

      {canManage && (
        <p className={styles.editHint}>
          Drag cards by the <strong>⠿</strong> handle to reposition, or drop onto a manager to
          change reporting lines. Middle-click drag to pan · scroll wheel to zoom.
          {isViewingAs ? ' You are previewing another role but can still edit the org chart.' : ''}
        </p>
      )}

      {!canManage && (
        <p className={styles.readOnlyNote}>
          You can view the org chart. Middle-click drag to pan · scroll wheel to zoom.
        </p>
      )}

      <div className={styles.chartArea}>
        <DndContext
          sensors={sensors}
          collisionDetection={collisionDetection}
          modifiers={dragModifiers}
          measuring={{
            droppable: { strategy: MeasuringStrategy.Always },
          }}
          onDragStart={handleDragStart}
          onDragEnd={handleDragEnd}
          onDragCancel={handleDragCancel}
        >
          <OrgChartViewport>
            <OrgTreeChart
              memberIds={memberIds}
              employees={employees}
              employeeReportsTo={employeeReportsTo}
              orgChartLevelSlots={orgChartLevelSlots}
              canManage={canManage}
              activeDragId={activeDragId}
            />
          </OrgChartViewport>

          <DragOverlay dropAnimation={null}>
            {activeEmployee != null ? (
              <OrgTreeCardPreview
                employee={activeEmployee}
                managerIds={activeManagerIds}
                employees={employees}
              />
            ) : null}
          </DragOverlay>
        </DndContext>
      </div>
    </div>
  );
}
