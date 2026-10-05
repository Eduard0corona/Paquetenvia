import Link from "next/link";

export default function HomePage() {
  return (
    <main className="shell">
      <section className="card" aria-labelledby="home-title">
        <p className="eyebrow">Paquetenvia</p>
        <h1 id="home-title">Envíos y entregas en un solo lugar</h1>
        <p>
          Crea órdenes, asigna repartidores, da seguimiento a cada entrega y
          concilia tus cobros.
        </p>
        <Link className="button" href="/login">
          Iniciar sesión
        </Link>
      </section>
    </main>
  );
}
