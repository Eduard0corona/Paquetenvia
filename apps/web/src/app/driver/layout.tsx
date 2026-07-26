import type { Metadata } from "next";
import type { ReactNode } from "react";

export const metadata: Metadata = {
  title: "Paquetenvia Repartidor",
  description: "Paradas asignadas y operación móvil de última milla",
};

export default function DriverLayout({
  children,
}: Readonly<{ children: ReactNode }>) {
  return children;
}
