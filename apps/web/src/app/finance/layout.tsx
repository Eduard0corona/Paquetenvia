import type { ReactNode } from "react";
import { AppShell } from "@/components/app-shell/app-shell";

/** Every /finance/* screen renders inside the shared application shell. */
export default function FinanceLayout({ children }: Readonly<{ children: ReactNode }>) {
  return <AppShell>{children}</AppShell>;
}
