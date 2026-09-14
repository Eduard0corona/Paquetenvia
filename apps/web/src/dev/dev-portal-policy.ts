export const localOrganizationId = "11111111-1111-1111-1111-111111111111";

export const localProfiles = {
  dispatcher: {
    label: "Synthetic dispatcher (MFA)",
    credential: "local-dispatcher-mfa",
    organizationId: localOrganizationId,
    namespace: "local-dispatcher",
  },
  driver: {
    label: "Synthetic own driver",
    credential: "active-driver",
    organizationId: localOrganizationId,
    namespace: "local-driver-session",
  },
} as const;

export function isDevPortalEnabled(
  nodeEnvironment: string | undefined,
  deploymentClass: string | undefined,
  explicitOptIn: string | undefined,
): boolean {
  return (
    explicitOptIn === "true" &&
    (nodeEnvironment === "development" ||
      (nodeEnvironment === "production" &&
        deploymentClass === "DEV_SYNTHETIC"))
  );
}

export function resolveLocalProfile(value: string) {
  if (!Object.hasOwn(localProfiles, value)) {
    return undefined;
  }

  return localProfiles[value as keyof typeof localProfiles];
}
