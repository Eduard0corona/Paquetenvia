import type { Metadata, Viewport } from "next";
import type { ReactNode } from "react";
import { ServiceWorkerRegistration } from "@/components/service-worker-registration";
import "./globals.css";

export const metadata: Metadata = {
  title: "Paquetenvia",
  description: "Foundation workspace for the Paquetenvia modular monolith.",
  manifest: "/manifest.webmanifest",
};

export const viewport: Viewport = {
  themeColor: "#102a43",
};

export default function RootLayout({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <html lang="es-MX">
      <body>
        <ServiceWorkerRegistration />
        {children}
      </body>
    </html>
  );
}
