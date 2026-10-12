"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { Field } from "../../components/ui/field";
import type { CreateRouteInput } from "../api/routes-api";
import { vehicleLabel } from "../contracts/assignable-driver";
import { cityLabel } from "../contracts/locations";
import { organizationDriverStatusLabels } from "../contracts/organization-driver";
import type {
  CreateRouteFormController,
  CreateRouteFormState,
  RouteFormList,
} from "../state/create-route-form-controller";

/**
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: "Crear ruta" in Rutas picks from lists instead of asking for pasted
 * internal ids - the ACTIVE drivers by DRV- reference, vehicle and status (listOrganizationDrivers), the cities by
 * name and state (listCities) and, once a city is chosen, its ACTIVE service areas or "Toda la ciudad"
 * (listServiceAreas). The date stays a date input. The form is sent only with a driver and a city; createRoute itself
 * is unchanged. Ids are option values only, never text.
 */
export function CreateRouteForm({
  form,
  controller,
  mutating,
  onCreate,
}: {
  readonly form: CreateRouteFormState;
  readonly controller: Pick<
    CreateRouteFormController,
    "chooseDriver" | "chooseCity" | "chooseArea" | "loadDrivers" | "loadCities" | "loadAreas" | "choice"
  >;
  readonly mutating: boolean;
  readonly onCreate: (input: CreateRouteInput) => void;
}) {
  const drivers = form.drivers.kind === "ready" ? form.drivers.items : [];
  const cities = form.cities.kind === "ready" ? form.cities.items : [];
  const areas = form.areas.kind === "ready" ? form.areas.items : [];
  const ready = form.driverId !== null && form.cityId !== null;
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const choice = controller.choice();
        if (choice === null) return;
        const scheduledFor = String(new FormData(event.currentTarget).get("scheduledFor") ?? "").trim() || null;
        onCreate({ ...choice, scheduledFor });
      }}
    >
      <Field
        label="Repartidor (flota propia)"
        hint={listHint(form.drivers, {
          loading: "Cargando repartidores…",
          empty: "Tu flota no tiene repartidores activos; la ruta necesita uno.",
          ready: form.driversTruncated
            ? "Hay más repartidores de los que esta lista muestra; algunos no aparecen."
            : "Solo repartidores activos de tu flota, por referencia.",
        })}
        error={listError(form.drivers, () => void controller.loadDrivers())}
      >
        {(control) => (
          <select
            {...control}
            name="driver"
            required
            value={form.driverId ?? ""}
            disabled={drivers.length === 0}
            onChange={(event) => controller.chooseDriver(event.currentTarget.value)}
          >
            <option value="" disabled>
              Elige un repartidor
            </option>
            {drivers.map((driver) => (
              <option key={driver.driver_id} value={driver.driver_id}>
                {driver.driver_reference} · {vehicleLabel(driver.vehicle_type)} ·{" "}
                {organizationDriverStatusLabels[driver.status]}
              </option>
            ))}
          </select>
        )}
      </Field>
      <Field
        label="Ciudad"
        hint={listHint(form.cities, {
          loading: "Cargando ciudades…",
          empty: "No hay ciudades disponibles.",
          ready: "La ciudad de la ruta, por nombre y estado.",
        })}
        error={listError(form.cities, () => void controller.loadCities())}
      >
        {(control) => (
          <select
            {...control}
            name="city"
            required
            value={form.cityId ?? ""}
            disabled={cities.length === 0}
            onChange={(event) => void controller.chooseCity(event.currentTarget.value)}
          >
            <option value="" disabled>
              Elige una ciudad
            </option>
            {cities.map((city) => (
              <option key={city.id} value={city.id}>
                {cityLabel(city)}
              </option>
            ))}
          </select>
        )}
      </Field>
      <Field
        label="Área de servicio (opcional)"
        hint={
          form.cityId === null
            ? "Elige primero la ciudad."
            : listHint(form.areas, {
                loading: "Cargando áreas de servicio…",
                empty: "Esta ciudad no tiene áreas de servicio activas; la ruta cubre toda la ciudad.",
                ready: "Déjala en «Toda la ciudad» si la ruta cubre toda la ciudad.",
              })
        }
        error={listError(form.areas, () => void controller.loadAreas())}
      >
        {(control) => (
          <select
            {...control}
            name="serviceArea"
            value={form.serviceAreaId ?? ""}
            disabled={areas.length === 0}
            onChange={(event) => controller.chooseArea(event.currentTarget.value)}
          >
            <option value="">Toda la ciudad</option>
            {areas.map((area) => (
              <option key={area.id} value={area.id}>
                {area.name}
              </option>
            ))}
          </select>
        )}
      </Field>
      <label>Fecha<input name="scheduledFor" type="date" /></label>
      <button className="btn btnPrimary" type="submit" disabled={mutating || !ready}>
        Crear ruta (borrador)
      </button>
    </form>
  );
}

/** The hint under a selector: loading, empty or what the list holds; nothing while it failed (the error says it). */
function listHint<T>(
  list: RouteFormList<T>,
  texts: { readonly loading: string; readonly empty: string; readonly ready: string },
): string | undefined {
  if (list.kind === "loading") return texts.loading;
  if (list.kind === "ready") return list.items.length === 0 ? texts.empty : texts.ready;
  return undefined;
}

/** A list that could not be read: its es-MX message, "Intentar de nuevo" and the MFA step-up when only that is missing. */
function listError<T>(list: RouteFormList<T>, retry: () => void): ReactNode {
  if (list.kind !== "failed") return undefined;
  return (
    <>
      {list.message}{" "}
      {list.stepUpHref !== null && (
        <Link className="btn btnPrimary" href={list.stepUpHref}>
          Verificar identidad
        </Link>
      )}{" "}
      <button type="button" className="btnLink" onClick={retry}>
        Intentar de nuevo
      </button>
    </>
  );
}
