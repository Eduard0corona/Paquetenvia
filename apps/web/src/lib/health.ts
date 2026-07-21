export type LocalHealthState = Readonly<{
  label: "listo";
  status: "healthy";
}>;

export function getLocalHealthState(): LocalHealthState {
  return {
    label: "listo",
    status: "healthy",
  };
}
