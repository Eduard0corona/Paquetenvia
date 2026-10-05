import type { ReactNode } from "react";
import type { OrderStatus } from "../../operations/contracts/operations-dashboard";
import { orderStatusLabels } from "../../operations/contracts/operations-formatters";
import {
  orderStatusGroups,
  statusGroup,
  type OrderStatusGroupId,
  type OrderStatusGroupTone,
} from "../../operations/contracts/status-groups";

const toneClass: Readonly<Record<OrderStatusGroupTone, string>> = {
  muted: "statusToneMuted",
  info: "statusToneInfo",
  accent: "statusToneAccent",
  warn: "statusToneWarn",
  ok: "statusToneOk",
};

/**
 * One of the five status groups as a chip: token color + dot + text, so color is
 * never the only signal (UI-001 phase 2A). `children` follows the label, e.g. a count.
 */
export function StatusGroupChip({
  group,
  children,
}: {
  readonly group: OrderStatusGroupId;
  readonly children?: ReactNode;
}) {
  const { label, tone } = orderStatusGroups[group];
  return (
    <span className={`statusChip ${toneClass[tone]}`}>
      <span className="statusDot" aria-hidden="true" />
      <span className="statusChipText">{label}</span>
      {children}
    </span>
  );
}

/**
 * An order status: the group chip followed by the exact AI-04 status label. Inside a
 * container that already names the group (a dashboard group column), `showGroup={false}`
 * keeps the dot in the group color next to the exact label.
 */
export function StatusBadge({
  status,
  showGroup = true,
}: {
  readonly status: OrderStatus;
  readonly showGroup?: boolean;
}) {
  const group = statusGroup(status);
  const label = <span className="statusBadgeLabel">{orderStatusLabels[status]}</span>;
  if (!showGroup) {
    return (
      <span className={`statusBadge ${toneClass[orderStatusGroups[group].tone]}`}>
        <span className="statusDot" aria-hidden="true" />
        {label}
      </span>
    );
  }
  return (
    <span className="statusBadge">
      <StatusGroupChip group={group} />
      <span className="srOnly">: </span>
      {label}
    </span>
  );
}
