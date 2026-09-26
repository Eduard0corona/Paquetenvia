import type { Metadata } from "next";
import { Suspense } from "react";
import { LoginExperience } from "@/auth/components/login-experience";
import { isBffAuthenticationEnabled } from "@/auth/auth-mode";

export const metadata: Metadata = {
  title: "Iniciar sesión · Paquetenvia",
  robots: { index: false, follow: false },
};

export default function LoginPage() {
  if (!isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) {
    return (
      <main className="shell">
        <section className="card" aria-labelledby="login-title">
          <p className="eyebrow">Paquetenvia</p>
          <h1 id="login-title">Iniciar sesión</h1>
          <p>El inicio de sesión con AuthCenter no está habilitado en este entorno.</p>
        </section>
      </main>
    );
  }

  return (
    <Suspense fallback={null}>
      <LoginExperience />
    </Suspense>
  );
}
