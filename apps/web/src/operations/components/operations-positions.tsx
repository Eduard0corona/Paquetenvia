import type { OperationsDashboardOrder } from "../contracts/operations-dashboard";
import { formatMazatlanTime } from "../contracts/operations-formatters";

export function OperationsPositions({
  items,
}: {
  readonly items: readonly OperationsDashboardOrder[];
}) {
  const positions = items
    .filter(
      (
        item,
      ): item is OperationsDashboardOrder & {
        latest_driver_location: NonNullable<
          OperationsDashboardOrder["latest_driver_location"]
        >;
        assignment: NonNullable<OperationsDashboardOrder["assignment"]>;
      } =>
        item.latest_driver_location !== null && item.assignment !== null,
    )
    .map((item, index, all) => ({
      item,
      x: normalize(
        item.latest_driver_location.lng,
        all.map((value) => value.latest_driver_location.lng),
      ),
      y:
        100 -
        normalize(
          item.latest_driver_location.lat,
          all.map((value) => value.latest_driver_location.lat),
        ),
    }));

  return (
    <section className="opsPositions" aria-labelledby="positions-title">
      <h2 id="positions-title">Posiciones</h2>
      <p>Vista de posiciones sin cartografía.</p>
      <div
        className="opsPositionViewport"
        role="img"
        aria-label={`${positions.length} posiciones operativas visibles`}
      >
        {positions.map(({ item, x, y }) => (
          <span
            key={item.order_id}
            className="opsPositionPoint"
            style={{ left: `${x}%`, top: `${y}%` }}
            aria-hidden="true"
          >
            {item.assignment.driver_reference}
          </span>
        ))}
      </div>
      <ul className="opsPositionList">
        {positions.map(({ item }) => (
          <li key={item.order_id}>
            <strong>{item.assignment.driver_reference}</strong>
            <span>
              Capturada{" "}
              <time dateTime={item.latest_driver_location.captured_at}>
                {formatMazatlanTime(item.latest_driver_location.captured_at)}
              </time>
            </span>
            <span>
              Precisión aproximada:{" "}
              {Math.round(item.latest_driver_location.accuracy_m)} m
            </span>
          </li>
        ))}
      </ul>
      {positions.length === 0 && <p>No hay posiciones disponibles.</p>}
    </section>
  );
}

function normalize(value: number, all: readonly number[]): number {
  const minimum = Math.min(...all);
  const maximum = Math.max(...all);
  if (minimum === maximum) return 50;
  return 8 + ((value - minimum) / (maximum - minimum)) * 84;
}
