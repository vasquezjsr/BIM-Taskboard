import { useStore } from '../store/useStore';
import { employeeAssigneeStyleFromMap } from '../data/assigneeColors';

export function useEmployeeAssigneeStyle(employeeId: string) {
  const styleMap = useStore((state) => state.employeeAssigneeStyles);
  return employeeAssigneeStyleFromMap(employeeId, styleMap);
}
