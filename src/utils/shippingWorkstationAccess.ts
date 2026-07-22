import type { Task } from '../types';
import {
  isSsv3AssemblyTask,
  isSsv3PackageTask,
  isSsv3ShippingPackageTask,
} from './boardroomPackageImport';
import {
  isAssemblyReleasedFromFabStatus,
  isShippingHandedToFieldStatus,
} from './taskStatuses';

/**
 * Package is fully on Shipping, or still on Fab with ≥1 assembly released for ship.
 * Shared by Shipping Dashboard and the project Shipping board ghost view.
 */
export function isShippingVisiblePackage(task: Task, allTasks: Task[]): boolean {
  if (isSsv3ShippingPackageTask(task) && !isShippingHandedToFieldStatus(task.status)) {
    return true;
  }
  if (!isSsv3PackageTask(task)) return false;
  return allTasks.some(
    (child) =>
      isSsv3AssemblyTask(child) &&
      child.parentTaskId === task.id &&
      isAssemblyReleasedFromFabStatus(child.status)
  );
}
