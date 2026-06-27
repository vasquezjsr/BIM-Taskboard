/** Standard Project Management board checklist for the project template. */
export interface PmChecklistGroup {
  name: string;
  tasks: string[];
}

export const PM_BOARD_CHECKLIST: PmChecklistGroup[] = [
  {
    name: 'Contract / Scope Review',
    tasks: [
      'Receive and review contract documents',
      'Review scope of work',
      'Review BIM requirements',
      'Review project specifications',
      'Review drawings and addenda',
      'Identify BIM deliverables',
      'Identify modeling/detailing deadlines',
      'Confirm LOD requirements',
      'Confirm trade scope boundaries',
    ],
  },
  {
    name: 'Project Kickoff / Coordination Setup',
    tasks: [
      'Attend BIM kickoff meeting',
      'Attend internal project kickoff',
      'Confirm coordination schedule',
      'Confirm clash detection process',
      'Confirm model exchange schedule',
      'Confirm software/version requirements',
      'Coordinate with GC/VDC team',
      'Coordinate with other trades',
    ],
  },
  {
    name: 'Project Setup / Standards',
    tasks: [
      'Set up project folder structure',
      'Set up file naming standards',
      'Set up Revit project files',
      'Set up project levels and grids',
      'Confirm project units and coordinates',
      'Confirm detail standards',
      'Create BIM project checklist',
      'Create issue tracking process',
    ],
  },
  {
    name: 'Model Setup / Coordination',
    tasks: [
      'Set up shared coordinates',
      'Link architectural model',
      'Link structural model',
      'Link MEP models as needed',
      'Verify model alignment',
      'Review existing conditions models',
      'Confirm all required backgrounds are available',
      'Confirm latest drawings are being used',
    ],
  },
  {
    name: 'Fabrication / Field Requirements',
    tasks: [
      'Confirm spool/fabrication expectations',
      'Confirm hanger/support expectations',
      'Confirm sleeve/opening expectations',
      'Set up fabrication parts or services',
    ],
  },
  {
    name: 'Access / Model Management',
    tasks: [
      'Set up worksharing/cloud model access',
      'Add users and permissions',
      'Assign model ownership responsibilities',
    ],
  },
  {
    name: 'Constructability Review',
    tasks: [
      'Review constructability risks',
      'Review major equipment locations',
      'Review ceiling space constraints',
      'Review structural constraints',
      'Review architectural constraints',
    ],
  },
  {
    name: 'Internal Detailing Planning',
    tasks: [
      'Coordinate with PM and superintendent',
      'Coordinate with foremen/detailers',
      'Establish internal detailing plan',
      'Assign detailers to areas/systems',
      'Prepare model for detailing start',
      'Communicate readiness to start detailing',
    ],
  },
];

/** First group name — used to detect whether the checklist is already present. */
export const PM_CHECKLIST_MARKER_GROUP = PM_BOARD_CHECKLIST[0]!.name;

export const PM_CHECKLIST_TASK_COUNT = PM_BOARD_CHECKLIST.reduce(
  (total, group) => total + group.tasks.length,
  0
);
