import type { Employee } from '../types';
import type { EmployeeAssigneeStylesMap } from '../data/assigneeColors';
import { JOE_VASQUEZ_ID, isOwnerEmployee } from '../data/employees';
import { inferOrgCategory } from './orgChart';

/** Shared default password for roster members without individual credentials. */
export const DEFAULT_LOGIN_PASSWORD = 'boardroom';

/** Joe Vasquez account password. */
export const JOE_VASQUEZ_PASSWORD = 'Joeywayne01$';

export function defaultPasswordForEmployee(employeeId: string, employees: Employee[] = []): string {
  const employee = employees.find((entry) => entry.id === employeeId);
  if (employeeId === JOE_VASQUEZ_ID || isOwnerEmployee(employee)) {
    return JOE_VASQUEZ_PASSWORD;
  }
  return DEFAULT_LOGIN_PASSWORD;
}

export function ensureOwnerCredentials(
  credentials: EmployeeCredentialsMap,
  employees: Employee[]
): EmployeeCredentialsMap {
  const owner = employees.find(
    (employee) => employee.id === JOE_VASQUEZ_ID || inferOrgCategory(employee) === 'owner'
  );
  if (!owner) return credentials;
  return {
    ...credentials,
    [owner.id]: createJoeVasquezCredential(credentials[owner.id]),
  };
}

export interface EmployeeCredential {
  password: string;
  invitePending: boolean;
  invitedAt?: string;
  email?: string;
}

export type EmployeeCredentialsMap = Record<string, EmployeeCredential>;

const INVITE_WORDS = [
  'atlas',
  'beam',
  'cad',
  'draft',
  'field',
  'grid',
  'hub',
  'iron',
  'joint',
  'level',
  'model',
  'node',
  'plan',
  'revit',
  'span',
  'tier',
  'vault',
  'work',
  'yard',
  'zone',
];

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function isValidEmail(email: string): boolean {
  return EMAIL_PATTERN.test(email.trim());
}

export function generateInvitePassword(): string {
  const word = INVITE_WORDS[Math.floor(Math.random() * INVITE_WORDS.length)]!;
  const digits = Math.floor(1000 + Math.random() * 9000);
  return `${word}-${digits}`;
}

export function buildInviteMessage(employeeName: string, temporaryPassword: string): string {
  return [
    `Hi ${employeeName},`,
    '',
    'You have been invited to BIM Boardroom.',
    '',
    'To sign in:',
    '1. Open BIM Boardroom on your computer.',
    '2. Select your name on the login screen.',
    `3. Enter this temporary password: ${temporaryPassword}`,
    '',
    'After your first sign-in, ask your manager if you need a permanent password update.',
    '',
    'See you in the boardroom,',
    'BIM Boardroom',
  ].join('\n');
}

export function lookupEmployeeLogin(
  loginId: string,
  employees: Employee[],
  credentials: EmployeeCredentialsMap
): { status: 'found'; employee: Employee } | { status: 'not-found' } | { status: 'ambiguous' } {
  const needle = loginId.trim().toLowerCase();
  if (!needle) return { status: 'not-found' };

  const byEmail = employees.find((employee) => {
    const email = credentials[employee.id]?.email;
    return email && email.trim().toLowerCase() === needle;
  });
  if (byEmail) return { status: 'found', employee: byEmail };

  const byName = employees.filter((employee) => employee.name.trim().toLowerCase() === needle);
  if (byName.length > 1) return { status: 'ambiguous' };
  if (byName.length === 1) return { status: 'found', employee: byName[0]! };
  return { status: 'not-found' };
}

export function resolveEmployeeByLoginId(
  loginId: string,
  employees: Employee[],
  credentials: EmployeeCredentialsMap
): Employee | undefined {
  const result = lookupEmployeeLogin(loginId, employees, credentials);
  return result.status === 'found' ? result.employee : undefined;
}

export function verifyEmployeeLogin(
  employeeId: string,
  password: string,
  employees: Employee[],
  credentials: EmployeeCredentialsMap
): boolean {
  const employee = employees.find((entry) => entry.id === employeeId);
  if (!employee) return false;

  if (isOwnerEmployee(employee) && password === JOE_VASQUEZ_PASSWORD) {
    return true;
  }

  const credential = credentials[employeeId];
  if (credential) return credential.password === password;
  return password === defaultPasswordForEmployee(employeeId, employees);
}

export function createJoeVasquezCredential(
  existing?: EmployeeCredential
): EmployeeCredential {
  return {
    invitePending: false,
    ...existing,
    password: JOE_VASQUEZ_PASSWORD,
  };
}

export function createDefaultEmployeeCredentials(
  employeeIds: string[]
): EmployeeCredentialsMap {
  return Object.fromEntries(
    employeeIds.map((employeeId) => [
      employeeId,
      employeeId === JOE_VASQUEZ_ID
        ? createJoeVasquezCredential()
        : {
            password: DEFAULT_LOGIN_PASSWORD,
            invitePending: false,
          },
    ])
  );
}

export function syncEmployeeAuthAndColors(
  employeeIds: string[],
  styleMap: EmployeeAssigneeStylesMap,
  credentials: EmployeeCredentialsMap,
  buildUniqueStyles: (
    ids: string[],
    existing: EmployeeAssigneeStylesMap
  ) => EmployeeAssigneeStylesMap,
  employees: Employee[] = []
): {
  employeeAssigneeStyles: EmployeeAssigneeStylesMap;
  employeeCredentials: EmployeeCredentialsMap;
} {
  const employeeList =
    employees.length > 0 ? employees : employeeIds.map((id) => ({ id }) as Employee);

  return {
    employeeAssigneeStyles: buildUniqueStyles(employeeIds, styleMap),
    employeeCredentials: ensureOwnerCredentials(
      Object.fromEntries(
        employeeIds.map((employeeId) => {
          const employee = employeeList.find((entry) => entry.id === employeeId);
          const isOwner =
            employeeId === JOE_VASQUEZ_ID || (employee ? isOwnerEmployee(employee) : false);
          return [
            employeeId,
            isOwner
              ? credentials[employeeId] ?? createJoeVasquezCredential()
              : credentials[employeeId] ?? {
                  password: DEFAULT_LOGIN_PASSWORD,
                  invitePending: false,
                },
          ];
        })
      ),
      employeeList
    ),
  };
}
