import type { Metadata, Viewport } from "next";
import { connection } from "next/server";
import type { ReactNode } from "react";
import { ServiceWorkerRegistration } from "@/components/service-worker-registration";
import { BffSessionBootstrap } from "@/auth/components/bff-session-bootstrap";
import "./globals.css";

export const metadata: Metadata = {
  title: "Paquetenvia",
  description: "Gestión de envíos, entregas y cobros para operaciones de paquetería.",
  manifest: "/manifest.webmanifest",
};

export const viewport: Viewport = {
  // Mirrors --surface in globals.css for the light and dark color schemes.
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#ffffff" },
    { media: "(prefers-color-scheme: dark)", color: "#161e20" },
  ],
  colorScheme: "light dark",
};

export default async function RootLayout({
  children,
}: Readonly<{ children: ReactNode }>) {
  // Every page is rendered per request so Next.js can attach the CSP nonce
  // emitted by src/proxy.ts; a prerendered page would carry no nonce and its
  // scripts would be blocked.
  await connection();
  return (
    <html lang="es-MX">
      <body>
        <ServiceWorkerRegistration />
        <BffSessionBootstrap />
        {children}
      </body>
    </html>
  );
}
