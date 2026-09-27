import type { Metadata } from "next";
import { OnboardingExperience } from "@/auth/components/onboarding-experience";
import { isBffAuthenticationEnabled } from "@/auth/auth-mode";

export const metadata: Metadata = {
  title: "Tu organización · Paquetenvia",
  robots: { index: false, follow: false },
};

export default function OnboardingPage() {
  if (!isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) {
    return (
      <main className="shell">
        <section className="card" aria-labelledby="onboarding-title">
          <p className="eyebrow">Paquetenvia</p>
          <h1 id="onboarding-title">Tu organización</h1>
          <p>El registro con AuthCenter no está habilitado en este entorno.</p>
        </section>
      </main>
    );
  }

  return <OnboardingExperience />;
}
