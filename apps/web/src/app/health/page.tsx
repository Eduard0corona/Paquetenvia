import Link from "next/link";
import { getLocalHealthState } from "@/lib/health";

export default function HealthPage() {
  const health = getLocalHealthState();

  return (
    <main className="shell">
      <section className="card" aria-labelledby="health-title">
        <p className="eyebrow">Estado local</p>
        <h1 id="health-title">Workspace {health.label}</h1>
        <p className="health" role="status">
          <span aria-hidden="true" />
          {health.status}
        </p>
        <p>Esta vista no consulta servicios externos ni endpoints de negocio.</p>
        <Link className="textLink" href="/">
          Volver al inicio
        </Link>
      </section>
    </main>
  );
}
