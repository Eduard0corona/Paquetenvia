import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const component = readFileSync(
  resolve(process.cwd(), "src/driver/components/driver-stops-experience.tsx"),
  "utf8",
);
const listPage = readFileSync(
  resolve(process.cwd(), "src/app/driver/stops/page.tsx"),
  "utf8",
);
const detailPage = readFileSync(
  resolve(process.cwd(), "src/app/driver/stops/[id]/page.tsx"),
  "utf8",
);

describe("driver stops generic shell", () => {
  it("derives the route from the browser pathname", () => {
    expect(component).toContain(
      "parseDriverStopsPathname(window.location.pathname)",
    );
    expect(component).not.toContain("detailOrderId");
  });

  it("uses document anchors without Next prefetch navigation", () => {
    expect(component).not.toContain('from "next/link"');
    expect(component).toContain("<a");
    expect(component).not.toContain("<Link");
    expect(component).toContain(
      'href={`/driver/stops/${stop.order_id}`}',
    );
    expect(component).toContain('href="/driver/stops"');
  });

  it("renders the same prop-free experience from list and detail pages", () => {
    expect(listPage).toContain("<DriverStopsExperience />");
    expect(detailPage).toContain("<DriverStopsExperience />");
    expect(detailPage).not.toContain("params");
  });

  it("does not render the internal route UUID as detail text", () => {
    expect(component).not.toContain(">{orderId}<");
  });
});
