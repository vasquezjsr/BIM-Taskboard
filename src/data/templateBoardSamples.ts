/** Default flat-board header on the RFI board. */
export const TEMPLATE_RFI_LOG_HEADER = 'RFI Log';

/** Default flat-board header on the Documents board. */
export const TEMPLATE_DOCUMENTS_HEADER = 'Contract Documents/Submittals';

export interface TemplateBoardSampleTask {
  title: string;
  description: string;
  status: string;
}

export const TEMPLATE_RFI_SAMPLE_TASKS: TemplateBoardSampleTask[] = [
  {
    title: 'RFI #101 — Structural Beam Conflict — Corridor B',
    description:
      'Submit RFI for structural beam encroachment at corridor B. Attach Navisworks clash view and proposed reroute.',
    status: 'waiting-for-response',
  },
  {
    title: 'RFI #102 — Ceiling Height — Restroom Core',
    description:
      'Confirm finished ceiling elevation at restroom core. GC to verify architectural reflected ceiling plan.',
    status: 'waiting-for-response',
  },
  {
    title: 'RFI #103 — Sleeve Locations — Level 01 Plenum',
    description:
      'Request confirmed sleeve and penetration locations for Level 01 mechanical plenum crossings.',
    status: 'waiting-for-response',
  },
];

export const TEMPLATE_DOCUMENTS_SAMPLE_TASKS: TemplateBoardSampleTask[] = [
  {
    title: 'Master Subcontract — Division 23 HVAC',
    description:
      'Receive and file the executed master subcontract covering HVAC scope, BIM deliverables, and coordination requirements.',
    status: 'received',
  },
  {
    title: 'Submittal — Rooftop AHU-1 Cut Sheets',
    description:
      'Collect manufacturer cut sheets and approved submittal for rooftop AHU-1. Link to equipment families in the model.',
    status: 'requested',
  },
  {
    title: 'Contract Addendum #2 — Reflected Ceiling Plan',
    description:
      'Receive addendum drawings updating reflected ceiling plan and plenum routing. Upload to project documents folder.',
    status: 'not-started',
  },
];
