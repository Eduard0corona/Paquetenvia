"use client";

import { useMemo } from "react";
import {
  type OperationsDashboardOrder,
  type OperationsDashboardFilters,
  type OperationsServiceType,
  type OrderStatus,
} from "../contracts/operations-dashboard";
import { dateTimeLocalToUtc, utcToDateTimeLocal } from "../contracts/filter-datetime";
import { orderStatusLabels } from "../contracts/operations-formatters";
import { orderStatusGroupIds, orderStatusGroups, statusesInGroup } from "../contracts/status-groups";

interface Props {
  readonly filters: OperationsDashboardFilters;
  readonly items: readonly OperationsDashboardOrder[];
  readonly disabled: boolean;
  readonly onChange: (filters: OperationsDashboardFilters) => void;
}

export function OperationsFilters({
  filters,
  items,
  disabled,
  onChange,
}: Props) {
  const options = useMemo(() => projectionOptions(items), [items]);
  return (
    <fieldset className="opsFilters" disabled={disabled}>
      <legend>Filtros</legend>
      <label>
        Estado
        <select
          value={filters.status ?? ""}
          onChange={(event) =>
            onChange({
              ...filters,
              status: (event.target.value || undefined) as
                | OrderStatus
                | undefined,
              cursor: undefined,
            })
          }
        >
          <option value="">Todos</option>
          {orderStatusGroupIds.map((group) => (
            <optgroup key={group} label={orderStatusGroups[group].label}>
              {statusesInGroup(group).map((status) => (
                <option key={status} value={status}>
                  {orderStatusLabels[status]}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
      </label>
      <ProjectionSelect
        label="Zona de entrega"
        value={filters.deliveryZoneId}
        options={options.zones}
        onChange={(value) =>
          onChange({ ...filters, deliveryZoneId: value, cursor: undefined })
        }
      />
      <ProjectionSelect
        label="Cliente"
        value={filters.clientAccountId}
        options={options.clients}
        onChange={(value) =>
          onChange({ ...filters, clientAccountId: value, cursor: undefined })
        }
      />
      <ProjectionSelect
        label="Dueño"
        value={filters.ownerOrganizationId}
        options={options.owners}
        onChange={(value) =>
          onChange({ ...filters, ownerOrganizationId: value, cursor: undefined })
        }
      />
      <ProjectionSelect
        label="Opera"
        value={filters.operatorOrganizationId}
        options={options.operators}
        onChange={(value) =>
          onChange({
            ...filters,
            operatorOrganizationId: value,
            cursor: undefined,
          })
        }
      />
      <label>
        Tipo de servicio
        <select
          value={filters.serviceType ?? ""}
          onChange={(event) =>
            onChange({
              ...filters,
              serviceType: (event.target.value || undefined) as
                | OperationsServiceType
                | undefined,
              cursor: undefined,
            })
          }
        >
          <option value="">Todos</option>
          <option value="SAME_DAY">Mismo día</option>
          <option value="URGENT">Urgente</option>
          <option value="SCHEDULED_ROUTE">Ruta programada</option>
        </select>
      </label>
      <label>
        Fecha de creación desde (hora de Mazatlán)
        <input
          type="datetime-local"
          value={utcToDateTimeLocal(filters.createdFrom)}
          onChange={(event) =>
            onChange({
              ...filters,
              createdFrom: dateTimeLocalToUtc(event.target.value),
              cursor: undefined,
            })
          }
        />
      </label>
      <label>
        Fecha de creación hasta (hora de Mazatlán)
        <input
          type="datetime-local"
          value={utcToDateTimeLocal(filters.createdTo)}
          onChange={(event) =>
            onChange({
              ...filters,
              createdTo: dateTimeLocalToUtc(event.target.value),
              cursor: undefined,
            })
          }
        />
      </label>
      <label className="opsCheckbox">
        <input
          type="checkbox"
          checked={filters.unassigned ?? false}
          onChange={(event) =>
            onChange({
              ...filters,
              unassigned: event.target.checked || undefined,
              cursor: undefined,
            })
          }
        />
        Solo sin asignar
      </label>
      <button
        type="button"
        className="btn btnSecondary"
        onClick={() => {
          onChange({});
        }}
      >
        Limpiar filtros
      </button>
    </fieldset>
  );
}

function ProjectionSelect({
  label,
  value,
  options,
  onChange,
}: {
  readonly label: string;
  readonly value?: string;
  readonly options: readonly ProjectionOption[];
  readonly onChange: (value: string | undefined) => void;
}) {
  return (
    <label>
      {label}
      <select
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value || undefined)}
      >
        <option value="">Todos</option>
        {options.map((option) => (
          <option key={option.id} value={option.id}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

interface ProjectionOption {
  readonly id: string;
  readonly label: string;
}

function projectionOptions(items: readonly OperationsDashboardOrder[]) {
  return {
    zones: unique(
      items.flatMap((item) =>
        item.delivery_zone === null
          ? []
          : [{
              id: item.delivery_zone.operating_zone_id,
              label: item.delivery_zone.name,
            }],
      ),
    ),
    clients: unique(
      items.flatMap((item) =>
        item.client === null
          ? []
          : [{
              id: item.client.client_account_id,
              label: item.client.display_name,
            }],
      ),
    ),
    owners: unique(
      items.map((item) => ({
        id: item.owner.organization_id,
        label: item.owner.display_name,
      })),
    ),
    operators: unique(
      items.flatMap((item) =>
        item.operator === null
          ? []
          : [{
              id: item.operator.organization_id,
              label: item.operator.display_name,
            }],
      ),
    ),
  };
}

function unique(options: readonly ProjectionOption[]): readonly ProjectionOption[] {
  return [...new Map(options.map((option) => [option.id, option])).values()]
    .sort((left, right) => left.label.localeCompare(right.label, "es-MX"));
}
