import type { ReactNode } from "react";
import { AppShell } from "@/components/app-shell/app-shell";

/** Every /ops/* screen renders inside the shared application shell. */
export default function OperationsLayout({ children }: Readonly<{ children: ReactNode }>) {
  return <AppShell>{children}</AppShell>;
}
