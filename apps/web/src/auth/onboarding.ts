import { csrfHeaderName } from "./request-credentials";

/**
 * REG-001 onboarding (AUTH-OPEN-REGISTRATION): a signed-in user without an organization
 * creates a business (active at once) or registers an ally (pending PLATFORM_ADMIN approval).
 * The API is the barrier; this module only shapes requests and turns responses into Spanish copy.
 */
export const onboardingPaths = {
  organizations: "/api/v1/onboarding/organizations",
  applications: "/api/v1/me/organization-applications",
} as const;

export type OnboardingOrganizationType = "BUSINESS" | "ALLY";

export type OrganizationStatus = "ACTIVE" | "PENDING_APPROVAL" | "SUSPENDED" | "CLOSED";

export interface OrganizationApplication {
  readonly organizationId: string;
  readonly organizationType: OnboardingOrganizationType;
  readonly displayName: string;
  readonly status: OrganizationStatus;
  readonly createdAt: string;
}

export interface OnboardingDraft {
  readonly organizationType: OnboardingOrganizationType;
  readonly legalName: string;
  readonly displayName: string;
}

export type OnboardingValidation =
  | { readonly ok: true; readonly body: Record<string, string> }
  | { readonly ok: false; readonly message: string };

export type CreateOrganizationResult =
  | { readonly kind: "created"; readonly application: OrganizationApplication }
  | { readonly kind: "limit_reached" }
  | { readonly kind: "idempotency_conflict" }
  | { readonly kind: "forbidden" }
  | { readonly kind: "invalid" }
  | { readonly kind: "unavailable" };

export const legalNameMaxLength = 200;
export const displayNameMaxLength = 80;

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/;
const statuses: readonly OrganizationStatus[] = ["ACTIVE", "PENDING_APPROVAL", "SUSPENDED", "CLOSED"];

export const organizationTypeLabels: Readonly<Record<OnboardingOrganizationType, string>> = {
  BUSINESS: "Negocio",
  ALLY: "Paquetería aliada",
};

export const organizationStatusLabels: Readonly<Record<OrganizationStatus, string>> = {
  ACTIVE: "Activa",
  PENDING_APPROVAL: "Pendiente de aprobación",
  SUSPENDED: "Suspendida",
  CLOSED: "Cerrada",
};

export const onboardingMessages = {
  limitReached:
    "Ya tienes una organización activa o pendiente de aprobación. Solo puedes crear una a la vez.",
  idempotencyConflict:
    "Esta solicitud ya se envió con otros datos. Recarga la página e inténtalo de nuevo.",
  forbidden:
    "Tu cuenta no puede crear organizaciones. Verifica tu correo en AuthCenter y vuelve a iniciar sesión.",
  invalid: "Revisa los datos de la organización.",
  unavailable: "El servicio no está disponible en este momento. Intenta de nuevo.",
  businessCreated: "Tu negocio quedó activo. Ya puedes entrar a Paquetenvia.",
  allyPending:
    "Recibimos tu solicitud de paquetería aliada. Un administrador de Paquetenvia la revisará; mientras tanto no puedes operar.",
} as const;

/** Trims, then applies the AI-05 limits; the API applies the same rules and is authoritative. */
export function validateOnboardingDraft(draft: OnboardingDraft): OnboardingValidation {
  const legalName = draft.legalName.trim();
  const displayName = draft.displayName.trim();
  if (draft.organizationType !== "BUSINESS" && draft.organizationType !== "ALLY") {
    return { ok: false, message: "Elige si es un negocio o una paquetería aliada." };
  }
  if (legalName.length === 0 || legalName.length > legalNameMaxLength || controlCharacters.test(legalName)) {
    return { ok: false, message: `La razón social es obligatoria (máximo ${legalNameMaxLength} caracteres).` };
  }
  if (
    displayName.length === 0 ||
    displayName.length > displayNameMaxLength ||
    controlCharacters.test(displayName)
  ) {
    return { ok: false, message: `El nombre visible es obligatorio (máximo ${displayNameMaxLength} caracteres).` };
  }
  return {
    ok: true,
    body: {
      organization_type: draft.organizationType,
      legal_name: legalName,
      display_name: displayName,
    },
  };
}

/** One key per submitted draft: a retry of the same draft replays the original 201. */
export function createIdempotencyKey(randomUuid: () => string = () => crypto.randomUUID()): string {
  return `onboarding-${randomUuid()}`;
}

export function parseOrganizationApplication(value: unknown): OrganizationApplication | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const body = value as Record<string, unknown>;
  if (
    typeof body.organization_id !== "string" ||
    !uuidPattern.test(body.organization_id) ||
    (body.organization_type !== "BUSINESS" && body.organization_type !== "ALLY") ||
    typeof body.display_name !== "string" ||
    typeof body.status !== "string" ||
    !statuses.includes(body.status as OrganizationStatus)
  ) {
    return null;
  }
  return {
    organizationId: body.organization_id,
    organizationType: body.organization_type,
    displayName: body.display_name,
    status: body.status as OrganizationStatus,
    createdAt: typeof body.created_at === "string" ? body.created_at : "",
  };
}

export async function fetchOwnApplications(
  fetcher: typeof fetch = fetch,
): Promise<readonly OrganizationApplication[] | null> {
  try {
    const response = await fetcher(onboardingPaths.applications, {
      method: "GET",
      credentials: "include",
      cache: "no-store",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) {
      return null;
    }
    const body: unknown = await response.json();
    if (!Array.isArray(body)) {
      return null;
    }
    const applications = body.map(parseOrganizationApplication);
    return applications.every((application) => application !== null)
      ? (applications as OrganizationApplication[])
      : null;
  } catch {
    return null;
  }
}

export async function createOnboardingOrganization(
  draft: OnboardingDraft,
  csrfToken: string,
  idempotencyKey: string,
  fetcher: typeof fetch = fetch,
): Promise<CreateOrganizationResult> {
  const validation = validateOnboardingDraft(draft);
  if (!validation.ok) {
    return { kind: "invalid" };
  }
  let response: Response;
  try {
    response = await fetcher(onboardingPaths.organizations, {
      method: "POST",
      credentials: "include",
      cache: "no-store",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        [csrfHeaderName]: csrfToken,
      },
      body: JSON.stringify(validation.body),
    });
  } catch {
    return { kind: "unavailable" };
  }
  if (response.status === 201) {
    try {
      const application = parseOrganizationApplication(await response.json());
      return application === null ? { kind: "unavailable" } : { kind: "created", application };
    } catch {
      return { kind: "unavailable" };
    }
  }
  if (response.status === 409) {
    const code = await readProblemCode(response);
    if (code === "ORGANIZATION_LIMIT_REACHED") {
      return { kind: "limit_reached" };
    }
    return code === "IDEMPOTENCY_CONFLICT" ? { kind: "idempotency_conflict" } : { kind: "unavailable" };
  }
  if (response.status === 400) {
    return { kind: "invalid" };
  }
  if (response.status === 401 || response.status === 403) {
    return { kind: "forbidden" };
  }
  return { kind: "unavailable" };
}

export function createOrganizationMessage(result: CreateOrganizationResult): string {
  switch (result.kind) {
    case "created":
      return result.application.status === "ACTIVE"
        ? onboardingMessages.businessCreated
        : onboardingMessages.allyPending;
    case "limit_reached":
      return onboardingMessages.limitReached;
    case "idempotency_conflict":
      return onboardingMessages.idempotencyConflict;
    case "forbidden":
      return onboardingMessages.forbidden;
    case "invalid":
      return onboardingMessages.invalid;
    default:
      return onboardingMessages.unavailable;
  }
}

async function readProblemCode(response: Response): Promise<string | null> {
  try {
    const body: unknown = await response.json();
    return typeof body === "object" && body !== null && typeof (body as Record<string, unknown>).code === "string"
      ? ((body as Record<string, unknown>).code as string)
      : null;
  } catch {
    return null;
  }
}
