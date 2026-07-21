import Link from "next/link";

export default function HomePage() {
  return (
    <main className="shell">
      <section className="card">
        <p className="eyebrow">FND-001 · Foundation</p>
        <h1>Paquetenvia</h1>
        <p>
          El workspace web está listo para crecer sobre los contratos normativos,
          sin autenticación ni flujos de negocio en esta etapa.
        </p>
        <Link className="button" href="/health">
          Ver estado local
        </Link>
      </section>
    </main>
  );
}
