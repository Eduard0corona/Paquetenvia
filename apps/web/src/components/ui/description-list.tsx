import type { ReactNode } from "react";

export interface DescriptionItem {
  readonly label: ReactNode;
  readonly value: ReactNode;
  /** Defaults to the label when it is plain text. */
  readonly key?: string;
}

const variantClass = {
  rows: "descList",
  money: "descList descListMoney",
  grid: "descList descListGrid",
} as const;

/**
 * Every label/value pair of the app (order cards, order detail, money breakdowns, COD and
 * settlement details) renders through this one dl. `money` right-aligns tabular values,
 * `grid` lays the pairs out as tiles. Falsy entries are skipped so optional rows read inline.
 */
export function DescriptionList({
  items,
  variant = "rows",
  label,
}: {
  readonly items: readonly (DescriptionItem | false | null | undefined)[];
  readonly variant?: keyof typeof variantClass;
  /** Accessible name when the list stands on its own. */
  readonly label?: string;
}) {
  return (
    <dl className={variantClass[variant]} aria-label={label}>
      {items.map((item, index) =>
        item ? (
          <div key={item.key ?? (typeof item.label === "string" ? item.label : index)}>
            <dt>{item.label}</dt>
            <dd>{item.value}</dd>
          </div>
        ) : null,
      )}
    </dl>
  );
}
